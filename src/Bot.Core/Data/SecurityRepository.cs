using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class SecurityRepository(DbConnectionFactory dbFactory)
{
    public async Task<bool> IsWhitelistedAsync(ulong guildId, ulong targetId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM security_whitelist
            WHERE guild_id = @GuildId AND target_id = @TargetId;",
            new { GuildId = guildId, TargetId = targetId });
        return count > 0;
    }

    public async Task AddWhitelistAsync(ulong guildId, ulong targetId, string targetType, ulong addedById, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO security_whitelist (guild_id, target_id, target_type, added_by_id, created_at)
            VALUES (@GuildId, @TargetId, @TargetType, @AddedById, @CreatedAt)
            ON CONFLICT(guild_id, target_id) DO UPDATE SET
                target_type = excluded.target_type,
                added_by_id = excluded.added_by_id,
                created_at = excluded.created_at;",
            new
            {
                GuildId = guildId,
                TargetId = targetId,
                TargetType = targetType,
                AddedById = addedById,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<bool> RemoveWhitelistAsync(ulong guildId, ulong targetId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            DELETE FROM security_whitelist
            WHERE guild_id = @GuildId AND target_id = @TargetId;",
            new { GuildId = guildId, TargetId = targetId });
        return rows > 0;
    }

    public async Task<IReadOnlyList<SecurityWhitelistEntity>> GetWhitelistAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SecurityWhitelistEntity>(@"
            SELECT * FROM security_whitelist
            WHERE guild_id = @GuildId
            ORDER BY created_at DESC;",
            new { GuildId = guildId });
        return rows.ToList();
    }

    public async Task LogSecurityEventAsync(
        ulong guildId,
        ulong offenderId,
        string eventType,
        string details,
        string actionTaken,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO security_events (guild_id, offender_id, event_type, details, action_taken, created_at)
            VALUES (@GuildId, @OffenderId, @EventType, @Details, @ActionTaken, @CreatedAt);",
            new
            {
                GuildId = guildId,
                OffenderId = offenderId,
                EventType = eventType,
                Details = details,
                ActionTaken = actionTaken,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<IReadOnlyList<SecurityEventEntity>> GetRecentEventsAsync(ulong guildId, int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SecurityEventEntity>(@"
            SELECT * FROM security_events
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }

    public async Task<long> SaveBackupAsync(
        ulong guildId,
        string snapshotName,
        ulong createdById,
        string payloadJson,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO guild_backups (guild_id, snapshot_name, created_by_id, payload_json, created_at)
            VALUES (@GuildId, @SnapshotName, @CreatedById, @PayloadJson, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                SnapshotName = snapshotName,
                CreatedById = createdById,
                PayloadJson = payloadJson,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<GuildBackupEntity?> GetBackupByIdAsync(ulong guildId, long backupId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<GuildBackupEntity>(@"
            SELECT * FROM guild_backups
            WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = backupId });
    }

    public async Task<GuildBackupEntity?> GetLatestBackupAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<GuildBackupEntity>(@"
            SELECT * FROM guild_backups
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId });
    }

    public async Task<IReadOnlyList<GuildBackupEntity>> GetBackupsAsync(ulong guildId, int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GuildBackupEntity>(@"
            SELECT * FROM guild_backups
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }
}
