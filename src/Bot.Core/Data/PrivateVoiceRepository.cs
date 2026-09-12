using Bot.Infrastructure;
using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class PrivateVoiceRepository(DbConnectionFactory dbFactory, SettingsProvider settingsProvider)
{
    public async Task<VoiceHubConfigEntity> GetOrCreateHubConfigAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<VoiceHubConfigEntity>(
            "SELECT * FROM voice_hub_config WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        if (existing is not null)
            return existing;

        var pv = settingsProvider.Current.PrivateVoice;
        var entity = new VoiceHubConfigEntity(
            GuildId: guildId,
            HubChannelId: pv.HubVoiceChannelId,
            CategoryId: pv.PrivateRoomsCategoryId,
            ControlChannelId: pv.ControlTextChannelId,
            NameTemplate: string.IsNullOrWhiteSpace(pv.RoomNameTemplate) ? "Комната {user}" : pv.RoomNameTemplate,
            DefaultLimit: 0,
            EmptyTimeoutSeconds: Math.Max(10, pv.EmptyRoomDeleteSeconds),
            UpdatedAt: DateTimeOffset.UtcNow);

        await UpsertHubConfigAsync(entity, ct);
        return entity;
    }

    public async Task UpsertHubConfigAsync(VoiceHubConfigEntity entity, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO voice_hub_config (
                guild_id, hub_channel_id, category_id, control_channel_id,
                name_template, default_limit, empty_timeout_seconds, updated_at
            ) VALUES (
                @GuildId, @HubChannelId, @CategoryId, @ControlChannelId,
                @NameTemplate, @DefaultLimit, @EmptyTimeoutSeconds, @UpdatedAt
            )
            ON CONFLICT(guild_id) DO UPDATE SET
                hub_channel_id = excluded.hub_channel_id,
                category_id = excluded.category_id,
                control_channel_id = excluded.control_channel_id,
                name_template = excluded.name_template,
                default_limit = excluded.default_limit,
                empty_timeout_seconds = excluded.empty_timeout_seconds,
                updated_at = excluded.updated_at;",
            entity);
    }

    public async Task<PrivateVoiceRoomEntity?> GetRoomByChannelAsync(ulong channelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PrivateVoiceRoomEntity>(
            "SELECT * FROM private_voice_rooms WHERE channel_id = @ChannelId;",
            new { ChannelId = channelId });
    }

    public async Task<PrivateVoiceRoomEntity?> GetRoomByOwnerAsync(ulong guildId, ulong ownerId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PrivateVoiceRoomEntity>(@"
            SELECT * FROM private_voice_rooms
            WHERE guild_id = @GuildId AND owner_id = @OwnerId
            ORDER BY created_at DESC
            LIMIT 1;",
            new { GuildId = guildId, OwnerId = ownerId });
    }

    public async Task<IReadOnlyList<PrivateVoiceRoomEntity>> GetAllRoomsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PrivateVoiceRoomEntity>("SELECT * FROM private_voice_rooms;");
        return rows.ToList();
    }

    public async Task CreateRoomAsync(PrivateVoiceRoomEntity room, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO private_voice_rooms (
                channel_id, guild_id, owner_id, room_name, user_limit,
                is_locked, is_hidden, banned_user_ids, empty_since, created_at
            ) VALUES (
                @ChannelId, @GuildId, @OwnerId, @RoomName, @UserLimit,
                @IsLocked, @IsHidden, @BannedUserIds, @EmptySince, @CreatedAt
            );",
            room);
    }

    public async Task UpdateRoomAsync(PrivateVoiceRoomEntity room, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE private_voice_rooms
            SET owner_id = @OwnerId,
                room_name = @RoomName,
                user_limit = @UserLimit,
                is_locked = @IsLocked,
                is_hidden = @IsHidden,
                banned_user_ids = @BannedUserIds,
                empty_since = @EmptySince
            WHERE channel_id = @ChannelId;",
            room);
    }

    public async Task SetEmptySinceAsync(ulong channelId, DateTimeOffset? emptySince, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE private_voice_rooms SET empty_since = @EmptySince WHERE channel_id = @ChannelId;",
            new { ChannelId = channelId, EmptySince = emptySince });
    }

    public async Task DeleteRoomAsync(ulong channelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM private_voice_rooms WHERE channel_id = @ChannelId;",
            new { ChannelId = channelId });
    }

    public async Task<PrivateVoicePresetEntity?> GetPresetAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PrivateVoicePresetEntity>(
            "SELECT * FROM private_voice_presets WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task UpsertPresetAsync(PrivateVoicePresetEntity preset, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO private_voice_presets (
                guild_id, user_id, room_name, user_limit,
                is_locked, is_hidden, trusted_user_ids, banned_user_ids, updated_at
            ) VALUES (
                @GuildId, @UserId, @RoomName, @UserLimit,
                @IsLocked, @IsHidden, @TrustedUserIds, @BannedUserIds, @UpdatedAt
            )
            ON CONFLICT(guild_id, user_id) DO UPDATE SET
                room_name = excluded.room_name,
                user_limit = excluded.user_limit,
                is_locked = excluded.is_locked,
                is_hidden = excluded.is_hidden,
                trusted_user_ids = excluded.trusted_user_ids,
                banned_user_ids = excluded.banned_user_ids,
                updated_at = excluded.updated_at;",
            preset);
    }
}
