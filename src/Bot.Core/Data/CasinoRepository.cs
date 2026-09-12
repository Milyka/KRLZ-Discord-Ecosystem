using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed record CasinoUserStatsDto(
    long TotalWagered,
    long TotalWon,
    long NetProfit,
    long TodayNetLoss,
    int TotalBetsCount,
    int TotalWinsCount
);

public sealed class CasinoRepository(DbConnectionFactory dbFactory)
{
    private static readonly string[] BetTxTypes =
    [
        "slots_bet",
        "coinflip_bet",
        "blackjack_bet",
        "roulette_bet",
        "crash_bet",
        "jackpot_bet"
    ];

    private static readonly string[] WinTxTypes =
    [
        "slots_win",
        "coinflip_win",
        "blackjack_win",
        "roulette_win",
        "crash_win",
        "jackpot_win"
    ];

    public async Task<long> GetTodayNetLossAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var startOfDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        var wageredToday = await conn.ExecuteScalarAsync<long>(@"
            SELECT COALESCE(SUM(amount), 0)
            FROM transactions
            WHERE guild_id = @GuildId
              AND from_user_id = @UserId
              AND tx_type IN @BetTypes
              AND created_at >= @Since;",
            new { GuildId = guildId, UserId = userId, BetTypes = BetTxTypes, Since = startOfDay });

        var wonToday = await conn.ExecuteScalarAsync<long>(@"
            SELECT COALESCE(SUM(amount), 0)
            FROM transactions
            WHERE guild_id = @GuildId
              AND to_user_id = @UserId
              AND tx_type IN @WinTypes
              AND created_at >= @Since;",
            new { GuildId = guildId, UserId = userId, WinTypes = WinTxTypes, Since = startOfDay });

