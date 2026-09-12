using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class PartyGameRepository(DbConnectionFactory dbFactory)
{
    public async Task<PartyGameSessionEntity?> GetSessionByIdAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE id = @Id;",
            new { Id = sessionId });
    }

    public async Task<PartyGameSessionEntity?> GetActiveSessionInChannelAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(@"
            SELECT * FROM party_game_sessions
            WHERE guild_id = @GuildId
              AND channel_id = @ChannelId
              AND status IN ('lobby', 'active')
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId, ChannelId = channelId });
    }

    public async Task<IReadOnlyList<PartyGamePlayerEntity>> GetPlayersAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PartyGamePlayerEntity>(@"
            SELECT * FROM party_game_players
            WHERE session_id = @SessionId
            ORDER BY seat_index ASC, id ASC;",
            new { SessionId = sessionId });
        return rows.ToList();
    }

    public async Task<(bool Success, string Error, PartyGameSessionEntity? Session)> CreateLobbySessionAsync(
        ulong guildId,
        ulong channelId,
        ulong voiceChannelId,
        ulong hostId,
        string hostDisplayName,
        string gameType,
        long bet,
        string initialStateJson,
        TimeSpan lobbyDuration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lobbyDuration);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingInChannel = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM party_game_sessions
            WHERE guild_id = @GuildId AND channel_id = @ChannelId AND status IN ('lobby', 'active');",
            new { GuildId = guildId, ChannelId = channelId },
            transaction: tx);

        if (existingInChannel > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "В этом канале уже идёт сбор или активная партия! Завершите её или перейдите в другой канал.", null);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = hostId, Now = now },
            transaction: tx);

        if (bet > 0)
        {
            var deducted = await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance - @Bet
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Bet;",
                new { GuildId = guildId, UserId = hostId, Bet = bet },
                transaction: tx);

            if (deducted == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "Недостаточно средств на балансе для оплаты вступительного взноса.", null);
            }

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @UserId, NULL, @Bet, 'party_bet', @Desc, @Now);",
                new
                {
                    GuildId = guildId,
                    UserId = (ulong?)hostId,
                    Bet = bet,
                    Desc = $"Вступительный взнос в лобби ({gameType})",
                    Now = now
                },
                transaction: tx);
        }

        var sessionId = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO party_game_sessions (
                guild_id, channel_id, voice_channel_id, message_id, host_id,
                game_type, bet, prize_pool, status, phase, round_num, turn_user_id,
                state_json, muted_user_ids, winner_summary, expires_at, created_at, updated_at
            ) VALUES (
                @GuildId, @ChannelId, @VoiceChannelId, 0, @HostId,
                @GameType, @Bet, @PrizePool, 'lobby', 'lobby', 1, @HostId,
                @StateJson, '', '', @ExpiresAt, @Now, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                VoiceChannelId = voiceChannelId,
                HostId = hostId,
                GameType = gameType,
                Bet = bet,
                PrizePool = bet,
                StateJson = initialStateJson,
                ExpiresAt = expiresAt,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO party_game_players (
                session_id, guild_id, user_id, display_name, seat_index,
                role_code, team_code, is_alive, bet_paid, score, player_data_json, joined_at
            ) VALUES (
                @SessionId, @GuildId, @UserId, @DisplayName, 1,
                '', '', 1, @BetPaid, 0, '{}', @Now
            );",
            new
            {
                SessionId = sessionId,
                GuildId = guildId,
                UserId = hostId,
                DisplayName = hostDisplayName,
                BetPaid = bet,
                Now = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        var session = await GetSessionByIdAsync(sessionId, ct);
        return (true, string.Empty, session);
    }

    public async Task UpdateSessionMessageIdAsync(long sessionId, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE party_game_sessions SET message_id = @MessageId WHERE id = @Id;",
            new { Id = sessionId, MessageId = messageId });
    }

    public async Task<(bool Success, string Error)> TryJoinLobbyAsync(
        long sessionId,
        ulong userId,
        string displayName,
        int maxPlayers,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (session is null || session.Status != "lobby")
        {
            await tx.RollbackAsync(ct);
            return (false, "Набор в это лобби уже закрыт.");
        }

        var alreadyJoined = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM party_game_players WHERE session_id = @SessionId AND user_id = @UserId;",
            new { SessionId = sessionId, UserId = userId },
            transaction: tx);

        if (alreadyJoined > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Вы уже находитесь в списке участников этой игры.");
        }

        var currentCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM party_game_players WHERE session_id = @SessionId;",
            new { SessionId = sessionId },
            transaction: tx);

        if (currentCount >= maxPlayers)
        {
            await tx.RollbackAsync(ct);
            return (false, $"В лобби достигнут максимум игроков ({maxPlayers}).");
        }

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { session.GuildId, UserId = userId, Now = now },
            transaction: tx);

        if (session.Bet > 0)
        {
            var deducted = await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance - @Bet
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Bet;",
                new { session.GuildId, UserId = userId, Bet = session.Bet },
                transaction: tx);

            if (deducted == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, $"Недостаточно средств для вступительного взноса ({session.Bet:N0}).");
            }

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @UserId, NULL, @Bet, 'party_bet', @Desc, @Now);",
                new
                {
                    session.GuildId,
                    UserId = (ulong?)userId,
                    session.Bet,
                    Desc = $"Вступительный взнос в игру #{sessionId} ({session.GameType})",
                    Now = now
                },
                transaction: tx);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO party_game_players (
                session_id, guild_id, user_id, display_name, seat_index,
                role_code, team_code, is_alive, bet_paid, score, player_data_json, joined_at
            ) VALUES (
                @SessionId, @GuildId, @UserId, @DisplayName, @SeatIndex,
                '', '', 1, @BetPaid, 0, '{}', @Now
            );",
            new
            {
                SessionId = sessionId,
                session.GuildId,
                UserId = userId,
                DisplayName = displayName,
                SeatIndex = currentCount + 1,
                BetPaid = session.Bet,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE party_game_sessions
            SET prize_pool = prize_pool + @Bet, updated_at = @Now
            WHERE id = @Id;",
            new { Id = sessionId, session.Bet, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> TryLeaveLobbyAsync(
        long sessionId,
        ulong userId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (session is null || session.Status != "lobby")
        {
            await tx.RollbackAsync(ct);
            return (false, "Выйти можно только до старта партии.");
        }

        if (session.HostId == userId)
        {
            await tx.RollbackAsync(ct);
            return (false, "Создатель стола не может покинуть лобби — используйте кнопку «Отменить стол», чтобы распустить игру и вернуть всем ставки.");
        }

        var player = await conn.QuerySingleOrDefaultAsync<PartyGamePlayerEntity>(
            "SELECT * FROM party_game_players WHERE session_id = @SessionId AND user_id = @UserId;",
            new { SessionId = sessionId, UserId = userId },
            transaction: tx);

        if (player is null)
        {
            await tx.RollbackAsync(ct);
            return (false, "Вы не состоите в этом лобби.");
        }

        await conn.ExecuteAsync(
            "DELETE FROM party_game_players WHERE id = @Id;",
            new { player.Id },
            transaction: tx);

        if (player.BetPaid > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Refund
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { session.GuildId, UserId = userId, Refund = player.BetPaid },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @Refund, 'party_refund', @Desc, @Now);",
                new
                {
                    session.GuildId,
                    UserId = (ulong?)userId,
                    Refund = player.BetPaid,
                    Desc = $"Возврат взноса при выходе из лобби #{sessionId}",
                    Now = now
                },
                transaction: tx);
        }

        await conn.ExecuteAsync(@"
            UPDATE party_game_sessions
            SET prize_pool = MAX(0, prize_pool - @Refund), updated_at = @Now
            WHERE id = @Id;",
            new { Id = sessionId, Refund = player.BetPaid, Now = now },
            transaction: tx);

        // Re-index seats
        var remaining = (await conn.QueryAsync<PartyGamePlayerEntity>(
            "SELECT * FROM party_game_players WHERE session_id = @SessionId ORDER BY seat_index ASC, id ASC;",
            new { SessionId = sessionId },
            transaction: tx)).ToList();

        for (var i = 0; i < remaining.Count; i++)
        {
            await conn.ExecuteAsync(
                "UPDATE party_game_players SET seat_index = @Seat WHERE id = @Id;",
                new { Seat = i + 1, remaining[i].Id },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<bool> CancelAndRefundSessionAsync(
        long sessionId,
        string finalStatus = "cancelled",
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (session is null || session.Status is not ("lobby" or "active"))
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var updated = await conn.ExecuteAsync(@"
            UPDATE party_game_sessions
            SET status = @Status, phase = 'finished', muted_user_ids = '', updated_at = @Now
            WHERE id = @Id AND status IN ('lobby', 'active');",
            new { Id = sessionId, Status = finalStatus, Now = now },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var players = (await conn.QueryAsync<PartyGamePlayerEntity>(
            "SELECT * FROM party_game_players WHERE session_id = @SessionId;",
            new { SessionId = sessionId },
            transaction: tx)).ToList();

        foreach (var p in players.Where(x => x.BetPaid > 0))
        {
            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Refund
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { session.GuildId, p.UserId, Refund = p.BetPaid },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @Refund, 'party_refund', @Desc, @Now);",
                new
                {
                    session.GuildId,
                    UserId = (ulong?)p.UserId,
                    Refund = p.BetPaid,
                    Desc = $"Возврат взноса партии #{sessionId} ({session.GameType})",
                    Now = now
                },
                transaction: tx);

            await conn.ExecuteAsync(
                "UPDATE party_game_players SET bet_paid = 0 WHERE id = @Id;",
                new { p.Id },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task SaveFullGameStateAsync(
        long sessionId,
        string status,
        string phase,
        int roundNum,
        ulong turnUserId,
        string stateJson,
        string mutedUserIds,
        string winnerSummary,
        DateTimeOffset expiresAt,
        IReadOnlyList<PartyGamePlayerEntity>? updatedPlayers = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(@"
            UPDATE party_game_sessions
            SET status = @Status,
                phase = @Phase,
                round_num = @RoundNum,
                turn_user_id = @TurnUserId,
                state_json = @StateJson,
                muted_user_ids = @MutedUserIds,
                winner_summary = @WinnerSummary,
                expires_at = @ExpiresAt,
                updated_at = @Now
            WHERE id = @Id;",
            new
            {
                Id = sessionId,
                Status = status,
                Phase = phase,
                RoundNum = roundNum,
                TurnUserId = turnUserId,
                StateJson = stateJson,
                MutedUserIds = mutedUserIds,
                WinnerSummary = winnerSummary,
                ExpiresAt = expiresAt,
                Now = now
            },
            transaction: tx);

        if (updatedPlayers is { Count: > 0 })
        {
            foreach (var p in updatedPlayers)
            {
                await conn.ExecuteAsync(@"
                    UPDATE party_game_players
                    SET seat_index = @SeatIndex,
                        role_code = @RoleCode,
                        team_code = @TeamCode,
                        is_alive = @IsAlive,
                        score = @Score,
                        player_data_json = @PlayerDataJson
                    WHERE id = @Id;",
                    new
                    {
                        p.Id,
                        p.SeatIndex,
                        p.RoleCode,
                        p.TeamCode,
                        IsAlive = p.IsAlive ? 1 : 0,
                        p.Score,
                        p.PlayerDataJson
                    },
                    transaction: tx);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<Dictionary<ulong, long>> FinishSessionAndDistributePrizeAsync(
        long sessionId,
        IReadOnlyList<ulong> winnerUserIds,
        long baseBonusPerWinner,
        string stateJson,
        string winnerSummary,
        IReadOnlyList<PartyGamePlayerEntity>? finalPlayers = null,
        CancellationToken ct = default)
    {
        var payouts = new Dictionary<ulong, long>();
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (session is null || session.Status == "finished")
        {
            await tx.RollbackAsync(ct);
            return payouts;
        }

        var rows = await conn.ExecuteAsync(@"
            UPDATE party_game_sessions
            SET status = 'finished',
                phase = 'finished',
                state_json = @StateJson,
                muted_user_ids = '',
                winner_summary = @WinnerSummary,
                updated_at = @Now
            WHERE id = @Id AND status != 'finished';",
            new
            {
                Id = sessionId,
                StateJson = stateJson,
                WinnerSummary = winnerSummary,
                Now = now
            },
            transaction: tx);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return payouts;
        }

        if (finalPlayers is { Count: > 0 })
        {
            foreach (var p in finalPlayers)
            {
                await conn.ExecuteAsync(@"
                    UPDATE party_game_players
                    SET role_code = @RoleCode,
                        team_code = @TeamCode,
                        is_alive = @IsAlive,
                        score = @Score,
                        player_data_json = @PlayerDataJson
                    WHERE id = @Id;",
                    new
                    {
                        p.Id,
                        p.RoleCode,
                        p.TeamCode,
                        IsAlive = p.IsAlive ? 1 : 0,
                        p.Score,
                        p.PlayerDataJson
                    },
                    transaction: tx);
            }
        }

        var distinctWinners = winnerUserIds.Distinct().ToList();
        if (distinctWinners.Count > 0)
        {
            var poolShare = session.PrizePool > 0 ? session.PrizePool / distinctWinners.Count : 0L;
            var totalEach = poolShare + Math.Max(0, baseBonusPerWinner);

            if (totalEach > 0)
            {
                var txType = $"{session.GameType}_win";
                foreach (var wId in distinctWinners)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO users (guild_id, user_id, created_at)
                        VALUES (@GuildId, @UserId, @Now)
                        ON CONFLICT(guild_id, user_id) DO NOTHING;",
                        new { session.GuildId, UserId = wId, Now = now },
                        transaction: tx);

                    await conn.ExecuteAsync(@"
                        UPDATE users SET balance = balance + @Amount
                        WHERE guild_id = @GuildId AND user_id = @UserId;",
                        new { session.GuildId, UserId = wId, Amount = totalEach },
                        transaction: tx);

                    await conn.ExecuteAsync(@"
                        INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                        VALUES (@GuildId, NULL, @UserId, @Amount, @TxType, @Desc, @Now);",
                        new
                        {
                            session.GuildId,
                            UserId = (ulong?)wId,
                            Amount = totalEach,
                            TxType = txType,
                            Desc = $"Победа в партии #{sessionId} ({session.GameType})",
                            Now = now
                        },
                        transaction: tx);

                    payouts[wId] = totalEach;
                }
            }
        }

        await tx.CommitAsync(ct);
        return payouts;
    }

    public async Task<IReadOnlyList<(ulong GuildId, ulong VoiceChannelId, string MutedUserIds)>> RefundInterruptedPartySessionsOnStartupAsync(
        CancellationToken ct = default)
    {
        var unmuteTargets = new List<(ulong GuildId, ulong VoiceChannelId, string MutedUserIds)>();
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);

        var openSessions = (await conn.QueryAsync<PartyGameSessionEntity>(
            "SELECT * FROM party_game_sessions WHERE status IN ('lobby', 'active');")).ToList();

        foreach (var s in openSessions)
        {
            if (s.VoiceChannelId != 0 && !string.IsNullOrWhiteSpace(s.MutedUserIds))
            {
                unmuteTargets.Add((s.GuildId, s.VoiceChannelId, s.MutedUserIds));
            }

            await CancelAndRefundSessionAsync(s.Id, "refunded", ct);
        }

        return unmuteTargets;
    }

    public async Task<IReadOnlyList<PartyGameSessionEntity>> GetExpiredSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PartyGameSessionEntity>(@"
            SELECT * FROM party_game_sessions
            WHERE status IN ('lobby', 'active') AND expires_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    // --- RAID BOSS REPOSITORY METHODS ---

    public async Task<RaidBossSessionEntity?> GetActiveRaidBossAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RaidBossSessionEntity>(@"
            SELECT * FROM raid_boss_sessions
            WHERE guild_id = @GuildId AND status = 'active'
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId });
    }

    public async Task<RaidBossSessionEntity?> GetRaidBossByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RaidBossSessionEntity>(
            "SELECT * FROM raid_boss_sessions WHERE id = @Id;",
            new { Id = id });
    }

    public async Task<IReadOnlyList<RaidBossHitEntity>> GetRaidHitsAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RaidBossHitEntity>(@"
            SELECT * FROM raid_boss_hits
            WHERE session_id = @SessionId
            ORDER BY total_damage DESC, last_hit_at ASC;",
            new { SessionId = sessionId });
        return rows.ToList();
    }

    public async Task<RaidBossSessionEntity> CreateRaidBossAsync(
        ulong guildId,
        ulong channelId,
        string bossCode,
        string bossName,
        int bossLevel,
        long maxHp,
        long rewardPoolCoins,
        int rewardCrystals,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var endsAt = now.Add(duration);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);

        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO raid_boss_sessions (
                guild_id, channel_id, message_id, boss_code, boss_name, boss_level,
                max_hp, current_hp, reward_pool_coins, reward_crystals,
                status, last_log, ends_at, created_at, updated_at
            ) VALUES (
                @GuildId, @ChannelId, 0, @BossCode, @BossName, @BossLevel,
                @MaxHp, @MaxHp, @RewardCoins, @RewardCrystals,
                'active', 'Босс вторгся в мир KRLZ! Выбирайте класс и атакуйте!', @EndsAt, @Now, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                BossCode = bossCode,
                BossName = bossName,
                BossLevel = bossLevel,
                MaxHp = maxHp,
                RewardCoins = rewardPoolCoins,
                RewardCrystals = rewardCrystals,
                EndsAt = endsAt,
                Now = now
            });

        return (await GetRaidBossByIdAsync(id, ct))!;
    }

    public async Task UpdateRaidBossMessageIdAsync(long id, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE raid_boss_sessions SET message_id = @MessageId WHERE id = @Id;",
            new { Id = id, MessageId = messageId });
    }

    public async Task<(bool Success, string Error, bool Defeated, long NewHp, Dictionary<ulong, (long Coins, int Crystals)> Rewards)> RegisterRaidHitAsync(
        long sessionId,
        ulong userId,
        string displayName,
        string heroClass,
        long damage,
        string actionLog,
        int cooldownSeconds = 6,
        CancellationToken ct = default)
    {
        var rewards = new Dictionary<ulong, (long Coins, int Crystals)>();
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var boss = await conn.QuerySingleOrDefaultAsync<RaidBossSessionEntity>(
            "SELECT * FROM raid_boss_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (boss is null || boss.Status != "active")
        {
            await tx.RollbackAsync(ct);
            return (false, "Этот рейд-босс уже повержен или покинул поле боя.", false, 0, rewards);
        }

        var prevHit = await conn.QuerySingleOrDefaultAsync<RaidBossHitEntity>(@"
            SELECT * FROM raid_boss_hits
            WHERE session_id = @SessionId AND user_id = @UserId;",
            new { SessionId = sessionId, UserId = userId },
            transaction: tx);

        if (prevHit is not null && cooldownSeconds > 0)
        {
            var elapsed = (now - prevHit.LastHitAt).TotalSeconds;
            if (elapsed < cooldownSeconds)
            {
                await tx.RollbackAsync(ct);
                var wait = Math.Max(1, (int)Math.Ceiling(cooldownSeconds - elapsed));
                return (false, $"Оружие перезаряжается! Подождите ещё **{wait} сек.** перед следующим ударом.", false, boss.CurrentHp, rewards);
            }
        }

        var newHp = Math.Max(0, boss.CurrentHp - damage);
        var defeated = newHp == 0;
        var newStatus = defeated ? "defeated" : "active";

        await conn.ExecuteAsync(@"
            INSERT INTO raid_boss_hits (
                session_id, guild_id, user_id, display_name, hero_class,
                total_damage, hits_count, last_hit_at
            ) VALUES (
                @SessionId, @GuildId, @UserId, @DisplayName, @HeroClass,
                @Damage, 1, @Now
            )
            ON CONFLICT(session_id, user_id) DO UPDATE SET
                display_name = excluded.display_name,
                hero_class = excluded.hero_class,
                total_damage = raid_boss_hits.total_damage + excluded.total_damage,
                hits_count = raid_boss_hits.hits_count + 1,
                last_hit_at = excluded.last_hit_at;",
            new
            {
                SessionId = sessionId,
                boss.GuildId,
                UserId = userId,
                DisplayName = displayName,
                HeroClass = heroClass,
                Damage = damage,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE raid_boss_sessions
            SET current_hp = @NewHp,
                status = @Status,
                last_log = @LastLog,
                updated_at = @Now
            WHERE id = @Id;",
            new
            {
                Id = sessionId,
                NewHp = newHp,
                Status = newStatus,
                LastLog = actionLog,
                Now = now
            },
            transaction: tx);

        if (defeated)
        {
            var allHits = (await conn.QueryAsync<RaidBossHitEntity>(@"
                SELECT * FROM raid_boss_hits
                WHERE session_id = @SessionId
                ORDER BY total_damage DESC;",
                new { SessionId = sessionId },
                transaction: tx)).ToList();

            var totalDmg = Math.Max(1L, allHits.Sum(x => x.TotalDamage));

            foreach (var h in allHits)
            {
                var ratio = (double)h.TotalDamage / totalDmg;
                var coins = Math.Max(150L, (long)Math.Round(boss.RewardPoolCoins * ratio));
                var crystals = Math.Max(1, (int)Math.Round(boss.RewardCrystals * ratio));

                await conn.ExecuteAsync(@"
                    INSERT INTO users (guild_id, user_id, created_at)
                    VALUES (@GuildId, @UserId, @Now)
                    ON CONFLICT(guild_id, user_id) DO NOTHING;",
                    new { boss.GuildId, h.UserId, Now = now },
                    transaction: tx);

                await conn.ExecuteAsync(@"
                    UPDATE users SET balance = balance + @Coins
                    WHERE guild_id = @GuildId AND user_id = @UserId;",
                    new { boss.GuildId, h.UserId, Coins = coins },
                    transaction: tx);

                await conn.ExecuteAsync(@"
                    INSERT INTO bank_accounts (
                        guild_id, user_id, deposit_balance, crystals, total_interest_earned, last_interest_at, updated_at
                    ) VALUES (
                        @GuildId, @UserId, 0, @Crystals, 0, @Now, @Now
                    )
                    ON CONFLICT(guild_id, user_id) DO UPDATE SET
                        crystals = bank_accounts.crystals + @Crystals,
                        updated_at = @Now;",
                    new { boss.GuildId, h.UserId, Crystals = crystals, Now = now },
                    transaction: tx);

                await conn.ExecuteAsync(@"
                    INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                    VALUES (@GuildId, NULL, @UserId, @Coins, 'raid_reward', @Desc, @Now);",
                    new
                    {
                        boss.GuildId,
                        UserId = (ulong?)h.UserId,
                        Coins = coins,
                        Desc = $"Награда за победу над Рейд-Боссом «{boss.BossName}» (урон: {h.TotalDamage:N0})",
                        Now = now
                    },
                    transaction: tx);

                rewards[h.UserId] = (coins, crystals);
            }
        }

        await tx.CommitAsync(ct);
        return (true, string.Empty, defeated, newHp, rewards);
    }

    // --- CUSTOM CONTENT & ANALYTICS FOR WEB PANEL ---

    public async Task<IReadOnlyList<PartyGameContentEntity>> GetCustomContentAsync(
        ulong guildId,
        string? gameType = null,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        if (string.IsNullOrWhiteSpace(gameType))
        {
            var all = await conn.QueryAsync<PartyGameContentEntity>(
                "SELECT * FROM party_game_content WHERE guild_id = @GuildId ORDER BY id DESC LIMIT 500;",
                new { GuildId = guildId });
            return all.ToList();
        }

        var filtered = await conn.QueryAsync<PartyGameContentEntity>(@"
            SELECT * FROM party_game_content
            WHERE guild_id = @GuildId AND game_type = @GameType
            ORDER BY id DESC;",
            new { GuildId = guildId, GameType = gameType });
        return filtered.ToList();
    }

    public async Task<long> AddCustomContentAsync(
        ulong guildId,
        string gameType,
        string title,
        string subtitle,
        string payloadJson = "{}",
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO party_game_content (guild_id, game_type, title, subtitle, payload_json, created_at)
            VALUES (@GuildId, @GameType, @Title, @Subtitle, @PayloadJson, @Now);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                GameType = gameType.Trim(),
                Title = title.Trim(),
                Subtitle = subtitle.Trim(),
                PayloadJson = payloadJson,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task DeleteCustomContentAsync(ulong guildId, long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM party_game_content WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = id, GuildId = guildId });
    }

    public async Task<IReadOnlyList<PartyGameSessionEntity>> GetRecentSessionsAsync(
        ulong guildId,
        int limit = 25,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PartyGameSessionEntity>(@"
            SELECT * FROM party_game_sessions
            WHERE guild_id = @GuildId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }
}
