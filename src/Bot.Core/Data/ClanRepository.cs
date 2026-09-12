using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class ClanRepository(DbConnectionFactory dbFactory)
{
    public async Task<ClanEntity?> GetByIdAsync(ulong guildId, long clanId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClanEntity>(
            "SELECT * FROM clans WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = clanId });
    }

    public async Task<ClanEntity?> GetByNameOrTagAsync(ulong guildId, string query, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var clean = query.Trim();
        return await conn.QuerySingleOrDefaultAsync<ClanEntity>(@"
            SELECT * FROM clans
            WHERE guild_id = @GuildId
              AND (LOWER(name) = LOWER(@Query) OR LOWER(tag) = LOWER(@Query));",
            new { GuildId = guildId, Query = clean });
    }

    public async Task<(ClanEntity Clan, ClanMemberEntity Member)?> GetUserClanAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var member = await conn.QuerySingleOrDefaultAsync<ClanMemberEntity>(
            "SELECT * FROM clan_members WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });

        if (member is null)
            return null;

        var clan = await conn.QuerySingleOrDefaultAsync<ClanEntity>(
            "SELECT * FROM clans WHERE id = @ClanId AND guild_id = @GuildId;",
            new { ClanId = member.ClanId, GuildId = guildId });

        return clan is null ? null : (clan, member);
    }

    public async Task<IReadOnlyList<ClanMemberEntity>> GetMembersAsync(long clanId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClanMemberEntity>(@"
            SELECT * FROM clan_members
            WHERE clan_id = @ClanId
            ORDER BY CASE rank
                WHEN 'leader' THEN 1
                WHEN 'officer' THEN 2
                ELSE 3
            END, contributed_points DESC;",
            new { ClanId = clanId });
        return rows.ToList();
    }

    public async Task<(bool Success, string Error, ClanEntity? Clan)> CreateClanWithPaymentAsync(
        ulong guildId,
        ulong leaderId,
        string name,
        string tag,
        string description,
        ulong roleId,
        ulong channelId,
        string colorHex,
        int baseMemberLimit,
        int baseOfficerSlots,
        long price,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existingMember = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM clan_members WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = leaderId },
            transaction: tx);

        if (existingMember > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Вы уже состоите в клане.", null);
        }

        var duplicate = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM clans
            WHERE guild_id = @GuildId
              AND (LOWER(name) = LOWER(@Name) OR LOWER(tag) = LOWER(@Tag));",
            new { GuildId = guildId, Name = name, Tag = tag },
            transaction: tx);

        if (duplicate > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Клан с таким названием или тегом уже существует.", null);
        }

        var now = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = leaderId, Now = now },
            transaction: tx);

        if (price > 0)
        {
            var deducted = await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance - @Price
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Price;",
                new { GuildId = guildId, UserId = leaderId, Price = price },
                transaction: tx);

            if (deducted == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "Недостаточно средств на балансе для создания клана.", null);
            }

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @UserId, NULL, @Price, 'clan_create', @Desc, @Now);",
                new { GuildId = guildId, UserId = (ulong?)leaderId, Price = price, Desc = $"Создание клана [{tag}] {name}", Now = now },
                transaction: tx);
        }

        var clanId = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO clans (
                guild_id, name, tag, description, leader_id, role_id, channel_id,
                treasury, points, member_limit, officer_slots, earn_bonus_percent,
                color_hex, badge_code, created_at
            ) VALUES (
                @GuildId, @Name, @Tag, @Description, @LeaderId, @RoleId, @ChannelId,
                0, 0, @MemberLimit, @OfficerSlots, 0,
                @ColorHex, 'SHIELD', @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                Name = name,
                Tag = tag,
                Description = description,
                LeaderId = leaderId,
                RoleId = roleId,
                ChannelId = channelId,
                MemberLimit = baseMemberLimit,
                OfficerSlots = baseOfficerSlots,
                ColorHex = colorHex,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO clan_members (guild_id, clan_id, user_id, rank, contributed_coins, contributed_points, joined_at)
            VALUES (@GuildId, @ClanId, @UserId, 'leader', 0, 0, @Now);",
            new { GuildId = guildId, ClanId = clanId, UserId = leaderId, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);

        var entity = new ClanEntity(
            clanId, guildId, name, tag, description, leaderId, roleId, channelId,
            0, 0, baseMemberLimit, baseOfficerSlots, 0, colorHex, "SHIELD", now);

        return (true, string.Empty, entity);
    }

    public async Task<(bool Success, string Error)> AddMemberAsync(ulong guildId, long clanId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var clan = await conn.QuerySingleOrDefaultAsync<ClanEntity>(
            "SELECT * FROM clans WHERE id = @ClanId AND guild_id = @GuildId;",
            new { ClanId = clanId, GuildId = guildId },
            transaction: tx);

        if (clan is null)
        {
            await tx.RollbackAsync(ct);
            return (false, "Клан больше не существует.");
        }

        var alreadyInClan = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM clan_members WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId },
            transaction: tx);

        if (alreadyInClan > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Вы уже состоите в клане.");
        }

        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM clan_members WHERE clan_id = @ClanId;",
            new { ClanId = clanId },
            transaction: tx);

        if (count >= clan.MemberLimit)
        {
            await tx.RollbackAsync(ct);
            return (false, $"В клане достигнут лимит участников ({clan.MemberLimit}).");
        }

        await conn.ExecuteAsync(@"
            INSERT INTO clan_members (guild_id, clan_id, user_id, rank, contributed_coins, contributed_points, joined_at)
            VALUES (@GuildId, @ClanId, @UserId, 'member', 0, 0, @Now);",
            new { GuildId = guildId, ClanId = clanId, UserId = userId, Now = DateTimeOffset.UtcNow },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<bool> RemoveMemberAsync(ulong guildId, long clanId, ulong userId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM clan_members WHERE guild_id = @GuildId AND clan_id = @ClanId AND user_id = @UserId;",
            new { GuildId = guildId, ClanId = clanId, UserId = userId });
        return rows > 0;
    }

    public async Task<bool> UpdateMemberRankAsync(ulong guildId, long clanId, ulong userId, string newRank, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            UPDATE clan_members
            SET rank = @Rank
            WHERE guild_id = @GuildId AND clan_id = @ClanId AND user_id = @UserId;",
            new { GuildId = guildId, ClanId = clanId, UserId = userId, Rank = newRank });
        return rows > 0;
    }

    public async Task TransferLeadershipAsync(ulong guildId, long clanId, ulong oldLeaderId, ulong newLeaderId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE clans SET leader_id = @NewLeader WHERE id = @ClanId AND guild_id = @GuildId;",
            new { ClanId = clanId, GuildId = guildId, NewLeader = newLeaderId },
            transaction: tx);

        await conn.ExecuteAsync(
            "UPDATE clan_members SET rank = 'officer' WHERE clan_id = @ClanId AND user_id = @OldLeader;",
            new { ClanId = clanId, OldLeader = oldLeaderId },
            transaction: tx);

        await conn.ExecuteAsync(
            "UPDATE clan_members SET rank = 'leader' WHERE clan_id = @ClanId AND user_id = @NewLeader;",
            new { ClanId = clanId, NewLeader = newLeaderId },
            transaction: tx);

        await tx.CommitAsync(ct);
    }

    public async Task<(bool Success, string Error)> DepositToTreasuryAsync(
        ulong guildId,
        long clanId,
        ulong userId,
        long amount,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            return (false, "Сумма взноса должна быть положительной.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Amount
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Amount;",
            new { GuildId = guildId, UserId = userId, Amount = amount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на личном балансе.");
        }

        await conn.ExecuteAsync(@"
            UPDATE clans
            SET treasury = treasury + @Amount
            WHERE id = @ClanId AND guild_id = @GuildId;",
            new { ClanId = clanId, GuildId = guildId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE clan_members
            SET contributed_coins = contributed_coins + @Amount
            WHERE clan_id = @ClanId AND user_id = @UserId;",
            new { ClanId = clanId, UserId = userId, Amount = amount },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Amount, 'clan_deposit', @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Desc = $"Взнос в казну клана #{clanId}", Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> WithdrawFromTreasuryAsync(
        ulong guildId,
        long clanId,
        ulong userId,
        long amount,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            return (false, "Сумма вывода должна быть положительной.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var deducted = await conn.ExecuteAsync(@"
            UPDATE clans
            SET treasury = treasury - @Amount
            WHERE id = @ClanId AND guild_id = @GuildId AND treasury >= @Amount;",
            new { ClanId = clanId, GuildId = guildId, Amount = amount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "В казне клана недостаточно средств.");
        }

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
            VALUES (@GuildId, NULL, @UserId, @Amount, 'clan_withdraw', @Desc, @Now);",
            new { GuildId = guildId, UserId = (ulong?)userId, Amount = amount, Desc = $"Вывод из казны клана #{clanId}", Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<(bool Success, string Error)> PurchaseUpgradeFromTreasuryAsync(
        ulong guildId,
        long clanId,
        ulong buyerId,
        string upgradeType,
        long price,
        int valueDelta,
        string? newColorHex = null,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var deducted = await conn.ExecuteAsync(@"
            UPDATE clans
            SET treasury = treasury - @Price
            WHERE id = @ClanId AND guild_id = @GuildId AND treasury >= @Price;",
            new { ClanId = clanId, GuildId = guildId, Price = price },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Недостаточно средств в казне клана (требуется {price:N0}).");
        }

        switch (upgradeType)
        {
            case "member_limit":
                await conn.ExecuteAsync(
                    "UPDATE clans SET member_limit = member_limit + @Delta WHERE id = @ClanId;",
                    new { ClanId = clanId, Delta = valueDelta },
                    transaction: tx);
                break;
            case "earn_bonus":
                await conn.ExecuteAsync(
                    "UPDATE clans SET earn_bonus_percent = earn_bonus_percent + @Delta WHERE id = @ClanId;",
                    new { ClanId = clanId, Delta = valueDelta },
                    transaction: tx);
                break;
            case "officer_slots":
                await conn.ExecuteAsync(
                    "UPDATE clans SET officer_slots = officer_slots + @Delta WHERE id = @ClanId;",
                    new { ClanId = clanId, Delta = valueDelta },
                    transaction: tx);
                break;
            case "color":
                await conn.ExecuteAsync(
                    "UPDATE clans SET color_hex = @ColorHex WHERE id = @ClanId;",
                    new { ClanId = clanId, ColorHex = newColorHex ?? "#5865F2" },
                    transaction: tx);
                break;
        }

        var prevLevel = await conn.ExecuteScalarAsync<int?>(@"
            SELECT MAX(level) FROM clan_upgrades
            WHERE clan_id = @ClanId AND upgrade_type = @Type;",
            new { ClanId = clanId, Type = upgradeType },
            transaction: tx) ?? 0;

        await conn.ExecuteAsync(@"
            INSERT INTO clan_upgrades (guild_id, clan_id, upgrade_type, level, purchased_by_id, price_paid, purchased_at)
            VALUES (@GuildId, @ClanId, @Type, @Level, @BuyerId, @Price, @Now);",
            new
            {
                GuildId = guildId,
                ClanId = clanId,
                Type = upgradeType,
                Level = prevLevel + 1,
                BuyerId = buyerId,
                Price = price,
                Now = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @BuyerId, NULL, @Price, 'clan_upgrade', @Desc, @Now);",
            new
            {
                GuildId = guildId,
                BuyerId = (ulong?)buyerId,
                Price = price,
                Desc = $"Апгрейд клана #{clanId} ({upgradeType})",
                Now = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task DisbandClanAsync(ulong guildId, long clanId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM clans WHERE id = @ClanId AND guild_id = @GuildId;",
            new { ClanId = clanId, GuildId = guildId });
    }

    public async Task AddActivityPointsAsync(ulong guildId, ulong userId, int pointsDelta, CancellationToken ct = default)
    {
        if (pointsDelta <= 0)
            return;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var member = await conn.QuerySingleOrDefaultAsync<ClanMemberEntity>(
            "SELECT * FROM clan_members WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId });

        if (member is null)
            return;

        await conn.ExecuteAsync(@"
            UPDATE clans SET points = points + @Pts WHERE id = @ClanId;
            UPDATE clan_members SET contributed_points = contributed_points + @Pts WHERE clan_id = @ClanId AND user_id = @UserId;
            UPDATE clan_wars SET challenger_score = challenger_score + @Pts
            WHERE guild_id = @GuildId AND status = 'active' AND challenger_clan_id = @ClanId;
            UPDATE clan_wars SET defender_score = defender_score + @Pts
            WHERE guild_id = @GuildId AND status = 'active' AND defender_clan_id = @ClanId;",
            new { ClanId = member.ClanId, UserId = userId, GuildId = guildId, Pts = pointsDelta });
    }

    public async Task<IReadOnlyList<ClanEntity>> GetTopClansAsync(ulong guildId, string sortBy, int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var orderCol = sortBy == "treasury" ? "treasury" : "points";
        var rows = await conn.QueryAsync<ClanEntity>($@"
            SELECT * FROM clans
            WHERE guild_id = @GuildId
            ORDER BY {orderCol} DESC, id ASC
            LIMIT @Limit;",
            new { GuildId = guildId, Limit = limit });
        return rows.ToList();
    }

    public async Task<ClanWarEntity?> GetActiveOrPendingWarForClanAsync(ulong guildId, long clanId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClanWarEntity>(@"
            SELECT * FROM clan_wars
            WHERE guild_id = @GuildId
              AND status IN ('pending', 'active')
              AND (challenger_clan_id = @ClanId OR defender_clan_id = @ClanId)
            LIMIT 1;",
            new { GuildId = guildId, ClanId = clanId });
    }

    public async Task<ClanWarEntity?> GetWarByIdAsync(long warId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClanWarEntity>(
            "SELECT * FROM clan_wars WHERE id = @Id;",
            new { Id = warId });
    }

    public async Task<long> CreateWarDeclarationAsync(
        ulong guildId,
        long challengerClanId,
        long defenderClanId,
        int durationDays,
        int wagerPercent,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO clan_wars (
                guild_id, challenger_clan_id, defender_clan_id, challenger_score, defender_score,
                wager_percent, duration_days, status, created_at
            ) VALUES (
                @GuildId, @ChallengerId, @DefenderId, 0, 0,
                @WagerPercent, @DurationDays, 'pending', @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                ChallengerId = challengerClanId,
                DefenderId = defenderClanId,
                WagerPercent = wagerPercent,
                DurationDays = durationDays,
                Now = DateTimeOffset.UtcNow
            });
    }

    public async Task<bool> ActivateWarAsync(long warId, int durationDays, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var ends = now.AddDays(durationDays);
        var rows = await conn.ExecuteAsync(@"
            UPDATE clan_wars
            SET status = 'active',
                started_at = @Now,
                ends_at = @Ends
            WHERE id = @Id AND status = 'pending';",
            new { Id = warId, Now = now, Ends = ends });
        return rows > 0;
    }

    public async Task CancelPendingWarAsync(long warId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync("UPDATE clan_wars SET status = 'declined' WHERE id = @Id AND status = 'pending';", new { Id = warId });
    }

    public async Task<IReadOnlyList<ClanWarEntity>> GetDueActiveWarsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClanWarEntity>(@"
            SELECT * FROM clan_wars
            WHERE status = 'active' AND ends_at IS NOT NULL AND ends_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task<(long? WinnerClanId, long StolenCoins)> FinalizeWarTransactionAsync(
        ClanWarEntity war,
        int warWinPoints,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var updated = await conn.ExecuteAsync(
            "UPDATE clan_wars SET status = 'finished' WHERE id = @WarId AND status = 'active';",
            new { WarId = war.Id },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return (null, 0);
        }

        long? winnerId = null;
        long? loserId = null;

        if (war.ChallengerScore > war.DefenderScore)
        {
            winnerId = war.ChallengerClanId;
            loserId = war.DefenderClanId;
        }
        else if (war.DefenderScore > war.ChallengerScore)
        {
            winnerId = war.DefenderClanId;
            loserId = war.ChallengerClanId;
        }

        long stolen = 0;
        if (winnerId.HasValue && loserId.HasValue)
        {
            var loserClan = await conn.QuerySingleOrDefaultAsync<ClanEntity>(
                "SELECT * FROM clans WHERE id = @Id;",
                new { Id = loserId.Value },
                transaction: tx);

            if (loserClan is not null && loserClan.Treasury > 0)
            {
                stolen = Math.Max(0, loserClan.Treasury * war.WagerPercent / 100);
                if (stolen > 0)
                {
                    await conn.ExecuteAsync(
                        "UPDATE clans SET treasury = MAX(0, treasury - @Stolen) WHERE id = @LoserId;",
                        new { LoserId = loserId.Value, Stolen = stolen },
                        transaction: tx);

                    await conn.ExecuteAsync(
                        "UPDATE clans SET treasury = treasury + @Stolen WHERE id = @WinnerId;",
                        new { WinnerId = winnerId.Value, Stolen = stolen },
                        transaction: tx);

                    await conn.ExecuteAsync(@"
                        INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                        VALUES (@GuildId, NULL, NULL, @Amount, 'clan_war_spoils', @Desc, @Now);",
                        new
                        {
                            GuildId = war.GuildId,
                            Amount = stolen,
                            Desc = $"Трофеи клановой войны #{war.Id} (Клан #{winnerId.Value} победил Клан #{loserId.Value})",
                            Now = DateTimeOffset.UtcNow
                        },
                        transaction: tx);
                }
            }

            await conn.ExecuteAsync(
                "UPDATE clans SET points = points + @WinPts WHERE id = @WinnerId;",
                new { WinnerId = winnerId.Value, WinPts = warWinPoints },
                transaction: tx);
        }

        await conn.ExecuteAsync(
            "UPDATE clan_wars SET winner_clan_id = @WinnerId WHERE id = @WarId;",
            new { WarId = war.Id, WinnerId = winnerId },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (winnerId, stolen);
    }
}