        return Math.Max(0, wageredToday - wonToday);
    }

    public async Task<CasinoUserStatsDto> GetUserCasinoStatsAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);

        var betRow = await conn.QuerySingleAsync<(long Sum, int Count)>(@"
            SELECT COALESCE(SUM(amount), 0) AS Sum, COUNT(*) AS Count
            FROM transactions
            WHERE guild_id = @GuildId AND from_user_id = @UserId AND tx_type IN @BetTypes;",
            new { GuildId = guildId, UserId = userId, BetTypes = BetTxTypes });

        var winRow = await conn.QuerySingleAsync<(long Sum, int Count)>(@"
            SELECT COALESCE(SUM(amount), 0) AS Sum, COUNT(*) AS Count
            FROM transactions
            WHERE guild_id = @GuildId AND to_user_id = @UserId AND tx_type IN @WinTypes;",
            new { GuildId = guildId, UserId = userId, WinTypes = WinTxTypes });

        var todayLoss = await GetTodayNetLossAsync(guildId, userId, ct);

        return new CasinoUserStatsDto(
            TotalWagered: betRow.Sum,
            TotalWon: winRow.Sum,
            NetProfit: winRow.Sum - betRow.Sum,
            TodayNetLoss: todayLoss,
            TotalBetsCount: betRow.Count,
            TotalWinsCount: winRow.Count);
    }

    public async Task<BlackjackSessionEntity?> GetActiveBlackjackAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BlackjackSessionEntity>(@"
            SELECT * FROM blackjack_sessions
            WHERE guild_id = @GuildId AND user_id = @UserId AND status = 'playing'
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<BlackjackSessionEntity?> GetBlackjackByIdAsync(long sessionId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BlackjackSessionEntity>(
            "SELECT * FROM blackjack_sessions WHERE id = @Id;",
            new { Id = sessionId });
    }

    public async Task<BlackjackSessionEntity> CreateBlackjackSessionAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        long bet,
        string deckJson,
        string playerHandsJson,
        string dealerHandJson,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO blackjack_sessions (
                guild_id, user_id, channel_id, message_id, bet, insurance_bet,
                deck_json, player_hands_json, dealer_hand_json, active_hand_index,
                status, created_at, updated_at
            ) VALUES (
                @GuildId, @UserId, @ChannelId, 0, @Bet, 0,
                @DeckJson, @PlayerHandsJson, @DealerHandJson, 0,
                'playing', @Now, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                Bet = bet,
                DeckJson = deckJson,
                PlayerHandsJson = playerHandsJson,
                DealerHandJson = dealerHandJson,
                Now = now
            });

        return new BlackjackSessionEntity(
            id, guildId, userId, channelId, 0, bet, 0,
            deckJson, playerHandsJson, dealerHandJson, 0, "playing", now, now);
    }

    public async Task UpdatePlayingBlackjackSessionAsync(BlackjackSessionEntity session, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE blackjack_sessions
            SET message_id = @MessageId,
                bet = @Bet,
                insurance_bet = @InsuranceBet,
                deck_json = @DeckJson,
                player_hands_json = @PlayerHandsJson,
                dealer_hand_json = @DealerHandJson,
                active_hand_index = @ActiveHandIndex,
                updated_at = @UpdatedAt
            WHERE id = @Id AND status = 'playing';",
            session with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    public async Task<bool> UpdateBlackjackStatusAsync(long sessionId, string newStatus = "finished", CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "UPDATE blackjack_sessions SET status = @NewStatus, updated_at = @Now WHERE id = @Id;",
            new { Id = sessionId, NewStatus = newStatus, Now = DateTimeOffset.UtcNow });
        return rows > 0;
    }

    public async Task<bool> TryTransitionBlackjackToFinishedAsync(
        BlackjackSessionEntity session,
        string newStatus = "finished",
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            UPDATE blackjack_sessions
            SET deck_json = @DeckJson,
                player_hands_json = @PlayerHandsJson,
                dealer_hand_json = @DealerHandJson,
                active_hand_index = @ActiveHandIndex,
                status = @NewStatus,
                updated_at = @UpdatedAt
            WHERE id = @Id AND status = 'playing';",
            new
            {
                session.Id,
                session.DeckJson,
                session.PlayerHandsJson,
                session.DealerHandJson,
                session.ActiveHandIndex,
                NewStatus = newStatus,
                UpdatedAt = DateTimeOffset.UtcNow
            });

        return rows > 0;
    }

    public async Task<IReadOnlyList<BlackjackSessionEntity>> GetAbandonedBlackjackSessionsAsync(
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BlackjackSessionEntity>(@"
            SELECT * FROM blackjack_sessions
            WHERE status = 'playing' AND updated_at <= @Cutoff;",
            new { Cutoff = cutoff });
        return rows.ToList();
    }

    public async Task<long> CreateRouletteRoundRecordAsync(
        ulong guildId,
        ulong channelId,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO roulette_rounds (guild_id, channel_id, status, ends_at, created_at)
            VALUES (@GuildId, @ChannelId, 'open', @EndsAt, @Now);
            SELECT last_insert_rowid();",
            new { GuildId = guildId, ChannelId = channelId, EndsAt = endsAt, Now = DateTimeOffset.UtcNow });
    }

    public async Task<long> AddRouletteBetRecordAsync(
        long roundId,
        ulong guildId,
        ulong userId,
        string betType,
        string betValue,
        long amount,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO roulette_bets (round_id, guild_id, user_id, bet_type, bet_value, amount, status, created_at)
            VALUES (@RoundId, @GuildId, @UserId, @BetType, @BetValue, @Amount, 'held', @Now);
            SELECT last_insert_rowid();",
            new
            {
                RoundId = roundId,
                GuildId = guildId,
                UserId = userId,
                BetType = betType,
                BetValue = betValue,
                Amount = amount,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task MarkRouletteRoundSettledAsync(
        long roundId,
        int rolledNumber,
        string rolledColor,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE roulette_rounds
            SET status = 'settled', rolled_number = @Num, rolled_color = @Color
            WHERE id = @Id;
            UPDATE roulette_bets SET status = 'settled' WHERE round_id = @Id AND status = 'held';",
            new { Id = roundId, Num = rolledNumber, Color = rolledColor });
    }

    public async Task<long> CreateJackpotRoundRecordAsync(
        ulong guildId,
        ulong channelId,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO jackpot_rounds (guild_id, channel_id, status, total_pool, ends_at, created_at)
            VALUES (@GuildId, @ChannelId, 'open', 0, @EndsAt, @Now);
            SELECT last_insert_rowid();",
            new { GuildId = guildId, ChannelId = channelId, EndsAt = endsAt, Now = DateTimeOffset.UtcNow });
    }

    public async Task AddJackpotBetRecordAsync(
        long roundId,
        ulong guildId,
        ulong userId,
        long amount,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO jackpot_bets (round_id, guild_id, user_id, amount, status, created_at)
            VALUES (@RoundId, @GuildId, @UserId, @Amount, 'held', @Now);
            UPDATE jackpot_rounds SET total_pool = total_pool + @Amount WHERE id = @RoundId;",
            new { RoundId = roundId, GuildId = guildId, UserId = userId, Amount = amount, Now = DateTimeOffset.UtcNow });
    }

    public async Task MarkJackpotRoundSettledAsync(long roundId, ulong winnerId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE jackpot_rounds SET status = 'settled', winner_id = @WinnerId WHERE id = @Id;
            UPDATE jackpot_bets SET status = 'settled' WHERE round_id = @Id AND status = 'held';",
            new { Id = roundId, WinnerId = winnerId });
    }

    public async Task<long> CreateCrashBetRecordAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        long amount,
        double crashPoint,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO crash_bets (guild_id, user_id, channel_id, amount, crash_point, status, created_at, updated_at)
            VALUES (@GuildId, @UserId, @ChannelId, @Amount, @CrashPoint, 'active', @Now, @Now);
            SELECT last_insert_rowid();",
            new { GuildId = guildId, UserId = userId, ChannelId = channelId, Amount = amount, CrashPoint = crashPoint, Now = now });
    }

    public async Task<bool> CompleteCrashBetRecordAsync(
        long crashBetId,
        string newStatus,
        double? cashedOutMultiplier,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            UPDATE crash_bets
            SET status = @Status,
                cashed_out_multiplier = @Mult,
                updated_at = @Now
            WHERE id = @Id AND status = 'active';",
            new { Id = crashBetId, Status = newStatus, Mult = cashedOutMultiplier, Now = DateTimeOffset.UtcNow });
        return rows > 0;
    }

    public async Task<int> RefundUnsettledCasinoBetsOnStartupAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var refundedCount = 0;

        var heldRoulette = (await conn.QueryAsync<RouletteBetEntity>(
            "SELECT * FROM roulette_bets WHERE status = 'held';",
            transaction: tx)).ToList();

        foreach (var rb in heldRoulette)
        {
            await RefundSingleBetInternalAsync(conn, tx, rb.GuildId, rb.UserId, rb.Amount, "roulette_refund", $"Возврат ставки рулетки #{rb.RoundId} после перезапуска", now);
            await conn.ExecuteAsync("UPDATE roulette_bets SET status = 'refunded' WHERE id = @Id;", new { rb.Id }, transaction: tx);
            refundedCount++;
        }

        await conn.ExecuteAsync("UPDATE roulette_rounds SET status = 'refunded' WHERE status = 'open';", transaction: tx);

        var heldJackpot = (await conn.QueryAsync<JackpotBetEntity>(
            "SELECT * FROM jackpot_bets WHERE status = 'held';",
            transaction: tx)).ToList();

        foreach (var jb in heldJackpot)
        {
            await RefundSingleBetInternalAsync(conn, tx, jb.GuildId, jb.UserId, jb.Amount, "jackpot_refund", $"Возврат ставки джекпота #{jb.RoundId} после перезапуска", now);
            await conn.ExecuteAsync("UPDATE jackpot_bets SET status = 'refunded' WHERE id = @Id;", new { jb.Id }, transaction: tx);
            refundedCount++;
        }

        await conn.ExecuteAsync("UPDATE jackpot_rounds SET status = 'refunded' WHERE status = 'open';", transaction: tx);

        var activeCrash = (await conn.QueryAsync<CrashBetEntity>(
            "SELECT * FROM crash_bets WHERE status = 'active';",
            transaction: tx)).ToList();

        foreach (var cb in activeCrash)
        {
            await RefundSingleBetInternalAsync(conn, tx, cb.GuildId, cb.UserId, cb.Amount, "crash_refund", $"Возврат прерванной ставки краша #{cb.Id} после перезапуска", now);
            await conn.ExecuteAsync("UPDATE crash_bets SET status = 'refunded', updated_at = @Now WHERE id = @Id;", new { cb.Id, Now = now }, transaction: tx);
            refundedCount++;
        }

        var activeBj = (await conn.QueryAsync<BlackjackSessionEntity>(
            "SELECT * FROM blackjack_sessions WHERE status != 'finished';",
            transaction: tx)).ToList();

        foreach (var bj in activeBj)
        {
            await RefundSingleBetInternalAsync(conn, tx, bj.GuildId, bj.UserId, bj.Bet, "blackjack_refund", $"Возврат прерванной ставки блэкджека #{bj.Id} после перезапуска", now);
            await conn.ExecuteAsync("UPDATE blackjack_sessions SET status = 'finished', updated_at = @Now WHERE id = @Id;", new { bj.Id, Now = now }, transaction: tx);
            refundedCount++;
        }

        await tx.CommitAsync(ct);
        return refundedCount;
    }

    private static async Task RefundSingleBetInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        ulong guildId,
        ulong userId,
        long amount,
        string txType,
        string description,
        DateTimeOffset now)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, NULL, @UserId, @Amount, @TxType, @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, TxType = txType, Desc = description, Now = now },
            transaction: tx);
    }
}
