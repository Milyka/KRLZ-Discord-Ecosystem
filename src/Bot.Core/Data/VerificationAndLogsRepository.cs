using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class VerificationAndLogsRepository(DbConnectionFactory dbFactory)
{
    public async Task<long> CreateApplicationAsync(ulong guildId, ulong userId, string answersJson, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO verification_applications (guild_id, user_id, answers_json, status, created_at)
            VALUES (@GuildId, @UserId, @AnswersJson, 'pending', @Now);
            SELECT last_insert_rowid();",
            new { GuildId = guildId, UserId = userId, AnswersJson = answersJson, Now = DateTimeOffset.UtcNow });
    }

    public async Task<VerificationApplicationEntity?> GetApplicationByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VerificationApplicationEntity>(
            "SELECT * FROM verification_applications WHERE id = @Id;",
            new { Id = id });
    }

    public async Task<VerificationApplicationEntity?> GetPendingApplicationByUserIdAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VerificationApplicationEntity>(
            "SELECT * FROM verification_applications WHERE guild_id = @GuildId AND user_id = @UserId AND status = 'pending' ORDER BY id DESC LIMIT 1;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<bool> ReviewApplicationAsync(long id, ulong reviewerId, string newStatus, string reason, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            UPDATE verification_applications
            SET status = @Status,
                reviewer_id = @ReviewerId,
                review_reason = @Reason,
                reviewed_at = @Now
            WHERE id = @Id AND status = 'pending';",
            new { Id = id, Status = newStatus, ReviewerId = reviewerId, Reason = reason, Now = DateTimeOffset.UtcNow });
        return rows > 0;
    }

    public async Task UpsertMessageLogAsync(
        ulong messageId,
        ulong guildId,
        ulong channelId,
        ulong authorId,
        string authorName,
        string content,
        string? attachments,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync(@"
            INSERT INTO message_logs (
                message_id, guild_id, channel_id, author_id, author_name,
                content, attachments, is_deleted, edited_content, created_at, updated_at
            ) VALUES (
                @MessageId, @GuildId, @ChannelId, @AuthorId, @AuthorName,
                @Content, @Attachments, 0, NULL, @Now, @Now
            )
            ON CONFLICT(message_id) DO UPDATE SET
                edited_content = @Content,
                attachments = COALESCE(@Attachments, message_logs.attachments),
                updated_at = @Now;",
            new
            {
                MessageId = messageId,
                GuildId = guildId,
                ChannelId = channelId,
                AuthorId = authorId,
                AuthorName = authorName,
                Content = content,
                Attachments = attachments,
                Now = now
            });
    }

    public async Task<MessageLogEntity?> GetMessageLogAsync(ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MessageLogEntity>(
            "SELECT * FROM message_logs WHERE message_id = @MessageId;",
            new { MessageId = messageId });
    }

    public async Task MarkMessageDeletedAsync(ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE message_logs SET is_deleted = 1, updated_at = @Now WHERE message_id = @MessageId;",
            new { MessageId = messageId, Now = DateTimeOffset.UtcNow });
    }

    public async Task AddNicknameHistoryAsync(
        ulong guildId,
        ulong userId,
        string? oldNickname,
        string? newNickname,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO nickname_history (guild_id, user_id, old_nickname, new_nickname, changed_at)
            VALUES (@GuildId, @UserId, @OldNick, @NewNick, @Now);",
            new { GuildId = guildId, UserId = userId, OldNick = oldNickname, NewNick = newNickname, Now = DateTimeOffset.UtcNow });
    }

    public async Task<IReadOnlyList<NicknameHistoryEntity>> GetNicknameHistoryAsync(
        ulong guildId,
        ulong userId,
        int limit = 15,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<NicknameHistoryEntity>(@"
            SELECT * FROM nickname_history
            WHERE guild_id = @GuildId AND user_id = @UserId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, UserId = userId, Limit = limit });
        return rows.ToList();
    }

    public async Task PurgeOldLogsAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM message_logs WHERE created_at <= @Cutoff;",
            new { Cutoff = cutoff });
    }
}
