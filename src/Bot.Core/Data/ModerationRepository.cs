using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class ModerationRepository(DbConnectionFactory dbFactory)
{
    public async Task<long> AddWarnAsync(ulong guildId, ulong userId, ulong moderatorId, string reason, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO warns (guild_id, user_id, moderator_id, reason, active, created_at)
            VALUES (@GuildId, @UserId, @ModeratorId, @Reason, 1, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ModeratorId = moderatorId,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<bool> DeactivateWarnAsync(ulong guildId, long warnId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(@"
            UPDATE warns
            SET active = 0
            WHERE id = @Id AND guild_id = @GuildId AND active = 1;",
            new { Id = warnId, GuildId = guildId });
        return affected > 0;
    }

    public async Task<IReadOnlyList<WarnEntity>> GetActiveWarnsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var list = await conn.QueryAsync<WarnEntity>(@"
            SELECT * FROM warns
            WHERE guild_id = @GuildId AND user_id = @UserId AND active = 1
            ORDER BY id DESC;",
            new { GuildId = guildId, UserId = userId });
        return list.ToList();
    }

    public async Task<int> GetActiveWarnCountAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM warns
            WHERE guild_id = @GuildId AND user_id = @UserId AND active = 1;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<long> AddPunishmentAsync(
        ulong guildId,
        ulong userId,
        ulong moderatorId,
        string type,
        string reason,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO punishments (guild_id, user_id, moderator_id, type, reason, expires_at, active, created_at)
            VALUES (@GuildId, @UserId, @ModeratorId, @Type, @Reason, @ExpiresAt, 1, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ModeratorId = moderatorId,
                Type = type,
                Reason = reason,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task DeactivatePunishmentsByTypeAsync(ulong guildId, ulong userId, string type, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE punishments
            SET active = 0
            WHERE guild_id = @GuildId AND user_id = @UserId AND type = @Type AND active = 1;",
            new { GuildId = guildId, UserId = userId, Type = type });
    }

    public async Task DeactivatePunishmentByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE punishments SET active = 0 WHERE id = @Id;", new { Id = id });
    }

    public async Task<IReadOnlyList<PunishmentEntity>> GetExpiredActivePunishmentsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PunishmentEntity>(@"
            SELECT * FROM punishments
            WHERE active = 1 AND expires_at IS NOT NULL AND expires_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PunishmentEntity>> GetUserHistoryAsync(ulong guildId, ulong userId, int limit = 15, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PunishmentEntity>(@"
            SELECT * FROM punishments
            WHERE guild_id = @GuildId AND user_id = @UserId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, UserId = userId, Limit = limit });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PunishmentEntity>> GetRecentModlogAsync(ulong guildId, int limit = 15, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PunishmentEntity>(@"
            SELECT * FROM punishments
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }

    public async Task SetVoiceBanAsync(ulong guildId, ulong userId, ulong moderatorId, string reason, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO voice_bans (guild_id, user_id, moderator_id, reason, expires_at, created_at)
            VALUES (@GuildId, @UserId, @ModeratorId, @Reason, @ExpiresAt, @CreatedAt)
            ON CONFLICT(guild_id, user_id) DO UPDATE SET
                moderator_id = excluded.moderator_id,
                reason = excluded.reason,
                expires_at = excluded.expires_at,
                created_at = excluded.created_at;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ModeratorId = moderatorId,
                Reason = reason,
                ExpiresAt = expiresAt,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task RemoveVoiceBanAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM voice_bans WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<VoiceBanEntity?> GetVoiceBanAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VoiceBanEntity>(
            "SELECT * FROM voice_bans WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }
}
