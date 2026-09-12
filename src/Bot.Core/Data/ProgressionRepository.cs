using System.Text.Json;
using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed record UserQuestView(
    QuestEntity Definition,
    UserQuestEntity Progress
);

public sealed record BattlePassRewardNodeDto(
    int Level,
    string Track,
    string RewardType,
    long CoinsAmount,
    string? Payload,
    int ItemQuantity,
    string Label
);

public sealed class ProgressionRepository(DbConnectionFactory dbFactory)
{
    public async Task EnsureDefaultQuestsAndSeasonAsync(ulong guildId, CancellationToken ct = default)
    {
        await EnsureDefaultQuestsAsync(guildId, ct);
        await GetOrCreateActiveSeasonAsync(guildId, 45, 50, 500, 10000, ct);
    }

    public async Task<BattlePassSeasonEntity?> GetActiveSeasonAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await conn.QuerySingleOrDefaultAsync<BattlePassSeasonEntity>(@"
            SELECT * FROM battlepass_seasons
            WHERE guild_id = @GuildId AND active = 1 AND ends_at > @Now
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId, Now = now });
    }

    public async Task EnsureDefaultQuestsAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM quests WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        if (count >= 13)
            return;

        var now = DateTimeOffset.UtcNow;
        var defaults = new (string Code, string Name, string Desc, string Period, string ObjType, long Target, long Coins, long Xp, int BpXp)[]
        {
            ("daily_msg_25", "Активный диалог", "Отправить 25 сообщений в текстовых каналах", "daily", "messages", 25, 200, 120, 150),
            ("daily_msg_60", "Душа компании", "Отправить 60 сообщений за день", "daily", "messages", 60, 400, 220, 250),
            ("daily_voice_20", "Голосовой эфир", "Провести 20 минут в голосовых каналах", "daily", "voice_minutes", 20, 250, 150, 180),
            ("daily_fish_5", "Свежий улов", "Поймать 5 рыб на рыбалке (/fish)", "daily", "fish_caught", 5, 280, 140, 175),
            ("daily_casino_500", "Удачная ставка", "Выиграть суммарно 500 монет в играх казино", "daily", "coins_won", 500, 300, 150, 180),
            ("daily_clan_pts", "Вклад в братство", "Принести 20 очков активности своему клану", "daily", "clan_points", 20, 260, 140, 160),
            ("daily_party_2", "Вечер настолок", "Сыграть 2 партии в Мафию, Бункер, Шпиона или Бар Лжецов", "daily", "party_games", 2, 450, 240, 260),
            ("daily_raid_3", "Охотник на Боссов", "Нанести 3 удара по Рейд-Боссу сервера (/raid)", "daily", "raid_hits", 3, 380, 200, 220),
            ("weekly_msg_300", "Недельный марафон чата", "Отправить 300 сообщений за неделю", "weekly", "messages", 300, 1500, 800, 750),
            ("weekly_voice_180", "Голосовой резидент", "Провести 180 минут в голосовых каналах за неделю", "weekly", "voice_minutes", 180, 1800, 950, 850),
            ("weekly_fish_30", "Промысловый рыболов", "Поймать 30 рыб за неделю", "weekly", "fish_caught", 30, 1600, 850, 800),
            ("weekly_party_10", "Мастер Интриг", "Сыграть 10 партий в компанейские игры сервера за неделю", "weekly", "party_games", 10, 2200, 1100, 950),
            ("weekly_raid_15", "Гвардия KRLZ", "Атаковать Рейд-Боссов 15 раз за неделю", "weekly", "raid_hits", 15, 2000, 1000, 900)
        };

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var q in defaults)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO quests (
                    guild_id, code, name, description, quest_period, objective_type,
                    target_value, reward_coins, reward_xp, reward_bp_xp, reward_item_def_id, enabled, created_at
                ) VALUES (
                    @GuildId, @Code, @Name, @Desc, @Period, @ObjType,
                    @Target, @Coins, @Xp, @BpXp, NULL, 1, @Now
                )
                ON CONFLICT(guild_id, code) DO NOTHING;",
                new
                {
                    GuildId = guildId,
                    q.Code,
                    q.Name,
                    Desc = q.Desc,
                    q.Period,
                    q.ObjType,
                    q.Target,
                    q.Coins,
                    q.Xp,
                    q.BpXp,
                    Now = now
                },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<UserQuestView>> GetOrAssignActiveQuestsAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await EnsureDefaultQuestsAsync(guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(@"
            DELETE FROM user_quests
            WHERE guild_id = @GuildId AND user_id = @UserId AND expires_at <= @Now;",
            new { GuildId = guildId, UserId = userId, Now = now });

        var currentRows = (await conn.QueryAsync<UserQuestEntity>(@"
            SELECT * FROM user_quests
            WHERE guild_id = @GuildId AND user_id = @UserId AND expires_at > @Now;",
            new { GuildId = guildId, UserId = userId, Now = now })).ToList();

        var allQuests = (await conn.QueryAsync<QuestEntity>(
            "SELECT * FROM quests WHERE guild_id = @GuildId AND enabled = 1;",
            new { GuildId = guildId })).ToDictionary(x => x.Id);

        var hasDaily = currentRows.Any(r => allQuests.TryGetValue(r.QuestId, out var q) && q.QuestPeriod == "daily");
        var hasWeekly = currentRows.Any(r => allQuests.TryGetValue(r.QuestId, out var q) && q.QuestPeriod == "weekly");

        var todayKey = now.UtcDateTime.ToString("yyyy-MM-dd");
        var nextDayUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.UtcDateTime.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        var nextMondayUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(daysUntilMonday), TimeSpan.Zero);

        if (!hasDaily)
        {
            var dailyPool = allQuests.Values
                .Where(q => q.QuestPeriod == "daily")
                .OrderBy(_ => Random.Shared.Next())
                .Take(3)
                .ToList();

            foreach (var dq in dailyPool)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO user_quests (
                        guild_id, user_id, quest_id, progress, completed, claimed, assigned_date, expires_at, updated_at
                    ) VALUES (
                        @GuildId, @UserId, @QuestId, 0, 0, 0, @DateKey, @ExpiresAt, @Now
                    );",
                    new { GuildId = guildId, UserId = userId, QuestId = dq.Id, DateKey = todayKey, ExpiresAt = nextDayUtc, Now = now });
            }
        }

        if (!hasWeekly)
        {
            var weeklyPool = allQuests.Values
                .Where(q => q.QuestPeriod == "weekly")
                .OrderBy(_ => Random.Shared.Next())
                .Take(2)
                .ToList();

            foreach (var wq in weeklyPool)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO user_quests (
                        guild_id, user_id, quest_id, progress, completed, claimed, assigned_date, expires_at, updated_at
                    ) VALUES (
                        @GuildId, @UserId, @QuestId, 0, 0, 0, @DateKey, @ExpiresAt, @Now
                    );",
                    new { GuildId = guildId, UserId = userId, QuestId = wq.Id, DateKey = todayKey, ExpiresAt = nextMondayUtc, Now = now });
            }
        }

        var refreshed = (await conn.QueryAsync<UserQuestEntity>(@"
            SELECT * FROM user_quests
            WHERE guild_id = @GuildId AND user_id = @UserId AND expires_at > @Now
            ORDER BY id ASC;",
            new { GuildId = guildId, UserId = userId, Now = now })).ToList();

        var views = new List<UserQuestView>();
        foreach (var r in refreshed)
        {
            if (allQuests.TryGetValue(r.QuestId, out var q))
                views.Add(new UserQuestView(q, r));
        }

        return views;
    }

    public async Task IncrementQuestObjectiveAsync(
        ulong guildId,
        ulong userId,
        string objectiveType,
        long delta,
        CancellationToken ct = default)
    {
        if (delta <= 0)
            return;

        var active = await GetOrAssignActiveQuestsAsync(guildId, userId, ct);
        var matching = active.Where(x => x.Definition.ObjectiveType == objectiveType && !x.Progress.Completed).ToList();
        if (matching.Count == 0)
            return;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in matching)
        {
            var nextProgress = Math.Min(item.Definition.TargetValue, item.Progress.Progress + delta);
            var completed = nextProgress >= item.Definition.TargetValue ? 1 : 0;

            await conn.ExecuteAsync(@"
                UPDATE user_quests
                SET progress = @NextProgress,
                    completed = @Completed,
                    updated_at = @Now
                WHERE id = @Id;",
                new { Id = item.Progress.Id, NextProgress = nextProgress, Completed = completed, Now = now });
        }
    }

    public async Task<IReadOnlyList<QuestEntity>> ClaimReadyQuestsAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var readyRows = (await conn.QueryAsync<UserQuestEntity>(@"
            SELECT * FROM user_quests
            WHERE guild_id = @GuildId AND user_id = @UserId AND completed = 1 AND claimed = 0 AND expires_at > @Now;",
            new { GuildId = guildId, UserId = userId, Now = now },
            transaction: tx)).ToList();

        if (readyRows.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return [];
        }

        var claimedDefs = new List<QuestEntity>();
        foreach (var row in readyRows)
        {
            var q = await conn.QuerySingleOrDefaultAsync<QuestEntity>(
                "SELECT * FROM quests WHERE id = @Id;",
                new { Id = row.QuestId },
                transaction: tx);

            if (q is null)
                continue;

            await conn.ExecuteAsync(
                "UPDATE user_quests SET claimed = 1, updated_at = @Now WHERE id = @Id;",
                new { Id = row.Id, Now = now },
                transaction: tx);

            claimedDefs.Add(q);
        }

        await tx.CommitAsync(ct);
        return claimedDefs;
    }

    public async Task<BattlePassSeasonEntity> GetOrCreateActiveSeasonAsync(
        ulong guildId,
        int defaultDays,
        int maxLevel,
        int xpPerLevel,
        long premiumPrice,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var active = await conn.QuerySingleOrDefaultAsync<BattlePassSeasonEntity>(@"
            SELECT * FROM battlepass_seasons
            WHERE guild_id = @GuildId AND active = 1 AND ends_at > @Now
            ORDER BY id DESC
            LIMIT 1;",
            new { GuildId = guildId, Now = now });

        if (active is not null)
            return active;

        var rewards = GenerateDefaultSeasonRewards(maxLevel);
        var rewardsJson = JsonSerializer.Serialize(rewards);
        var endsAt = now.AddDays(Math.Clamp(defaultDays, 30, 60));

        await conn.ExecuteAsync(
            "UPDATE battlepass_seasons SET active = 0 WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO battlepass_seasons (
                guild_id, name, max_level, xp_per_level, premium_price,
                active, starts_at, ends_at, rewards_json
            ) VALUES (
                @GuildId, 'Сезон I: Кибер-Рассвет KRLZ', @MaxLevel, @XpPerLevel, @PremiumPrice,
                1, @StartsAt, @EndsAt, @RewardsJson
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                MaxLevel = maxLevel,
                XpPerLevel = xpPerLevel,
                PremiumPrice = premiumPrice,
                StartsAt = now,
                EndsAt = endsAt,
                RewardsJson = rewardsJson
            });

        return new BattlePassSeasonEntity(
            id, guildId, "Сезон I: Кибер-Рассвет KRLZ", maxLevel, xpPerLevel,
            premiumPrice, true, now, endsAt, rewardsJson);
    }

    public async Task<BattlePassProgressEntity> GetOrCreateUserProgressAsync(
        ulong guildId,
        long seasonId,
        ulong userId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(@"
            INSERT INTO battlepass_progress (
                guild_id, season_id, user_id, xp, level, is_premium,
                claimed_free_levels, claimed_premium_levels, updated_at
            ) VALUES (
                @GuildId, @SeasonId, @UserId, 0, 1, 0, '', '', @Now
            )
            ON CONFLICT(guild_id, season_id, user_id) DO NOTHING;",
            new { GuildId = guildId, SeasonId = seasonId, UserId = userId, Now = now });

        return await conn.QuerySingleAsync<BattlePassProgressEntity>(@"
            SELECT * FROM battlepass_progress
            WHERE guild_id = @GuildId AND season_id = @SeasonId AND user_id = @UserId;",
            new { GuildId = guildId, SeasonId = seasonId, UserId = userId });
    }

    public async Task<(int OldLevel, int NewLevel)> AddBattlePassXpAsync(
        ulong guildId,
        long seasonId,
        ulong userId,
        int xpDelta,
        int maxLevel,
        int xpPerLevel,
        CancellationToken ct = default)
    {
        var prog = await GetOrCreateUserProgressAsync(guildId, seasonId, userId, ct);
        var newXp = prog.Xp + Math.Max(0, xpDelta);
        var calcLevel = Math.Clamp((int)(newXp / Math.Max(1, xpPerLevel)) + 1, 1, maxLevel);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE battlepass_progress
            SET xp = @Xp,
                level = @Level,
                updated_at = @Now
            WHERE guild_id = @GuildId AND season_id = @SeasonId AND user_id = @UserId;",
            new { GuildId = guildId, SeasonId = seasonId, UserId = userId, Xp = newXp, Level = calcLevel, Now = DateTimeOffset.UtcNow });

        return (prog.Level, calcLevel);
    }

    public async Task<(bool Success, string Error)> UpgradeToPremiumAsync(
        ulong guildId,
        long seasonId,
        ulong userId,
        long price,
        CancellationToken ct = default)
    {
        var prog = await GetOrCreateUserProgressAsync(guildId, seasonId, userId, ct);
        if (prog.IsPremium)
            return (false, "У вас уже активирован Премиум-пропуск текущего сезона.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Price
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Price;",
            new { GuildId = guildId, UserId = userId, Price = price },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на балансе для покупки Премиум Батлпасса.");
        }

        await conn.ExecuteAsync(@"
            UPDATE battlepass_progress
            SET is_premium = 1,
                updated_at = @Now
            WHERE guild_id = @GuildId AND season_id = @SeasonId AND user_id = @UserId;",
            new { GuildId = guildId, SeasonId = seasonId, UserId = userId, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Price, 'battlepass_premium', @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Price = price, Desc = $"Покупка Премиум Батлпасса (сезон #{seasonId})", Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task SaveClaimedLevelsAsync(
        ulong guildId,
        long seasonId,
        ulong userId,
        string claimedFree,
        string claimedPremium,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE battlepass_progress
            SET claimed_free_levels = @Free,
                claimed_premium_levels = @Prem,
                updated_at = @Now
            WHERE guild_id = @GuildId AND season_id = @SeasonId AND user_id = @UserId;",
            new { GuildId = guildId, SeasonId = seasonId, UserId = userId, Free = claimedFree, Prem = claimedPremium, Now = DateTimeOffset.UtcNow });
    }

    private static List<BattlePassRewardNodeDto> GenerateDefaultSeasonRewards(int maxLevel)
    {
        var nodes = new List<BattlePassRewardNodeDto>();

        for (var lvl = 1; lvl <= maxLevel; lvl++)
        {
            if (lvl == 10)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "free", "title", 0, "Искатель KRLZ", 1, "Титул: Искатель KRLZ"));
            else if (lvl == 25)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "free", "background", 0, "nebula.png", 1, "Фон: Туманность"));
            else if (lvl == 50)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "free", "title", 0, "Хранитель Сезона", 1, "Титул: Хранитель Сезона"));
            else if (lvl % 5 == 0)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "free", "item", 0, "xp_elixir", 1, "Эликсир опыта x1"));
            else
                nodes.Add(new BattlePassRewardNodeDto(lvl, "free", "coins", 150 + lvl * 20L, null, 1, $"{150 + lvl * 20} монет"));

            if (lvl == 15)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "premium", "background", 0, "solar.png", 1, "Фон: Солнечное Затмение"));
            else if (lvl == 30)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "premium", "title", 0, "Архонт KRLZ", 1, "Титул: Архонт KRLZ"));
            else if (lvl == 40)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "premium", "item", 0, "lucky_amulet", 2, "Амулет удачи x2"));
            else if (lvl == 50)
                nodes.Add(new BattlePassRewardNodeDto(lvl, "premium", "background", 0, "void_gold.png", 1, "Фон: Золотая Бездна"));
            else
                nodes.Add(new BattlePassRewardNodeDto(lvl, "premium", "coins", 350 + lvl * 45L, null, 1, $"{350 + lvl * 45} монет"));
        }

        return nodes;
    }
}
