using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class CommunityEventsRepository(DbConnectionFactory dbFactory)
{
    public async Task<long> CreateReactionRolePanelAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        string panelTitle,
        string mode,
        string componentType,
        string mappingsJson,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO reaction_roles (
                guild_id, channel_id, message_id, panel_title, mode, component_type, mappings_json, created_at
            ) VALUES (
                @GuildId, @ChannelId, @MessageId, @PanelTitle, @Mode, @ComponentType, @MappingsJson, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                MessageId = messageId,
                PanelTitle = panelTitle,
                Mode = mode,
                ComponentType = componentType,
                MappingsJson = mappingsJson,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task<ReactionRoleEntity?> GetReactionRoleByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ReactionRoleEntity>(
            "SELECT * FROM reaction_roles WHERE id = @Id;",
            new { Id = id });
    }

    public async Task UpdateReactionRoleMessageIdAsync(long id, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE reaction_roles SET message_id = @MessageId WHERE id = @Id;",
            new { Id = id, MessageId = messageId });
    }

    public async Task<long> CreatePollV2Async(
        ulong guildId,
        ulong channelId,
        ulong creatorId,
        string question,
        string optionsJson,
        bool isAnonymous,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO polls_v2 (
                guild_id, channel_id, message_id, creator_id, question,
                options_json, is_anonymous, status, ends_at, created_at
            ) VALUES (
                @GuildId, @ChannelId, 0, @CreatorId, @Question,
                @OptionsJson, @IsAnonymous, 'active', @EndsAt, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                CreatorId = creatorId,
                Question = question,
                OptionsJson = optionsJson,
                IsAnonymous = isAnonymous ? 1 : 0,
                EndsAt = endsAt,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task UpdatePollMessageIdAsync(long pollId, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE polls_v2 SET message_id = @MessageId WHERE id = @Id;", new { Id = pollId, MessageId = messageId });
    }

    public async Task<PollV2Entity?> GetPollByIdAsync(long pollId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PollV2Entity>("SELECT * FROM polls_v2 WHERE id = @Id;", new { Id = pollId });
    }

    public async Task CastPollVoteAsync(long pollId, ulong guildId, ulong userId, int optionIndex, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO poll_v2_votes (poll_id, guild_id, user_id, option_index, voted_at)
            VALUES (@PollId, @GuildId, @UserId, @OptionIndex, @Now)
            ON CONFLICT(poll_id, user_id) DO UPDATE SET
                option_index = excluded.option_index,
                voted_at = excluded.voted_at;",
            new { PollId = pollId, GuildId = guildId, UserId = userId, OptionIndex = optionIndex, Now = DateTimeOffset.UtcNow });
    }

    public async Task<IReadOnlyList<PollV2VoteEntity>> GetPollVotesAsync(long pollId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PollV2VoteEntity>(
            "SELECT * FROM poll_v2_votes WHERE poll_id = @PollId;",
            new { PollId = pollId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PollV2Entity>> GetDueActivePollsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PollV2Entity>(
            "SELECT * FROM polls_v2 WHERE status = 'active' AND ends_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task<bool> MarkPollClosedAsync(long pollId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "UPDATE polls_v2 SET status = 'closed' WHERE id = @Id AND status = 'active';",
            new { Id = pollId });
        return rows > 0;
    }

    public async Task<long> CreateGiveawayAsync(
        ulong guildId,
        ulong channelId,
        ulong hostId,
        string prize,
        int winnerCount,
        ulong? requiredRoleId,
        int minLevel,
        long minBalance,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO giveaways (
                guild_id, channel_id, message_id, host_id, prize, winner_count,
                required_role_id, min_level, min_balance, status, ends_at, created_at
            ) VALUES (
                @GuildId, @ChannelId, 0, @HostId, @Prize, @WinnerCount,
                @RequiredRoleId, @MinLevel, @MinBalance, 'active', @EndsAt, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                HostId = hostId,
                Prize = prize,
                WinnerCount = winnerCount,
                RequiredRoleId = requiredRoleId,
                MinLevel = minLevel,
                MinBalance = minBalance,
                EndsAt = endsAt,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task UpdateGiveawayMessageIdAsync(long giveawayId, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE giveaways SET message_id = @MessageId WHERE id = @Id;", new { Id = giveawayId, MessageId = messageId });
    }

    public async Task<GiveawayEntity?> GetGiveawayByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<GiveawayEntity>("SELECT * FROM giveaways WHERE id = @Id;", new { Id = id });
    }

    public async Task<(bool Added, int TotalEntries)> TryEnterGiveawayAsync(long giveawayId, ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            INSERT INTO giveaway_entries (giveaway_id, guild_id, user_id, entered_at)
            VALUES (@GiveawayId, @GuildId, @UserId, @Now)
            ON CONFLICT(giveaway_id, user_id) DO NOTHING;",
            new { GiveawayId = giveawayId, GuildId = guildId, UserId = userId, Now = DateTimeOffset.UtcNow });

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM giveaway_entries WHERE giveaway_id = @GiveawayId;",
            new { GiveawayId = giveawayId });

        return (rows > 0, total);
    }

    public async Task<IReadOnlyList<ulong>> GetGiveawayParticipantIdsAsync(long giveawayId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ulong>(
            "SELECT user_id FROM giveaway_entries WHERE giveaway_id = @GiveawayId;",
            new { GiveawayId = giveawayId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ulong>> GetGiveawayWinnerIdsAsync(long giveawayId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ulong>(
            "SELECT user_id FROM giveaway_winners WHERE giveaway_id = @GiveawayId;",
            new { GiveawayId = giveawayId });
        return rows.ToList();
    }

    public async Task RecordGiveawayWinnersAsync(
        long giveawayId,
        ulong guildId,
        IEnumerable<ulong> winnerIds,
        bool isReroll,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var uid in winnerIds)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO giveaway_winners (giveaway_id, guild_id, user_id, is_reroll, selected_at)
                VALUES (@GiveawayId, @GuildId, @UserId, @IsReroll, @Now);",
                new { GiveawayId = giveawayId, GuildId = guildId, UserId = uid, IsReroll = isReroll ? 1 : 0, Now = now },
                transaction: tx);
        }

        await conn.ExecuteAsync(
            "UPDATE giveaways SET status = 'finished' WHERE id = @GiveawayId;",
            new { GiveawayId = giveawayId },
            transaction: tx);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<GiveawayEntity>> GetDueActiveGiveawaysAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<GiveawayEntity>(
            "SELECT * FROM giveaways WHERE status = 'active' AND ends_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task<long> LogDatabaseBackupAsync(
        ulong guildId,
        string fileName,
        string filePath,
        long sizeBytes,
        ulong triggeredById,
        bool sentToChannel,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO db_backups_log (
                guild_id, file_name, file_path, size_bytes, triggered_by_id, sent_to_channel, created_at
            ) VALUES (
                @GuildId, @FileName, @FilePath, @SizeBytes, @TriggeredById, @Sent, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                FileName = fileName,
                FilePath = filePath,
                SizeBytes = sizeBytes,
                TriggeredById = triggeredById,
                Sent = sentToChannel ? 1 : 0,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task<IReadOnlyList<DbBackupLogEntity>> GetDatabaseBackupsAsync(ulong guildId, int limit = 15, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DbBackupLogEntity>(@"
            SELECT * FROM db_backups_log
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }
}
