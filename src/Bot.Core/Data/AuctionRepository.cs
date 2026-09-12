using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class AuctionRepository(DbConnectionFactory dbFactory)
{
    public async Task<AuctionEntity?> GetByIdAsync(ulong guildId, long auctionId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AuctionEntity>(
            "SELECT * FROM auctions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = auctionId });
    }

    public async Task<int> GetActiveLotCountForSellerAsync(ulong guildId, ulong sellerId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM auctions WHERE guild_id = @GuildId AND seller_id = @SellerId AND status = 'active';",
            new { GuildId = guildId, SellerId = sellerId });
    }

    public async Task<IReadOnlyList<AuctionEntity>> GetActiveAuctionsAsync(
        ulong guildId,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuctionEntity>(@"
            SELECT * FROM auctions
            WHERE guild_id = @GuildId AND status = 'active'
            ORDER BY ends_at ASC, id ASC
            LIMIT @Limit OFFSET @Offset;",
            new { GuildId = guildId, Limit = limit, Offset = offset });
        return rows.ToList();
    }

    public async Task<int> CountActiveAuctionsAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM auctions WHERE guild_id = @GuildId AND status = 'active';",
            new { GuildId = guildId });
    }

    public async Task<(bool Success, string Error, AuctionEntity? Lot)> CreateItemLotAsync(
        ulong guildId,
        ulong sellerId,
        long itemDefId,
        int quantity,
        long startPrice,
        long minStep,
        long? buyoutPrice,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var def = await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = itemDefId },
            transaction: tx);

        if (def is null || !def.Tradeable)
        {
            await tx.RollbackAsync(ct);
            return (false, "Этот предмет нельзя выставить на аукцион.", null);
        }

        var deducted = await conn.ExecuteAsync(@"
            UPDATE inventory_items
            SET quantity = quantity - @Quantity,
                updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @SellerId AND item_def_id = @ItemDefId AND quantity >= @Quantity;",
            new { GuildId = guildId, SellerId = sellerId, ItemDefId = itemDefId, Quantity = quantity, Now = now },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, $"В вашем инвентаре недостаточно предмета {def.Name} ({quantity} шт.).", null);
        }

        await conn.ExecuteAsync(@"
            DELETE FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @SellerId AND item_def_id = @ItemDefId AND quantity <= 0;",
            new { GuildId = guildId, SellerId = sellerId, ItemDefId = itemDefId },
            transaction: tx);

        var title = $"{def.Name} x{quantity}";
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO auctions (
                guild_id, seller_id, lot_type, item_def_id, item_quantity, custom_role_id,
                lot_title, start_price, min_step, buyout_price, current_bid, highest_bidder_id,
                status, ends_at, created_at
            ) VALUES (
                @GuildId, @SellerId, 'item', @ItemDefId, @Quantity, NULL,
                @Title, @StartPrice, @MinStep, @BuyoutPrice, 0, NULL,
                'active', @EndsAt, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                SellerId = sellerId,
                ItemDefId = itemDefId,
                Quantity = quantity,
                Title = title,
                StartPrice = startPrice,
                MinStep = minStep,
                BuyoutPrice = buyoutPrice,
                EndsAt = endsAt,
                Now = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        var lot = new AuctionEntity(
            id, guildId, sellerId, "item", itemDefId, quantity, null,
            title, startPrice, minStep, buyoutPrice, 0, null, "active", endsAt, now);

        return (true, string.Empty, lot);
    }

    public async Task<(bool Success, string Error, AuctionEntity? Lot)> CreateRoleLotAsync(
        ulong guildId,
        ulong sellerId,
        ulong roleId,
        long startPrice,
        long minStep,
        long? buyoutPrice,
        DateTimeOffset endsAt,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var customRole = await conn.QuerySingleOrDefaultAsync<CustomRoleEntity>(@"
            SELECT * FROM custom_roles
            WHERE guild_id = @GuildId AND role_id = @RoleId AND owner_id = @SellerId;",
            new { GuildId = guildId, RoleId = roleId, SellerId = sellerId },
            transaction: tx);

        if (customRole is null)
        {
            await tx.RollbackAsync(ct);
            return (false, "Кастомная роль не найдена или не принадлежит вам.", null);
        }

        var alreadyListed = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM auctions
            WHERE guild_id = @GuildId AND custom_role_id = @RoleId AND status = 'active';",
            new { GuildId = guildId, RoleId = roleId },
            transaction: tx);

        if (alreadyListed > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Эта кастомная роль уже выставлена на активный аукцион.", null);
        }

        var title = $"Кастомная роль: {customRole.Name} ({customRole.ColorHex})";
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO auctions (
                guild_id, seller_id, lot_type, item_def_id, item_quantity, custom_role_id,
                lot_title, start_price, min_step, buyout_price, current_bid, highest_bidder_id,
                status, ends_at, created_at
            ) VALUES (
                @GuildId, @SellerId, 'role', NULL, 1, @RoleId,
                @Title, @StartPrice, @MinStep, @BuyoutPrice, 0, NULL,
                'active', @EndsAt, @Now
            );
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                SellerId = sellerId,
                RoleId = roleId,
                Title = title,
                StartPrice = startPrice,
                MinStep = minStep,
                BuyoutPrice = buyoutPrice,
                EndsAt = endsAt,
                Now = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        var lot = new AuctionEntity(
            id, guildId, sellerId, "role", null, 1, roleId,
            title, startPrice, minStep, buyoutPrice, 0, null, "active", endsAt, now);

        return (true, string.Empty, lot);
    }

    public async Task<(bool Success, string Error, ulong? OutbidUserId, long OutbidAmount, DateTimeOffset NewEndsAt)> PlaceBidAsync(
        ulong guildId,
        long auctionId,
        ulong bidderId,
        long bidAmount,
        int antiSnipeWindowSeconds,
        int antiSnipeExtendSeconds,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var lot = await conn.QuerySingleOrDefaultAsync<AuctionEntity>(
            "SELECT * FROM auctions WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = auctionId, GuildId = guildId },
            transaction: tx);

        if (lot is null || lot.Status != "active" || lot.EndsAt <= now)
        {
            await tx.RollbackAsync(ct);
            return (false, "Лот не найден или время торгов уже истекло.", null, 0, now);
        }

        if (lot.SellerId == bidderId)
        {
            await tx.RollbackAsync(ct);
            return (false, "Вы не можете делать ставки на собственный лот.", null, 0, lot.EndsAt);
        }

        var minRequired = lot.CurrentBid == 0 ? lot.StartPrice : lot.CurrentBid + lot.MinStep;
        if (bidAmount < minRequired)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Минимальная допустимая ставка по лоту: **{minRequired:N0}**.", null, 0, lot.EndsAt);
        }

        if (lot.BuyoutPrice.HasValue && bidAmount >= lot.BuyoutPrice.Value)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Ставка равна или превышает цену выкупа (**{lot.BuyoutPrice.Value:N0}**). Используйте `/auction buyout`.", null, 0, lot.EndsAt);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = bidderId, Now = now },
            transaction: tx);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @BidAmount
            WHERE guild_id = @GuildId AND user_id = @BidderId AND balance >= @BidAmount;",
            new { GuildId = guildId, BidderId = bidderId, BidAmount = bidAmount },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на балансе для заморозки ставки.", null, 0, lot.EndsAt);
        }

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @BidderId, NULL, @Amount, 'auction_bid_hold', @Desc, @Now);",
            new
            {
                GuildId = guildId,
                BidderId = (ulong?)bidderId,
                Amount = bidAmount,
                Desc = $"Заморозка ставки по лоту #{lot.Id} ({lot.LotTitle})",
                Now = now
            },
            transaction: tx);

        var prevBidderId = lot.HighestBidderId;
        var prevBidAmount = lot.CurrentBid;

        if (prevBidderId.HasValue && prevBidAmount > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @Refund
                WHERE guild_id = @GuildId AND user_id = @PrevBidder;",
                new { GuildId = guildId, PrevBidder = prevBidderId.Value, Refund = prevBidAmount },
                transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auction_bids
                SET refunded = 1
                WHERE auction_id = @AuctionId AND bidder_id = @PrevBidder AND refunded = 0;",
                new { AuctionId = lot.Id, PrevBidder = prevBidderId.Value },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @PrevBidder, @Refund, 'auction_bid_refund', @Desc, @Now);",
                new
                {
                    GuildId = guildId,
                    PrevBidder = (ulong?)prevBidderId.Value,
                    Refund = prevBidAmount,
                    Desc = $"Возврат перебитой ставки по лоту #{lot.Id}",
                    Now = now
                },
                transaction: tx);
        }

        var newEndsAt = lot.EndsAt;
        if ((lot.EndsAt - now).TotalSeconds <= antiSnipeWindowSeconds)
        {
            newEndsAt = lot.EndsAt.AddSeconds(antiSnipeExtendSeconds);
        }

        await conn.ExecuteAsync(@"
            UPDATE auctions
            SET current_bid = @BidAmount,
                highest_bidder_id = @BidderId,
                ends_at = @NewEndsAt
            WHERE id = @AuctionId;",
            new { AuctionId = lot.Id, BidAmount = bidAmount, BidderId = bidderId, NewEndsAt = newEndsAt },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO auction_bids (auction_id, guild_id, bidder_id, amount, refunded, created_at)
            VALUES (@AuctionId, @GuildId, @BidderId, @Amount, 0, @Now);",
            new { AuctionId = lot.Id, GuildId = guildId, BidderId = bidderId, Amount = bidAmount, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty, prevBidderId, prevBidAmount, newEndsAt);
    }

    public async Task<(bool Success, string Error, AuctionEntity? FinalizedLot, ulong? RefundedBidderId, long RefundedAmount, long SellerPayout)> BuyoutAsync(
        ulong guildId,
        long auctionId,
        ulong buyerId,
        int commissionPercent,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var lot = await conn.QuerySingleOrDefaultAsync<AuctionEntity>(
            "SELECT * FROM auctions WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = auctionId, GuildId = guildId },
            transaction: tx);

        if (lot is null || lot.Status != "active" || lot.EndsAt <= now)
        {
            await tx.RollbackAsync(ct);
            return (false, "Лот не найден или уже завершён.", null, null, 0, 0);
        }

        if (!lot.BuyoutPrice.HasValue)
        {
            await tx.RollbackAsync(ct);
            return (false, "Для этого лота не предусмотрен мгновенный выкуп.", null, null, 0, 0);
        }

        if (lot.SellerId == buyerId)
        {
            await tx.RollbackAsync(ct);
            return (false, "Нельзя выкупить собственный лот.", null, null, 0, 0);
        }

        var price = lot.BuyoutPrice.Value;
        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @BuyerId, @Now), (@GuildId, @SellerId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, BuyerId = buyerId, SellerId = lot.SellerId, Now = now },
            transaction: tx);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Price
            WHERE guild_id = @GuildId AND user_id = @BuyerId AND balance >= @Price;",
            new { GuildId = guildId, BuyerId = buyerId, Price = price },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на балансе для мгновенного выкупа.", null, null, 0, 0);
        }

        var prevBidder = lot.HighestBidderId;
        var prevBid = lot.CurrentBid;

        if (prevBidder.HasValue && prevBid > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @Refund
                WHERE guild_id = @GuildId AND user_id = @PrevBidder;",
                new { GuildId = guildId, PrevBidder = prevBidder.Value, Refund = prevBid },
                transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auction_bids SET refunded = 1
                WHERE auction_id = @AuctionId AND bidder_id = @PrevBidder AND refunded = 0;",
                new { AuctionId = lot.Id, PrevBidder = prevBidder.Value },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @PrevBidder, @Refund, 'auction_bid_refund', @Desc, @Now);",
                new
                {
                    GuildId = guildId,
                    PrevBidder = (ulong?)prevBidder.Value,
                    Refund = prevBid,
                    Desc = $"Возврат ставки из-за выкупа лота #{lot.Id}",
                    Now = now
                },
                transaction: tx);
        }

        var fee = Math.Max(0, price * commissionPercent / 100);
        var sellerPayout = Math.Max(0, price - fee);

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Payout
            WHERE guild_id = @GuildId AND user_id = @SellerId;",
            new { GuildId = guildId, SellerId = lot.SellerId, Payout = sellerPayout },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @BuyerId, @SellerId, @Payout, 'auction_buyout', @Desc, @Now);",
            new
            {
                GuildId = guildId,
                BuyerId = (ulong?)buyerId,
                SellerId = (ulong?)lot.SellerId,
                Payout = sellerPayout,
                Desc = $"Выкуп лота #{lot.Id} ({lot.LotTitle}), комиссия {fee}",
                Now = now
            },
            transaction: tx);

        await DeliverLotAssetInternalAsync(conn, tx, lot, buyerId, now);

        await conn.ExecuteAsync(@"
            UPDATE auctions
            SET status = 'sold',
                current_bid = @Price,
                highest_bidder_id = @BuyerId,
                ends_at = @Now
            WHERE id = @Id;",
            new { Id = lot.Id, Price = price, BuyerId = buyerId, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        var updatedLot = lot with { Status = "sold", CurrentBid = price, HighestBidderId = buyerId, EndsAt = now };
        return (true, string.Empty, updatedLot, prevBidder, prevBid, sellerPayout);
    }

    public async Task<(bool Success, string Error, AuctionEntity? CancelledLot)> CancelLotAsync(
        ulong guildId,
        long auctionId,
        ulong actorId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var lot = await conn.QuerySingleOrDefaultAsync<AuctionEntity>(
            "SELECT * FROM auctions WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = auctionId, GuildId = guildId },
            transaction: tx);

        if (lot is null || lot.Status != "active")
        {
            await tx.RollbackAsync(ct);
            return (false, "Активный лот с таким номером не найден.", null);
        }

        if (lot.SellerId != actorId && !isAdmin)
        {
            await tx.RollbackAsync(ct);
            return (false, "Отменить лот может только его продавец или администратор.", null);
        }

        if (lot.HighestBidderId.HasValue && !isAdmin)
        {
            await tx.RollbackAsync(ct);
            return (false, "Нельзя отменить лот, по которому уже сделаны ставки.", null);
        }

        if (lot.HighestBidderId.HasValue && lot.CurrentBid > 0)
        {
            await conn.ExecuteAsync(@"
                UPDATE users
                SET balance = balance + @Refund
                WHERE guild_id = @GuildId AND user_id = @BidderId;",
                new { GuildId = guildId, BidderId = lot.HighestBidderId.Value, Refund = lot.CurrentBid },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, NULL, @BidderId, @Refund, 'auction_cancel_refund', @Desc, @Now);",
                new
                {
                    GuildId = guildId,
                    BidderId = (ulong?)lot.HighestBidderId.Value,
                    Refund = lot.CurrentBid,
                    Desc = $"Возврат ставки при отмене лота #{lot.Id}",
                    Now = now
                },
                transaction: tx);
        }

        await ReturnLotAssetToSellerInternalAsync(conn, tx, lot, now);

        await conn.ExecuteAsync(
            "UPDATE auctions SET status = 'cancelled', ends_at = @Now WHERE id = @Id;",
            new { Id = lot.Id, Now = now },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty, lot with { Status = "cancelled" });
    }

    public async Task<IReadOnlyList<AuctionEntity>> GetExpiredActiveAuctionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuctionEntity>(@"
            SELECT * FROM auctions
            WHERE status = 'active' AND ends_at <= @Now;",
            new { Now = now });
        return rows.ToList();
    }

    public async Task<(bool Sold, long SellerPayout)> FinalizeExpiredAuctionAsync(
        AuctionEntity lot,
        int commissionPercent,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var current = await conn.QuerySingleOrDefaultAsync<AuctionEntity>(
            "SELECT * FROM auctions WHERE id = @Id AND status = 'active';",
            new { Id = lot.Id },
            transaction: tx);

        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return (false, 0);
        }

        if (!current.HighestBidderId.HasValue || current.CurrentBid <= 0)
        {
            await ReturnLotAssetToSellerInternalAsync(conn, tx, current, now);
            await conn.ExecuteAsync(
                "UPDATE auctions SET status = 'expired' WHERE id = @Id;",
                new { Id = current.Id },
                transaction: tx);

            await tx.CommitAsync(ct);
            return (false, 0);
        }

        var winnerId = current.HighestBidderId.Value;
        var fee = Math.Max(0, current.CurrentBid * commissionPercent / 100);
        var sellerPayout = Math.Max(0, current.CurrentBid - fee);

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @SellerId, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = current.GuildId, SellerId = current.SellerId, Now = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @Payout
            WHERE guild_id = @GuildId AND user_id = @SellerId;",
            new { GuildId = current.GuildId, SellerId = current.SellerId, Payout = sellerPayout },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @WinnerId, @SellerId, @Payout, 'auction_sale', @Desc, @Now);",
            new
            {
                GuildId = current.GuildId,
                WinnerId = (ulong?)winnerId,
                SellerId = (ulong?)current.SellerId,
                Payout = sellerPayout,
                Desc = $"Продажа лота #{current.Id} ({current.LotTitle}), комиссия {fee}",
                Now = now
            },
            transaction: tx);

        await DeliverLotAssetInternalAsync(conn, tx, current, winnerId, now);

        await conn.ExecuteAsync(
            "UPDATE auctions SET status = 'sold' WHERE id = @Id;",
            new { Id = current.Id },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, sellerPayout);
    }

    private static async Task DeliverLotAssetInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        AuctionEntity lot,
        ulong recipientId,
        DateTimeOffset now)
    {
        if (lot.LotType == "item" && lot.ItemDefId.HasValue)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO inventory_items (guild_id, user_id, item_def_id, quantity, updated_at)
                VALUES (@GuildId, @UserId, @ItemDefId, @Qty, @Now)
                ON CONFLICT(guild_id, user_id, item_def_id) DO UPDATE SET
                    quantity = inventory_items.quantity + @Qty,
                    updated_at = @Now;",
                new
                {
                    GuildId = lot.GuildId,
                    UserId = recipientId,
                    ItemDefId = lot.ItemDefId.Value,
                    Qty = lot.ItemQuantity,
                    Now = now
                },
                transaction: tx);
        }
        else if (lot.LotType == "role" && lot.CustomRoleId.HasValue)
        {
            await conn.ExecuteAsync(@"
                UPDATE custom_roles
                SET owner_id = @NewOwner
                WHERE guild_id = @GuildId AND role_id = @RoleId;",
                new
                {
                    GuildId = lot.GuildId,
                    RoleId = lot.CustomRoleId.Value,
                    NewOwner = recipientId
                },
                transaction: tx);
        }
    }

    private static async Task ReturnLotAssetToSellerInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        AuctionEntity lot,
        DateTimeOffset now)
    {
        if (lot.LotType == "item" && lot.ItemDefId.HasValue)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO inventory_items (guild_id, user_id, item_def_id, quantity, updated_at)
                VALUES (@GuildId, @SellerId, @ItemDefId, @Qty, @Now)
                ON CONFLICT(guild_id, user_id, item_def_id) DO UPDATE SET
                    quantity = inventory_items.quantity + @Qty,
                    updated_at = @Now;",
                new
                {
                    GuildId = lot.GuildId,
                    SellerId = lot.SellerId,
                    ItemDefId = lot.ItemDefId.Value,
                    Qty = lot.ItemQuantity,
                    Now = now
                },
                transaction: tx);
        }
    }
}
