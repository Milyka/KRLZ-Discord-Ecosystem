using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class AchievementRepository(DbConnectionFactory dbFactory)
{
    public async Task EnsureDefaultAchievementsAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM achievements WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        if (count >= 17)
            return;

        var now = DateTimeOffset.UtcNow;
        var defaults = new (string Code, string Name, string Desc, string EventType, long Target, string Rarity, string Icon, long Coins, long Xp)[]
        {
            ("msg_100", "Собеседник KRLZ", "Отправить 100 сообщений в чатах сервера", "messages", 100, "common", "CHAT", 250, 150),
            ("msg_1000", "Голос сообщества", "Отправить 1 000 сообщений на сервере", "messages", 1000, "rare", "MEGAPHONE", 1200, 600),
            ("voice_60", "Голосовой старт", "Провести 60 минут в голосовых каналах", "voice_minutes", 60, "common", "MIC", 300, 200),
            ("voice_600", "Радиоведущий KRLZ", "Провести 10 часов в голосовых каналах", "voice_minutes", 600, "epic", "HEADPHONES", 2500, 1200),
            ("wealth_10k", "Первая вершина капитала", "Накопить 10 000 валюты сервера", "balance", 10000, "rare", "COIN", 1000, 400),
            ("wealth_100k", "Магнат KRLZ", "Накопить 100 000 валюты сервера", "balance", 100000, "legendary", "CROWN", 5000, 2500),
            ("marriage_1", "Семейные узы", "Заключить брачный союз на сервере", "marriage", 1, "common", "RING", 400, 200),
            ("casino_win_10", "Любимец фортуны", "Победить 10 раз в играх казино", "casino_wins", 10, "rare", "DICE", 800, 400),
            ("clan_join", "Братство KRLZ", "Вступить в клан или основать собственный", "clan_member", 1, "common", "SHIELD", 350, 200),
            ("fish_25", "Опытный рыболов", "Поймать 25 рыб на рыбалке", "fish_caught", 25, "rare", "FISH", 900, 450),
            ("fish_legend", "Охотник глубин", "Поймать легендарный трофей на рыбалке", "fish_legendary", 1, "legendary", "TRIDENT", 4000, 2000),
            ("mafia_win_5", "Дон KRLZ", "Победить 5 раз в настольной игре Мафия (/mafia)", "mafia_wins", 5, "epic", "MASK", 1800, 900),
            ("bunker_win_5", "Инженер Бункера", "Выжить и попасть в Бункер 5 раз (/bunker)", "bunker_wins", 5, "epic", "BUNKER", 1800, 900),
            ("spyfall_win_3", "Агент под прикрытием", "Победить 3 раза в игре Шпион (/spyfall)", "spyfall_wins", 3, "rare", "SPY", 1200, 600),
            ("liarsbar_win_5", "Король Блефа", "Выжить 5 раз за столом Бара Лжецов (/liarsbar)", "liarsbar_wins", 5, "epic", "REVOLVER", 2000, 950),
            ("raid_slayer_1", "Гроза Титанов", "Принять участие в победе над Рейд-Боссом (/raid)", "raid_boss_kills", 1, "rare", "SWORD", 1500, 700),
            ("party_master_15", "Душа Вечеринки KRLZ", "Победить 15 раз в любых настольных и компанейских играх", "party_wins", 15, "legendary", "TROPHY", 5000, 2500)
        };

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var a in defaults)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO achievements (
                    guild_id, code, name, description, event_type, target_value,
                    rarity, badge_icon, reward_coins, reward_xp, created_at
                ) VALUES (
                    @GuildId, @Code, @Name, @Description, @EventType, @TargetValue,
                    @Rarity, @BadgeIcon, @RewardCoins, @RewardXp, @CreatedAt
                )
                ON CONFLICT(guild_id, code) DO NOTHING;",
                new
                {
                    GuildId = guildId,
                    a.Code,
                    a.Name,
                    Description = a.Desc,
                    a.EventType,
                    TargetValue = a.Target,
                    a.Rarity,
                    BadgeIcon = a.Icon,
                    RewardCoins = a.Coins,
                    RewardXp = a.Xp,
                    CreatedAt = now
                },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AchievementEntity>> ProcessProgressAsync(
        ulong guildId,
        ulong userId,
        string eventType,
        long delta,
        bool isAbsoluteValue,
        CancellationToken ct = default)
    {
        await EnsureDefaultAchievementsAsync(guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var matching = (await conn.QueryAsync<AchievementEntity>(@"
            SELECT * FROM achievements
            WHERE guild_id = @GuildId AND event_type = @EventType;",
            new { GuildId = guildId, EventType = eventType },
            transaction: tx)).ToList();

        if (matching.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return [];
        }

        var newlyUnlocked = new List<AchievementEntity>();
        var now = DateTimeOffset.UtcNow;

        foreach (var ach in matching)
        {
            var current = await conn.QuerySingleOrDefaultAsync<UserAchievementEntity>(@"
                SELECT * FROM user_achievements
                WHERE guild_id = @GuildId AND user_id = @UserId AND achievement_id = @AchievementId;",
                new { GuildId = guildId, UserId = userId, AchievementId = ach.Id },
                transaction: tx);

            if (current is not null && current.Unlocked)
                continue;

            var newVal = isAbsoluteValue
                ? delta
                : (current?.CurrentValue ?? 0) + delta;

            var unlocked = newVal >= ach.TargetValue;
            DateTimeOffset? unlockedAt = unlocked ? now : null;

            await conn.ExecuteAsync(@"
                INSERT INTO user_achievements (
                    guild_id, user_id, achievement_id, current_value, unlocked, equipped_slot, unlocked_at, updated_at
                ) VALUES (
                    @GuildId, @UserId, @AchievementId, @CurrentValue, @Unlocked, NULL, @UnlockedAt, @Now
                )
                ON CONFLICT(guild_id, user_id, achievement_id) DO UPDATE SET
                    current_value = @CurrentValue,
                    unlocked = @Unlocked,
                    unlocked_at = COALESCE(user_achievements.unlocked_at, @UnlockedAt),
                    updated_at = @Now;",
                new
                {
                    GuildId = guildId,
                    UserId = userId,
                    AchievementId = ach.Id,
                    CurrentValue = newVal,
                    Unlocked = unlocked ? 1 : 0,
                    UnlockedAt = unlockedAt,
                    Now = now
                },
                transaction: tx);

            if (unlocked)
                newlyUnlocked.Add(ach);
        }

        await tx.CommitAsync(ct);
        return newlyUnlocked;
    }

    public async Task<IReadOnlyList<UserAchievementView>> GetUserAchievementsAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await EnsureDefaultAchievementsAsync(guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);

        var defs = (await conn.QueryAsync<AchievementEntity>(
            "SELECT * FROM achievements WHERE guild_id = @GuildId ORDER BY id ASC;",
            new { GuildId = guildId })).ToList();

        var userRows = (await conn.QueryAsync<UserAchievementEntity>(@"
            SELECT * FROM user_achievements
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId })).ToDictionary(x => x.AchievementId);

        var list = new List<UserAchievementView>(defs.Count);
        foreach (var d in defs)
        {
            if (!userRows.TryGetValue(d.Id, out var prog))
            {
                prog = new UserAchievementEntity(0, guildId, userId, d.Id, 0, false, null, null, DateTimeOffset.UtcNow);
            }

            list.Add(new UserAchievementView(d, prog));
        }

        return list;
    }

    public async Task<IReadOnlyList<AchievementEntity>> GetEquippedBadgesAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AchievementEntity>(@"
            SELECT a.*
            FROM user_achievements ua
            INNER JOIN achievements a ON a.id = ua.achievement_id
            WHERE ua.guild_id = @GuildId
              AND ua.user_id = @UserId
              AND ua.unlocked = 1
              AND ua.equipped_slot IS NOT NULL
            ORDER BY ua.equipped_slot ASC
            LIMIT 3;",
            new { GuildId = guildId, UserId = userId });

        return rows.ToList();
    }

    public async Task<(bool Success, string Error)> EquipBadgeAsync(
        ulong guildId,
        ulong userId,
        string code,
        int slot,
        CancellationToken ct = default)
    {
        if (slot is < 1 or > 3)
            return (false, "Слот значка должен быть от 1 до 3.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var ach = await conn.QuerySingleOrDefaultAsync<AchievementEntity>(@"
            SELECT * FROM achievements
            WHERE guild_id = @GuildId AND LOWER(code) = LOWER(@Code);",
            new { GuildId = guildId, Code = code.Trim() });

        if (ach is null)
            return (false, "Достижение с таким кодом не найдено.");

        var ua = await conn.QuerySingleOrDefaultAsync<UserAchievementEntity>(@"
            SELECT * FROM user_achievements
            WHERE guild_id = @GuildId AND user_id = @UserId AND achievement_id = @AchId AND unlocked = 1;",
            new { GuildId = guildId, UserId = userId, AchId = ach.Id });

        if (ua is null)
            return (false, "Это достижение ещё не разблокировано вами.");

        await conn.ExecuteAsync(@"
            UPDATE user_achievements
            SET equipped_slot = NULL
            WHERE guild_id = @GuildId AND user_id = @UserId AND equipped_slot = @Slot;",
            new { GuildId = guildId, UserId = userId, Slot = slot });

        await conn.ExecuteAsync(@"
            UPDATE user_achievements
            SET equipped_slot = @Slot, updated_at = @Now
            WHERE id = @Id;",
            new { ua.Id, Slot = slot, Now = DateTimeOffset.UtcNow });

        return (true, string.Empty);
    }

    public async Task<(bool Success, string Message)> EquipBadgesAsync(
        ulong guildId,
        ulong userId,
        IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        if (codes.Count > 5)
            return (false, "Можно выбрать не более 5 значков для карточки профиля.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(@"
            UPDATE user_achievements
            SET equipped_slot = NULL
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId },
            transaction: tx);

        var slot = 1;
        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ach = await conn.QuerySingleOrDefaultAsync<AchievementEntity>(
                "SELECT * FROM achievements WHERE guild_id = @GuildId AND LOWER(code) = LOWER(@Code);",
                new { GuildId = guildId, Code = code.Trim() },
                transaction: tx);

            if (ach is null)
            {
                await tx.RollbackAsync(ct);
                return (false, $"Достижение с кодом `{code}` не найдено.");
            }

            var updated = await conn.ExecuteAsync(@"
                UPDATE user_achievements
                SET equipped_slot = @Slot
                WHERE guild_id = @GuildId AND user_id = @UserId AND achievement_id = @AchId AND unlocked = 1;",
                new { GuildId = guildId, UserId = userId, AchId = ach.Id, Slot = slot },
                transaction: tx);

            if (updated == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, $"Достижение **{ach.Name}** ещё не разблокировано вами.");
            }

            slot++;
        }

        await tx.CommitAsync(ct);
        return (true, $"Установлено значков профиля: **{slot - 1}**.");
    }
}
