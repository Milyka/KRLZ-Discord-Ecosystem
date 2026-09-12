using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class SocialRepository(DbConnectionFactory dbFactory)
{
    public async Task<MarriageEntity?> GetMarriageAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MarriageEntity>(@"
            SELECT * FROM marriages
            WHERE guild_id = @GuildId AND (user1_id = @UserId OR user2_id = @UserId);",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<MarriageEntity?> GetMarriageByIdAsync(long marriageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MarriageEntity>(@"
            SELECT * FROM marriages
            WHERE id = @Id;",
            new { Id = marriageId });
    }

    public async Task<bool> CreateMarriageAsync(ulong guildId, ulong user1Id, ulong user2Id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM marriages
            WHERE guild_id = @GuildId
              AND (user1_id IN (@U1, @U2) OR user2_id IN (@U1, @U2));",
            new { GuildId = guildId, U1 = user1Id, U2 = user2Id });

        if (existing > 0)
            return false;

        await conn.ExecuteAsync(@"
            INSERT INTO marriages (guild_id, user1_id, user2_id, married_at)
            VALUES (@GuildId, @U1, @U2, @MarriedAt);",
            new { GuildId = guildId, U1 = user1Id, U2 = user2Id, MarriedAt = DateTimeOffset.UtcNow });
        return true;
    }

    public async Task<bool> DivorceAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            DELETE FROM marriages
            WHERE guild_id = @GuildId AND (user1_id = @UserId OR user2_id = @UserId);",
            new { GuildId = guildId, UserId = userId });
        return rows > 0;
    }

    public async Task<long> AddReminderAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        string message,
        DateTimeOffset triggerAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO reminders (guild_id, user_id, channel_id, message, trigger_at, completed, created_at)
            VALUES (@GuildId, @UserId, @ChannelId, @Message, @TriggerAt, 0, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                Message = message,
                TriggerAt = triggerAt,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<IReadOnlyList<ReminderEntity>> GetDueRemindersAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReminderEntity>(@"
            SELECT * FROM reminders
            WHERE completed = 0 AND trigger_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task MarkReminderCompletedAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE reminders SET completed = 1 WHERE id = @Id;", new { Id = id });
    }

    public async Task RecordVoiceSessionAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        long durationSeconds,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO voice_sessions (guild_id, user_id, channel_id, started_at, ended_at, duration_seconds)
            VALUES (@GuildId, @UserId, @ChannelId, @StartedAt, @EndedAt, @DurationSeconds);",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                StartedAt = startedAt,
                EndedAt = endedAt,
                DurationSeconds = durationSeconds
            });
    }
}
