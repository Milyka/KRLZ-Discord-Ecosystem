using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class UserRepository(DbConnectionFactory dbFactory)
{
    public async Task<UserProfileEntity> GetOrCreateAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);

        return await conn.QuerySingleAsync<UserProfileEntity>(
            "SELECT * FROM users WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<bool> TryModifyBalanceAsync(
        ulong guildId,
        ulong userId,
        long delta,
        string txType,
        string description,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await EnsureExistsInternalAsync(conn, tx, guildId, userId);

        if (delta < 0)
        {
            var rows = await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @Delta
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance + @Delta >= 0;",
                new { GuildId = guildId, UserId = userId, Delta = delta },
                transaction: tx);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }
        else
        {
            await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @Delta
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userId, Delta = delta },
                transaction: tx);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @FromUserId, @ToUserId, @Amount, @TxType, @Description, @CreatedAt);",
            new
            {
                GuildId = guildId,
                FromUserId = delta < 0 ? (ulong?)userId : null,
                ToUserId = delta >= 0 ? (ulong?)userId : null,
                Amount = Math.Abs(delta),
                TxType = txType,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryTransferAsync(
        ulong guildId,
        ulong fromUserId,
        ulong toUserId,
        long amount,
        string description,
        CancellationToken ct = default)
    {
        if (amount <= 0 || fromUserId == toUserId)
            return false;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await EnsureExistsInternalAsync(conn, tx, guildId, fromUserId);
        await EnsureExistsInternalAsync(conn, tx, guildId, toUserId);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Amount
            WHERE guild_id = @GuildId AND user_id = @FromUserId AND balance >= @Amount;",
            new { GuildId = guildId, FromUserId = fromUserId, Amount = amount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Amount
            WHERE guild_id = @GuildId AND user_id = @ToUserId;",
            new { GuildId = guildId, ToUserId = toUserId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @FromUserId, @ToUserId, @Amount, 'transfer', @Description, @CreatedAt);",
            new
            {
                GuildId = guildId,
                FromUserId = (ulong?)fromUserId,
                ToUserId = (ulong?)toUserId,
                Amount = amount,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task ResetBalanceAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await EnsureExistsInternalAsync(conn, tx, guildId, userId);

        await conn.ExecuteAsync(
            "UPDATE users SET balance = 0 WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, 0, 'admin_reset', 'Сброс баланса администратором', @CreatedAt);",
            new { GuildId = guildId, UserId = (ulong?)userId, CreatedAt = DateTimeOffset.UtcNow },
            transaction: tx);

        await tx.CommitAsync(ct);
    }

    public async Task UpdateCooldownTimestampAsync(ulong guildId, ulong userId, bool isDaily, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        var column = isDaily ? "last_daily_at" : "last_work_at";
        await conn.ExecuteAsync(
            $"UPDATE users SET {column} = @Timestamp WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Timestamp = timestamp });
    }

    public async Task<(int NewLevel, bool LeveledUp)> AddMessageProgressAsync(
        ulong guildId,
        ulong userId,
        long xpGain,
        long coinGain,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await EnsureExistsInternalAsync(conn, tx, guildId, userId);
        var profile = await conn.QuerySingleAsync<UserProfileEntity>(
            "SELECT * FROM users WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId },
            transaction: tx);

        var newXp = profile.Xp + Math.Max(0, xpGain);
        var newLevel = CalculateLevel(newXp);
        var leveledUp = newLevel > profile.Level;

        await conn.ExecuteAsync(@"
            UPDATE users
            SET messages = messages + 1,
                xp = @NewXp,
                level = @NewLevel,
                balance = balance + @CoinGain
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                NewXp = newXp,
                NewLevel = newLevel,
                CoinGain = Math.Max(0, coinGain)
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (newLevel, leveledUp);
    }

    public async Task<(int NewLevel, bool LeveledUp)> AddVoiceProgressAsync(
        ulong guildId,
        ulong userId,
        long secondsDelta,
        long xpGain,
        long coinGain,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await EnsureExistsInternalAsync(conn, tx, guildId, userId);
        var profile = await conn.QuerySingleAsync<UserProfileEntity>(
            "SELECT * FROM users WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId },
            transaction: tx);

        var newXp = profile.Xp + Math.Max(0, xpGain);
        var newLevel = CalculateLevel(newXp);
        var leveledUp = newLevel > profile.Level;

        await conn.ExecuteAsync(@"
            UPDATE users
            SET voice_seconds = voice_seconds + @SecondsDelta,
                xp = @NewXp,
                level = @NewLevel,
                balance = balance + @CoinGain
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                SecondsDelta = Math.Max(0, secondsDelta),
                NewXp = newXp,
                NewLevel = newLevel,
                CoinGain = Math.Max(0, coinGain)
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (newLevel, leveledUp);
    }

    public async Task UpdateBioAsync(ulong guildId, ulong userId, string bio, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        await conn.ExecuteAsync(
            "UPDATE users SET bio = @Bio WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Bio = bio });
    }

    public async Task UpdateBirthdayAsync(ulong guildId, ulong userId, string birthday, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        await conn.ExecuteAsync(
            "UPDATE users SET birthday = @Birthday WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Birthday = birthday });
    }

    public async Task UpdateTitleAsync(ulong guildId, ulong userId, string title, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        await conn.ExecuteAsync(
            "UPDATE users SET title = @Title WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Title = title });
    }

    public async Task UpdateBackgroundAsync(ulong guildId, ulong userId, string background, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        await conn.ExecuteAsync(
            "UPDATE users SET selected_background = @Bg WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Bg = background });
    }

    public async Task<int> GetUserRankByXpAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        var rank = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1
            FROM users
            WHERE guild_id = @GuildId
              AND xp > COALESCE((SELECT xp FROM users WHERE guild_id = @GuildId AND user_id = @UserId), 0);",
            new { GuildId = guildId, UserId = userId });
        return Math.Max(1, rank);
    }

    public async Task<int> GetUserRankByBalanceAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await EnsureExistsInternalAsync(conn, null, guildId, userId);
        var rank = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1
            FROM users
            WHERE guild_id = @GuildId
              AND balance > COALESCE((SELECT balance FROM users WHERE guild_id = @GuildId AND user_id = @UserId), 0);",
            new { GuildId = guildId, UserId = userId });
        return Math.Max(1, rank);
    }

    public async Task<int> GetUserRankByReputationAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rank = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) + 1
            FROM user_reputation
            WHERE guild_id = @GuildId
              AND reputation > COALESCE((SELECT reputation FROM user_reputation WHERE guild_id = @GuildId AND user_id = @UserId), 0);",
            new { GuildId = guildId, UserId = userId });
        return Math.Max(1, rank);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetTopAsync(
        ulong guildId,
        string metric,
        string period,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var since = period switch
        {
            "day" => DateTimeOffset.UtcNow.AddDays(-1),
            "week" => DateTimeOffset.UtcNow.AddDays(-7),
            _ => (DateTimeOffset?)null
        };

        if (metric == "reputation")
        {
            var rows = await conn.QueryAsync<LeaderboardEntryDto>(@"
                SELECT ur.user_id AS UserId,
                       ur.reputation AS Value,
                       COALESCE(u.level, 1) AS Level
                FROM user_reputation ur
                LEFT JOIN users u ON u.guild_id = ur.guild_id AND u.user_id = ur.user_id
                WHERE ur.guild_id = @GuildId
                ORDER BY ur.reputation DESC
                LIMIT @Limit OFFSET @Offset;",
                new { GuildId = guildId, Limit = limit, Offset = offset });
            return rows.ToList();
        }

        if (metric == "voice" && since.HasValue)
        {
            var rows = await conn.QueryAsync<LeaderboardEntryDto>(@"
                SELECT vs.user_id AS UserId,
                       SUM(vs.duration_seconds) AS Value,
                       COALESCE(u.level, 1) AS Level
                FROM voice_sessions vs
                LEFT JOIN users u ON u.guild_id = vs.guild_id AND u.user_id = vs.user_id
                WHERE vs.guild_id = @GuildId AND vs.started_at >= @Since
                GROUP BY vs.user_id
                ORDER BY Value DESC
                LIMIT @Limit OFFSET @Offset;",
                new { GuildId = guildId, Since = since.Value, Limit = limit, Offset = offset });
            return rows.ToList();
        }

        if (metric == "balance" && since.HasValue)
        {
            var rows = await conn.QueryAsync<LeaderboardEntryDto>(@"
                SELECT t.to_user_id AS UserId,
                       SUM(t.amount) AS Value,
                       COALESCE(u.level, 1) AS Level
                FROM transactions t
                LEFT JOIN users u ON u.guild_id = t.guild_id AND u.user_id = t.to_user_id
                WHERE t.guild_id = @GuildId AND t.to_user_id IS NOT NULL AND t.created_at >= @Since
                GROUP BY t.to_user_id
                ORDER BY Value DESC
                LIMIT @Limit OFFSET @Offset;",
                new { GuildId = guildId, Since = since.Value, Limit = limit, Offset = offset });
            return rows.ToList();
        }

        var column = metric switch
        {
            "voice" => "voice_seconds",
            "messages" => "messages",
            "level" => "xp",
            _ => "balance"
        };

        var result = await conn.QueryAsync<LeaderboardEntryDto>($@"
            SELECT user_id AS UserId, {column} AS Value, level AS Level
            FROM users
            WHERE guild_id = @GuildId
            ORDER BY {column} DESC
            LIMIT @Limit OFFSET @Offset;",
            new { GuildId = guildId, Limit = limit, Offset = offset });

        return result.ToList();
    }

    public async Task<(long TotalUsers, long TotalBalance, long TotalMessages, long TotalVoiceSeconds)> GetGuildTotalsAsync(
        ulong guildId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<(long TotalUsers, long TotalBalance, long TotalMessages, long TotalVoiceSeconds)>(@"
            SELECT COUNT(*) AS TotalUsers,
                   COALESCE(SUM(balance), 0) AS TotalBalance,
                   COALESCE(SUM(messages), 0) AS TotalMessages,
                   COALESCE(SUM(voice_seconds), 0) AS TotalVoiceSeconds
            FROM users
            WHERE guild_id = @GuildId;",
            new { GuildId = guildId });
        return row;
    }

    public static int CalculateLevel(long xp) =>
        Math.Max(1, (int)Math.Floor(Math.Sqrt(Math.Max(0, xp) / 100.0)) + 1);

    public static long XpForLevel(int level)
    {
        if (level <= 1)
            return 0;
        long prev = level - 1;
        return prev * prev * 100L;
    }

    private static async Task EnsureExistsInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction? tx,
        ulong guildId,
        ulong userId)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @CreatedAt)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, CreatedAt = DateTimeOffset.UtcNow },
            transaction: tx);
    }
}
