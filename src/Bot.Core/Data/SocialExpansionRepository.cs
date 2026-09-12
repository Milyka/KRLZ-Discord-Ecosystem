using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class SocialExpansionRepository(DbConnectionFactory dbFactory)
{
    public async Task<UserReputationEntity> GetOrCreateReputationAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO user_reputation (guild_id, user_id, reputation, positive_count, negative_count, updated_at)
            VALUES (@GuildId, @UserId, 0, 0, 0, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, Now = DateTimeOffset.UtcNow });

        return await conn.QuerySingleAsync<UserReputationEntity>(
            "SELECT * FROM user_reputation WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<(bool Success, string Message, long NewScore)> TryGiveReputationAsync(
        ulong guildId,
        ulong fromUserId,
        ulong toUserId,
        int delta,
        string comment,
        int cooldownHours,
        CancellationToken ct = default)
    {
        if (fromUserId == toUserId)
            return (false, "Нельзя изменять репутацию самому себе.", 0);

        var normDelta = delta >= 0 ? 1 : -1;
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(@"
            INSERT INTO user_reputation (guild_id, user_id, reputation, positive_count, negative_count, updated_at)
            VALUES (@GuildId, @UserId, 0, 0, 0, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = fromUserId, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO user_reputation (guild_id, user_id, reputation, positive_count, negative_count, updated_at)
            VALUES (@GuildId, @UserId, 0, 0, 0, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = toUserId, Now = now },
            transaction: tx);

        var giver = await conn.QuerySingleAsync<UserReputationEntity>(
            "SELECT * FROM user_reputation WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = fromUserId },
            transaction: tx);

        if (giver.LastGivenAt.HasValue && (now - giver.LastGivenAt.Value).TotalHours < cooldownHours)
        {
            var rem = giver.LastGivenAt.Value.AddHours(cooldownHours) - now;
            await tx.RollbackAsync(ct);
            return (false, $"Вы уже выдавали репутацию недавно. Следующая выдача доступна через **{Math.Max(1, (int)rem.TotalMinutes)} мин.**", 0);
        }

        await conn.ExecuteAsync(@"
            UPDATE user_reputation
            SET last_given_at = @Now, updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @FromUserId;",
            new { GuildId = guildId, FromUserId = fromUserId, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE user_reputation
            SET reputation = reputation + @Delta,
                positive_count = positive_count + @Pos,
                negative_count = negative_count + @Neg,
                updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @ToUserId;",
            new
            {
                GuildId = guildId,
                ToUserId = toUserId,
                Delta = normDelta,
                Pos = normDelta > 0 ? 1 : 0,
                Neg = normDelta < 0 ? 1 : 0,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO reputation_logs (guild_id, from_user_id, to_user_id, delta, comment, created_at)
            VALUES (@GuildId, @FromUserId, @ToUserId, @Delta, @Comment, @Now);",
            new
            {
                GuildId = guildId,
                FromUserId = fromUserId,
                ToUserId = toUserId,
                Delta = normDelta,
                Comment = string.IsNullOrWhiteSpace(comment) ? "Без комментария" : comment.Trim(),
                Now = now
            },
            transaction: tx);

        var newScore = await conn.ExecuteScalarAsync<long>(
            "SELECT reputation FROM user_reputation WHERE guild_id = @GuildId AND user_id = @ToUserId;",
            new { GuildId = guildId, ToUserId = toUserId },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, normDelta > 0 ? "Положительный отзыв записан!" : "Отрицательный отзыв записан!", newScore);
    }

    public async Task<IReadOnlyList<ReputationLogEntity>> GetReputationLogsAsync(
        ulong guildId,
        ulong userId,
        int limit = 10,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReputationLogEntity>(@"
            SELECT * FROM reputation_logs
            WHERE guild_id = @GuildId AND to_user_id = @UserId
            ORDER BY id DESC
            LIMIT @Limit;",
            new { GuildId = guildId, UserId = userId, Limit = limit });
        return rows.ToList();
    }

    public async Task SetAccentColorAsync(ulong guildId, ulong userId, string? colorHex, CancellationToken ct = default)
    {
        await GetOrCreateReputationAsync(guildId, userId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE user_reputation
            SET accent_color_hex = @ColorHex, updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, ColorHex = colorHex, Now = DateTimeOffset.UtcNow });
    }

    public async Task<FamilyProfileEntity> GetOrCreateFamilyProfileAsync(long marriageId, ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO family_profiles (marriage_id, guild_id, family_name, family_balance, love_xp, love_level, updated_at)
            VALUES (@MarriageId, @GuildId, 'Семья KRLZ', 0, 0, 1, @Now)
            ON CONFLICT(marriage_id) DO NOTHING;",
            new { MarriageId = marriageId, GuildId = guildId, Now = DateTimeOffset.UtcNow });

        return await conn.QuerySingleAsync<FamilyProfileEntity>(
            "SELECT * FROM family_profiles WHERE marriage_id = @MarriageId;",
            new { MarriageId = marriageId });
    }

    public async Task UpdateFamilyNameAsync(long marriageId, string familyName, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE family_profiles
            SET family_name = @Name, updated_at = @Now
            WHERE marriage_id = @MarriageId;",
            new { MarriageId = marriageId, Name = familyName.Trim(), Now = DateTimeOffset.UtcNow });
    }

    public async Task<bool> TryDepositFamilyAsync(
        long marriageId,
        ulong guildId,
        ulong userId,
        long amount,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            return false;

        await GetOrCreateFamilyProfileAsync(marriageId, guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Amount;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            UPDATE family_profiles
            SET family_balance = family_balance + @Amount, updated_at = @Now
            WHERE marriage_id = @MarriageId;",
            new { MarriageId = marriageId, Amount = amount, Now = DateTimeOffset.UtcNow },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Amount, 'family_deposit', 'Пополнение семейного банка', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Now = DateTimeOffset.UtcNow },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryWithdrawFamilyAsync(
        long marriageId,
        ulong guildId,
        ulong userId,
        long amount,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            return false;

        await GetOrCreateFamilyProfileAsync(marriageId, guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = await conn.ExecuteAsync(@"
            UPDATE family_profiles
            SET family_balance = family_balance - @Amount, updated_at = @Now
            WHERE marriage_id = @MarriageId AND family_balance >= @Amount;",
            new { MarriageId = marriageId, Amount = amount, Now = DateTimeOffset.UtcNow },
            transaction: tx);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, NULL, @UserId, @Amount, 'family_withdraw', 'Снятие из семейного банка', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Now = DateTimeOffset.UtcNow },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task AddFamilyLoveXpAsync(long marriageId, ulong guildId, long xpDelta, CancellationToken ct = default)
    {
        if (xpDelta <= 0)
            return;

        var profile = await GetOrCreateFamilyProfileAsync(marriageId, guildId, ct);
        var nextXp = profile.LoveXp + xpDelta;
        var nextLvl = Math.Max(1, (int)(nextXp / 250) + 1);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE family_profiles
            SET love_xp = @NextXp, love_level = @NextLvl, updated_at = @Now
            WHERE marriage_id = @MarriageId;",
            new { MarriageId = marriageId, NextXp = nextXp, NextLvl = nextLvl, Now = DateTimeOffset.UtcNow });
    }

    public async Task<IReadOnlyList<FamilyMemberEntity>> GetFamilyMembersAsync(long marriageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<FamilyMemberEntity>(
            "SELECT * FROM family_members WHERE marriage_id = @MarriageId ORDER BY id ASC;",
            new { MarriageId = marriageId });
        return rows.ToList();
    }

    public async Task<FamilyMemberEntity?> GetAdoptedRecordAsync(ulong guildId, ulong childUserId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FamilyMemberEntity>(
            "SELECT * FROM family_members WHERE guild_id = @GuildId AND child_user_id = @ChildUserId;",
            new { GuildId = guildId, ChildUserId = childUserId });
    }

    public async Task<(bool Success, string Message)> TryAdoptChildAsync(
        long marriageId,
        ulong guildId,
        ulong childUserId,
        int maxChildren,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var existingCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM family_members WHERE marriage_id = @MarriageId;",
            new { MarriageId = marriageId });

        if (existingCount >= maxChildren)
            return (false, $"В семье уже максимальное число детей ({maxChildren}).");

        var alreadyChild = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM family_members WHERE guild_id = @GuildId AND child_user_id = @ChildId;",
            new { GuildId = guildId, ChildId = childUserId });

        if (alreadyChild > 0)
            return (false, "Этот участник уже усыновлён в другой семье.");

        await conn.ExecuteAsync(@"
            INSERT INTO family_members (marriage_id, guild_id, child_user_id, role_label, adopted_at)
            VALUES (@MarriageId, @GuildId, @ChildId, 'Ребёнок', @Now);",
            new { MarriageId = marriageId, GuildId = guildId, ChildId = childUserId, Now = DateTimeOffset.UtcNow });

        return (true, "Участник официально принят в вашу семью!");
    }

    public async Task<bool> RemoveFamilyMemberAsync(ulong guildId, ulong childUserId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM family_members WHERE guild_id = @GuildId AND child_user_id = @ChildId;",
            new { GuildId = guildId, ChildId = childUserId });
        return rows > 0;
    }

    public async Task<BankAccountEntity> GetOrCreateBankAccountAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO bank_accounts (guild_id, user_id, deposit_balance, crystals, total_interest_earned, last_interest_at, updated_at)
            VALUES (@GuildId, @UserId, 0, 0, 0, @Now, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, Now = now });

        return await conn.QuerySingleAsync<BankAccountEntity>(
            "SELECT * FROM bank_accounts WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<bool> TryDepositBankAsync(ulong guildId, ulong userId, long amount, CancellationToken ct = default)
    {
        if (amount <= 0)
            return false;

        await GetOrCreateBankAccountAsync(guildId, userId, ct);
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Amount;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            UPDATE bank_accounts
            SET deposit_balance = deposit_balance + @Amount, updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Amount = amount, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Amount, 'bank_deposit', 'Вклад на депозитный счёт Банка KRLZ', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryWithdrawBankAsync(ulong guildId, ulong userId, long amount, CancellationToken ct = default)
    {
        if (amount <= 0)
            return false;

        await GetOrCreateBankAccountAsync(guildId, userId, ct);
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE bank_accounts
            SET deposit_balance = deposit_balance - @Amount, updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @UserId AND deposit_balance >= @Amount;",
            new { GuildId = guildId, UserId = userId, Amount = amount, Now = now },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, NULL, @UserId, @Amount, 'bank_withdraw', 'Снятие с депозитного счёта Банка KRLZ', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<(bool Success, string Message)> TryExchangeCrystalsAsync(
        ulong guildId,
        ulong userId,
        bool buyCrystals,
        long crystalCount,
        long ratePerCrystal,
        string currencySymbol,
        CancellationToken ct = default)
    {
        if (crystalCount <= 0 || ratePerCrystal <= 0)
            return (false, "Укажите положительное количество кристаллов.");

        await GetOrCreateBankAccountAsync(guildId, userId, ct);
        var totalCoins = crystalCount * ratePerCrystal;
        var now = DateTimeOffset.UtcNow;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (buyCrystals)
        {
            var deducted = await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance - @TotalCoins
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @TotalCoins;",
                new { GuildId = guildId, UserId = userId, TotalCoins = totalCoins },
                transaction: tx);

            if (deducted == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, $"Недостаточно средств на балансе. Требуется **{totalCoins:N0} {currencySymbol}**.");
            }

            await conn.ExecuteAsync(@"
                UPDATE bank_accounts
                SET crystals = crystals + @Crystals, updated_at = @Now
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userId, Crystals = crystalCount, Now = now },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @UserId, NULL, @TotalCoins, 'crystal_buy', @Desc, @Now);",
                new { GuildId = guildId, UserId = (ulong?)userId, TotalCoins = totalCoins, Desc = $"Покупка {crystalCount} кристаллов", Now = now },
                transaction: tx);

            await tx.CommitAsync(ct);
            return (true, $"Вы приобрели **{crystalCount:N0} 💎** за **{totalCoins:N0} {currencySymbol}**.");
        }
        else
        {
            var deducted = await conn.ExecuteAsync(@"
                UPDATE bank_accounts
                SET crystals = crystals - @Crystals, updated_at = @Now
                WHERE guild_id = @GuildId AND user_id = @UserId AND crystals >= @Crystals;",
                new { GuildId = guildId, UserId = userId, Crystals = crystalCount, Now = now },
                transaction: tx);

            if (deducted == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "На вашем банковском счёте недостаточно кристаллов 💎.");
            }

            await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @TotalCoins
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userId, TotalCoins = totalCoins },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @TotalCoins, 'crystal_sell', @Desc, @Now);",
                new { GuildId = guildId, UserId = (ulong?)userId, TotalCoins = totalCoins, Desc = $"Продажа {crystalCount} кристаллов", Now = now },
                transaction: tx);

            await tx.CommitAsync(ct);
            return (true, $"Вы обменяли **{crystalCount:N0} 💎** и получили **{totalCoins:N0} {currencySymbol}**.");
        }
    }

    public async Task<int> ProcessDueBankInterestAsync(
        double dailyPercent,
        long maxCap,
        CancellationToken ct = default)
    {
        if (dailyPercent <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var threshold = now.AddHours(-24);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var accounts = (await conn.QueryAsync<BankAccountEntity>(@"
            SELECT * FROM bank_accounts
            WHERE deposit_balance > 0 AND last_interest_at <= @Threshold;",
            new { Threshold = threshold })).ToList();

        var count = 0;
        foreach (var acc in accounts)
        {
            var interest = (long)Math.Floor(acc.DepositBalance * (dailyPercent / 100.0));
            if (maxCap > 0)
                interest = Math.Min(interest, maxCap);

            if (interest <= 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE bank_accounts SET last_interest_at = @Now WHERE guild_id = @GuildId AND user_id = @UserId;",
                    new { GuildId = acc.GuildId, UserId = acc.UserId, Now = now });
                continue;
            }

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(@"
                UPDATE bank_accounts
                SET deposit_balance = deposit_balance + @Interest,
                    total_interest_earned = total_interest_earned + @Interest,
                    last_interest_at = @Now,
                    updated_at = @Now
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = acc.GuildId, UserId = acc.UserId, Interest = interest, Now = now },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @Interest, 'bank_interest', 'Ежедневный процент по вкладу Банка KRLZ', @Now);",
                new { GuildId = acc.GuildId, UserId = (ulong?)acc.UserId, Interest = interest, Now = now },
                transaction: tx);

            await tx.CommitAsync(ct);
            count++;
        }

        return count;
    }

    public async Task<UserPetEntity?> GetUserPetAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserPetEntity>(
            "SELECT * FROM user_pets WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<(bool Success, string Message, UserPetEntity? Pet)> TryAdoptPetAsync(
        ulong guildId,
        ulong userId,
        string petCode,
        string petName,
        string rarity,
        long price,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var existing = await GetUserPetAsync(guildId, userId, ct);
        if (existing is not null)
            return (false, $"У вас уже есть питомец **{existing.Name}** (Ур. {existing.Level}).", existing);

        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Price
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Price;",
            new { GuildId = guildId, UserId = userId, Price = price },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Недостаточно средств для приручения питомца ({price:N0} {currencySymbol}).", null);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO user_pets (
                guild_id, user_id, pet_code, name, rarity, level, xp,
                satiety, is_active, expedition_ends_at, last_fed_at, created_at
            ) VALUES (
                @GuildId, @UserId, @PetCode, @Name, @Rarity, 1, 0,
                100, 1, NULL, @Now, @Now
            );",
            new { GuildId = guildId, UserId = userId, PetCode = petCode, Name = petName, Rarity = rarity, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Price, 'pet_adopt', @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Price = price, Desc = $"Приручение питомца {petName}", Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        var pet = await GetUserPetAsync(guildId, userId, ct);
        return (true, $"Вы приручили питомца **{petName}** (`{rarity.ToUpperInvariant()}`)!", pet);
    }

    public async Task<(bool Success, string Message)> TryFeedPetAsync(
        ulong guildId,
        ulong userId,
        long feedCost,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var pet = await GetUserPetAsync(guildId, userId, ct);
        if (pet is null)
            return (false, "У вас ещё нет питомца. Используйте `/pet adopt`!");

        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Cost
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Cost;",
            new { GuildId = guildId, UserId = userId, Cost = feedCost },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Недостаточно средств на кормление ({feedCost:N0} {currencySymbol}).");
        }

        var nextXp = pet.Xp + 35;
        var nextLvl = Math.Max(1, (int)(nextXp / 150) + 1);

        await conn.ExecuteAsync(@"
            UPDATE user_pets
            SET satiety = 100, xp = @NextXp, level = @NextLvl, last_fed_at = @Now
            WHERE id = @Id;",
            new { Id = pet.Id, NextXp = nextXp, NextLvl = nextLvl, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Cost, 'pet_feed', 'Кормление питомца', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Cost = feedCost, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, $"Питомец **{pet.Name}** накормлен до 100% (+35 XP, Уровень {nextLvl})!");
    }

    public async Task RenamePetAsync(ulong guildId, ulong userId, string newName, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE user_pets SET name = @Name WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, Name = newName.Trim() });
    }

    public async Task<(bool Success, string Message)> StartPetExpeditionAsync(
        ulong guildId,
        ulong userId,
        int hours,
        CancellationToken ct = default)
    {
        var pet = await GetUserPetAsync(guildId, userId, ct);
        if (pet is null)
            return (false, "У вас нет питомца. Приручите его через `/pet adopt`.");

        if (pet.ExpeditionEndsAt.HasValue)
        {
            if (pet.ExpeditionEndsAt.Value > DateTimeOffset.UtcNow)
                return (false, $"Питомец уже в экспедиции! Вернётся <t:{pet.ExpeditionEndsAt.Value.ToUnixTimeSeconds()}:R>.");
            return (false, "Питомец уже вернулся из похода! Сначала заберите награду через `/pet claim`.");
        }

        if (pet.Satiety < 30)
            return (false, $"Питомец слишком голоден ({pet.Satiety}%). Покормите его через `/pet feed`.");

        var endsAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, hours));
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE user_pets
            SET expedition_ends_at = @EndsAt,
                satiety = MAX(0, satiety - 25)
            WHERE id = @Id;",
            new { Id = pet.Id, EndsAt = endsAt });

        return (true, $"Питомец **{pet.Name}** отправился в экспедицию! Возвращение <t:{endsAt.ToUnixTimeSeconds()}:R>.");
    }

    public async Task<(bool Success, string Message, long CoinsWon, long XpWon)> ClaimPetExpeditionAsync(
        ulong guildId,
        ulong userId,
        long baseCoins,
        long baseXp,
        CancellationToken ct = default)
    {
        var pet = await GetUserPetAsync(guildId, userId, ct);
        if (pet is null || !pet.ExpeditionEndsAt.HasValue)
            return (false, "Ваш питомец сейчас не находится в экспедиции.", 0, 0);

        var now = DateTimeOffset.UtcNow;
        if (pet.ExpeditionEndsAt.Value > now)
            return (false, $"Экспедиция ещё не завершена! Окончание <t:{pet.ExpeditionEndsAt.Value.ToUnixTimeSeconds()}:R>.", 0, 0);

        var rarityMult = pet.Rarity switch
        {
            "legendary" => 2.0,
            "epic" => 1.5,
            "rare" => 1.25,
            _ => 1.0
        };
        var levelMult = 1.0 + (pet.Level - 1) * 0.08;
        var coins = (long)Math.Round(baseCoins * rarityMult * levelMult);
        var userXp = (long)Math.Round(baseXp * rarityMult * levelMult);
        var nextPetXp = pet.Xp + 90;
        var nextPetLvl = Math.Max(1, (int)(nextPetXp / 150) + 1);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var updated = await conn.ExecuteAsync(@"
            UPDATE user_pets
            SET expedition_ends_at = NULL,
                xp = @NextPetXp,
                level = @NextPetLvl
            WHERE id = @Id AND expedition_ends_at IS NOT NULL;",
            new { Id = pet.Id, NextPetXp = nextPetXp, NextPetLvl = nextPetLvl },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Награда за экспедицию уже получена.", 0, 0);
        }

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Coins,
                xp = xp + @Xp,
                level = MAX(level, @CalcLevel)
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                Coins = coins,
                Xp = userXp,
                CalcLevel = 1
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, NULL, @UserId, @Coins, 'pet_expedition', @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Coins = coins, Desc = $"Награда экспедиции питомца {pet.Name}", Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, $"Питомец **{pet.Name}** (Ур. {nextPetLvl}) вернулся из похода!", coins, userXp);
    }

    public async Task<long> CreateDuelAsync(
        ulong guildId,
        ulong channelId,
        ulong challengerId,
        ulong opponentId,
        long bet,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO duel_sessions (
                guild_id, channel_id, message_id, challenger_id, opponent_id,
                bet, challenger_hp, opponent_hp, turn_user_id, round_num,
                last_log, status, winner_id, expires_at, created_at
            ) VALUES (
                @GuildId, @ChannelId, 0, @ChallengerId, @OpponentId,
                @Bet, 100, 100, @ChallengerId, 1,
                'Дуэль начинается! Выберите удар.', 'pending', NULL, @ExpiresAt, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChannelId = channelId,
                ChallengerId = challengerId,
                OpponentId = opponentId,
                Bet = bet,
                ExpiresAt = expiresAt,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task UpdateDuelMessageIdAsync(long duelId, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE duel_sessions SET message_id = @MessageId WHERE id = @Id;",
            new { Id = duelId, MessageId = messageId });
    }

    public async Task<DuelSessionEntity?> GetDuelByIdAsync(long duelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DuelSessionEntity>(
            "SELECT * FROM duel_sessions WHERE id = @Id;",
            new { Id = duelId });
    }

    public async Task<(bool Success, string Error)> TryAcceptDuelAsync(long duelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var duel = await conn.QuerySingleOrDefaultAsync<DuelSessionEntity>(
            "SELECT * FROM duel_sessions WHERE id = @Id;",
            new { Id = duelId },
            transaction: tx);

        if (duel is null || duel.Status != "pending")
        {
            await tx.RollbackAsync(ct);
            return (false, "Вызов на дуэль уже неактуален.");
        }

        if (duel.Bet > 0)
        {
            var r1 = await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance - @Bet
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Bet;",
                new { duel.GuildId, UserId = duel.ChallengerId, duel.Bet },
                transaction: tx);

            if (r1 == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "У инициатора дуэли недостаточно средств для ставки.");
            }

            var r2 = await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance - @Bet
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Bet;",
                new { duel.GuildId, UserId = duel.OpponentId, duel.Bet },
                transaction: tx);

            if (r2 == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "На вашем балансе недостаточно средств для принятия ставки.");
            }

            var now = DateTimeOffset.UtcNow;
            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @U1, NULL, @Bet, 'duel_bet', 'Ставка на PvP-дуэль', @Now),
                       (@GuildId, @U2, NULL, @Bet, 'duel_bet', 'Ставка на PvP-дуэль', @Now);",
                new { duel.GuildId, U1 = (ulong?)duel.ChallengerId, U2 = (ulong?)duel.OpponentId, duel.Bet, Now = now },
                transaction: tx);
        }

        var rows = await conn.ExecuteAsync(@"
            UPDATE duel_sessions
            SET status = 'active', expires_at = @ExpiresAt
            WHERE id = @Id AND status = 'pending';",
            new { Id = duelId, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) },
            transaction: tx);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Не удалось запустить дуэль.");
        }

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task UpdateDuelStateAsync(
        long duelId,
        int challengerHp,
        int opponentHp,
        ulong nextTurnUserId,
        int roundNum,
        string lastLog,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE duel_sessions
            SET challenger_hp = @ChallengerHp,
                opponent_hp = @OpponentHp,
                turn_user_id = @NextTurn,
                round_num = @RoundNum,
                last_log = @LastLog,
                expires_at = @ExpiresAt
            WHERE id = @Id AND status = 'active';",
            new
            {
                Id = duelId,
                ChallengerHp = challengerHp,
                OpponentHp = opponentHp,
                NextTurn = nextTurnUserId,
                RoundNum = roundNum,
                LastLog = lastLog,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            });
    }

    public async Task<bool> TryFinishDuelAsync(
        long duelId,
        ulong winnerId,
        int challengerHp,
        int opponentHp,
        string finalLog,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var duel = await conn.QuerySingleOrDefaultAsync<DuelSessionEntity>(
            "SELECT * FROM duel_sessions WHERE id = @Id;",
            new { Id = duelId },
            transaction: tx);

        if (duel is null || duel.Status != "active")
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var updated = await conn.ExecuteAsync(@"
            UPDATE duel_sessions
            SET status = 'finished',
                winner_id = @WinnerId,
                challenger_hp = @ChallengerHp,
                opponent_hp = @OpponentHp,
                last_log = @FinalLog
            WHERE id = @Id AND status = 'active';",
            new { Id = duelId, WinnerId = (ulong?)winnerId, ChallengerHp = challengerHp, OpponentHp = opponentHp, FinalLog = finalLog },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var pot = duel.Bet * 2;
        if (pot > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Pot
                WHERE guild_id = @GuildId AND user_id = @WinnerId;",
                new { duel.GuildId, WinnerId = winnerId, Pot = pot },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @WinnerId, @Pot, 'duel_win', 'Победа в PvP-дуэли', @Now);",
                new { duel.GuildId, WinnerId = (ulong?)winnerId, Pot = pot, Now = DateTimeOffset.UtcNow },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> CancelPendingDuelAsync(long duelId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "UPDATE duel_sessions SET status = 'cancelled' WHERE id = @Id AND status = 'pending';",
            new { Id = duelId });
        return rows > 0;
    }

    public async Task<int> RefundExpiredDuelsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE duel_sessions SET status = 'cancelled' WHERE status = 'pending' AND expires_at <= @Now;",
            new { Now = now });

        var expiredActive = (await conn.QueryAsync<DuelSessionEntity>(
            "SELECT * FROM duel_sessions WHERE status = 'active' AND expires_at <= @Now;",
            new { Now = now })).ToList();

        var count = 0;
        foreach (var d in expiredActive)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var rows = await conn.ExecuteAsync(
                "UPDATE duel_sessions SET status = 'cancelled' WHERE id = @Id AND status = 'active';",
                new { d.Id },
                transaction: tx);

            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            if (d.Bet > 0)
            {
                await conn.ExecuteAsync(
                    "UPDATE users SET balance = balance + @Bet WHERE guild_id = @GuildId AND user_id IN (@U1, @U2);",
                    new { d.GuildId, d.Bet, U1 = d.ChallengerId, U2 = d.OpponentId },
                    transaction: tx);

                await conn.ExecuteAsync(@"
                    INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                    VALUES (@GuildId, NULL, @U1, @Bet, 'duel_refund', 'Возврат ставки неактивной дуэли', @Now),
                           (@GuildId, NULL, @U2, @Bet, 'duel_refund', 'Возврат ставки неактивной дуэли', @Now);",
                    new { d.GuildId, U1 = (ulong?)d.ChallengerId, U2 = (ulong?)d.OpponentId, d.Bet, Now = now },
                    transaction: tx);
            }

            await tx.CommitAsync(ct);
            count++;
        }

        return count;
    }

    public async Task<MinesSessionEntity?> GetActiveMinesSessionAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MinesSessionEntity>(@"
            SELECT * FROM mines_sessions
            WHERE guild_id = @GuildId AND user_id = @UserId AND status = 'playing'
            ORDER BY id DESC LIMIT 1;",
            new { GuildId = guildId, UserId = userId });
    }

    public async Task<MinesSessionEntity?> GetMinesSessionByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<MinesSessionEntity>(
            "SELECT * FROM mines_sessions WHERE id = @Id;",
            new { Id = id });
    }

    public async Task<(bool Success, string Error, MinesSessionEntity? Session)> TryCreateMinesSessionAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        long bet,
        int bombCount,
        string bombsJson,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users SET balance = balance - @Bet
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Bet;",
            new { GuildId = guildId, UserId = userId, Bet = bet },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на балансе для указанной ставки.", null);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Bet, 'mines_bet', 'Ставка в игре Сапёр (/mines)', @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Bet = bet, Now = now },
            transaction: tx);

        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO mines_sessions (
                guild_id, user_id, channel_id, message_id, bet,
                bomb_count, bombs_json, revealed_json, status, created_at, updated_at
            ) VALUES (
                @GuildId, @UserId, @ChannelId, 0, @Bet,
                @BombCount, @BombsJson, '[]', 'playing', @Now, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ChannelId = channelId,
                Bet = bet,
                BombCount = bombCount,
                BombsJson = bombsJson,
                Now = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        var session = await GetMinesSessionByIdAsync(id, ct);
        return (true, string.Empty, session);
    }

    public async Task UpdateMinesMessageIdAsync(long id, ulong messageId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE mines_sessions SET message_id = @MessageId WHERE id = @Id;",
            new { Id = id, MessageId = messageId });
    }

    public async Task UpdateMinesRevealedAsync(long id, string revealedJson, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            UPDATE mines_sessions
            SET revealed_json = @RevealedJson, updated_at = @Now
            WHERE id = @Id AND status = 'playing';",
            new { Id = id, RevealedJson = revealedJson, Now = DateTimeOffset.UtcNow });
    }

    public async Task<bool> TryFinishMinesSessionAsync(
        long id,
        string finalStatus,
        string revealedJson,
        long payout,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<MinesSessionEntity>(
            "SELECT * FROM mines_sessions WHERE id = @Id;",
            new { Id = id },
            transaction: tx);

        if (session is null || session.Status != "playing")
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var rows = await conn.ExecuteAsync(@"
            UPDATE mines_sessions
            SET status = @Status, revealed_json = @RevealedJson, updated_at = @Now
            WHERE id = @Id AND status = 'playing';",
            new { Id = id, Status = finalStatus, RevealedJson = revealedJson, Now = now },
            transaction: tx);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        if (payout > 0 && finalStatus == "won")
        {
            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Payout
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { session.GuildId, session.UserId, Payout = payout },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @Payout, 'mines_win', 'Выигрыш в игре Сапёр (/mines)', @Now);",
                new { session.GuildId, UserId = (ulong?)session.UserId, Payout = payout, Now = now },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<(bool Success, long RefundAmount, string Error)> TryCancelAndRefundMinesSessionAsync(
        long sessionId,
        ulong userId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var session = await conn.QuerySingleOrDefaultAsync<MinesSessionEntity>(
            "SELECT * FROM mines_sessions WHERE id = @Id;",
            new { Id = sessionId },
            transaction: tx);

        if (session is null)
        {
            await tx.RollbackAsync(ct);
            return (false, 0, "Сессия игры не найдена.");
        }

        if (session.UserId != userId)
        {
            await tx.RollbackAsync(ct);
            return (false, 0, "Вы не являетесь владельцем этой игры.");
        }

        if (session.Status != "playing")
        {
            await tx.RollbackAsync(ct);
            return (false, 0, "Эта партия уже завершена.");
        }

        var updated = await conn.ExecuteAsync(@"
            UPDATE mines_sessions
            SET status = 'refunded', updated_at = @Now
            WHERE id = @Id AND status = 'playing';",
            new { Id = sessionId, Now = now },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, 0, "Не удалось отменить партию.");
        }

        if (session.Bet > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Bet
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { session.GuildId, session.UserId, session.Bet },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @UserId, @Bet, 'mines_refund', 'Возврат ставки отменённой партии Сапёр', @Now);",
                new { session.GuildId, UserId = (ulong?)session.UserId, session.Bet, Now = now },
                transaction: tx);
        }

        await tx.CommitAsync(ct);
        return (true, session.Bet, string.Empty);
    }

    public async Task<int> RefundInterruptedMinesSessionsOnStartupAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var pendingSessions = (await conn.QueryAsync<MinesSessionEntity>(
            "SELECT * FROM mines_sessions WHERE status = 'playing';")).ToList();

        if (pendingSessions.Count == 0)
            return 0;

        var count = 0;
        foreach (var session in pendingSessions)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var updated = await conn.ExecuteAsync(@"
                UPDATE mines_sessions
                SET status = 'refunded', updated_at = @Now
                WHERE id = @Id AND status = 'playing';",
                new { session.Id, Now = now },
                transaction: tx);

            if (updated > 0)
            {
                if (session.Bet > 0)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE users SET balance = balance + @Bet
                        WHERE guild_id = @GuildId AND user_id = @UserId;",
                        new { session.GuildId, session.UserId, session.Bet },
                        transaction: tx);

                    await conn.ExecuteAsync(@"
                        INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                        VALUES (@GuildId, NULL, @UserId, @Bet, 'mines_refund', 'Возврат ставки Сапёр после перезапуска бота', @Now);",
                        new { session.GuildId, UserId = (ulong?)session.UserId, session.Bet, Now = now },
                        transaction: tx);
                }

                await tx.CommitAsync(ct);
                count++;
            }
            else
            {
                await tx.RollbackAsync(ct);
            }
        }

        return count;
    }
}
