using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class TicketRepository(DbConnectionFactory dbFactory)
{
    public async Task<int> GetOpenCountForUserAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM tickets
            WHERE guild_id = @GuildId AND owner_id = @UserId AND status = 'open';",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<int> GetNextTicketNumberAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var max = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(ticket_number) FROM tickets WHERE guild_id = @GuildId;",
            new { GuildId = guildId });
        return (max ?? 0) + 1;
    }

    public async Task<TicketEntity> CreateTicketAsync(
        ulong guildId,
        int ticketNumber,
        ulong channelId,
        ulong ownerId,
        string category,
        string subject,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO tickets (guild_id, ticket_number, channel_id, owner_id, category, subject, status, created_at)
            VALUES (@GuildId, @TicketNumber, @ChannelId, @OwnerId, @Category, @Subject, 'open', @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                TicketNumber = ticketNumber,
                ChannelId = channelId,
                OwnerId = ownerId,
                Category = category,
                Subject = subject,
                CreatedAt = now
            });

        return new TicketEntity(id, guildId, ticketNumber, channelId, ownerId, null, category, subject, "open", null, null, now, null);
    }

    public async Task<TicketEntity?> GetByChannelAsync(ulong channelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TicketEntity>(
            "SELECT * FROM tickets WHERE channel_id = @ChannelId;",
            new { ChannelId = channelId });
    }

    public async Task<TicketEntity?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TicketEntity>(
            "SELECT * FROM tickets WHERE id = @Id;",
            new { Id = id });
    }

    public async Task SetClaimedByAsync(long ticketId, ulong moderatorId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE tickets SET claimed_by_id = @ModeratorId WHERE id = @Id;",
            new { Id = ticketId, ModeratorId = moderatorId });
    }

    public async Task CloseTicketAsync(long ticketId, string closeReason, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE tickets
            SET status = 'closed',
                close_reason = @CloseReason,
                closed_at = @ClosedAt
            WHERE id = @Id;",
            new { Id = ticketId, CloseReason = closeReason, ClosedAt = DateTimeOffset.UtcNow });
    }

    public async Task SetRatingAsync(long ticketId, int rating, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE tickets SET rating = @Rating WHERE id = @Id;",
            new { Id = ticketId, Rating = rating });
    }

    public async Task AddMessageAsync(
        long ticketId,
        ulong authorId,
        string authorName,
        string content,
        string? attachments,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO ticket_messages (ticket_id, author_id, author_name, content, attachments, created_at)
            VALUES (@TicketId, @AuthorId, @AuthorName, @Content, @Attachments, @CreatedAt);",
            new
            {
                TicketId = ticketId,
                AuthorId = authorId,
                AuthorName = authorName,
                Content = content,
                Attachments = attachments,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<IReadOnlyList<TicketMessageEntity>> GetMessagesAsync(long ticketId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketMessageEntity>(@"
            SELECT * FROM ticket_messages
            WHERE ticket_id = @TicketId
            ORDER BY id ASC;",
            new { TicketId = ticketId });
        return rows.ToList();
    }
}
