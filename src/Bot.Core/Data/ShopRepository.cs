using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class ShopRepository(DbConnectionFactory dbFactory)
{
    public async Task EnsureDefaultFishingGearAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM shop_items WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        if (count > 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var defaults = new (string Name, string Desc, string Type, long Price, string Payload)[]
        {
            ("Бамбуковая удочка", "Надёжная стартовая удочка для рыбалки (+7% к клёву)", "item", 500, "bamboo_rod"),
            ("Карбоновый спиннинг", "Профессиональный спиннинг (+18% к клёву и редким видам)", "item", 4500, "carbon_rod"),
            ("Наживка: Червь (x5)", "Набор из 5 червей для рыбалки", "item", 110, "worm_bait:5"),
            ("Наживка: Креветка (x5)", "Набор из 5 креветок для ловли хищной и редкой рыбы", "item", 400, "shrimp_bait:5"),
            ("Эликсир опыта", "Повышает получаемый XP на +50% на 60 минут (/use)", "item", 1800, "xp_elixir"),
            ("Фон профиля: Киберпанк", "Неоновая тема карточки /profile", "background", 3500, "cyberpunk.png"),
            ("Фон профиля: Изумруд", "Изумрудная тема карточки /profile", "background", 3500, "emerald.png")
        };

        foreach (var d in defaults)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO shop_items (guild_id, name, description, item_type, price, role_id, payload, enabled, created_at)
                VALUES (@GuildId, @Name, @Desc, @Type, @Price, NULL, @Payload, 1, @Now);",
                new { GuildId = guildId, d.Name, Desc = d.Desc, d.Type, d.Price, d.Payload, Now = now });
        }
    }

    public Task<long> AddShopItemAsync(
        ulong guildId,
        string name,
        string description,
        string itemType,
        long price,
        ulong? roleId,
        string? payload,
        CancellationToken ct = default)
        => AddItemAsync(guildId, name, description, itemType, price, roleId, payload, ct);

    public Task<bool> RemoveShopItemAsync(ulong guildId, long itemId, CancellationToken ct = default)
        => RemoveItemAsync(itemId, guildId, ct);

    public async Task<long> AddItemAsync(
        ulong guildId,
        string name,
        string description,
        string itemType,
        long price,
        ulong? roleId,
        string? payload,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO shop_items (guild_id, name, description, item_type, price, role_id, payload, enabled, created_at)
            VALUES (@GuildId, @Name, @Description, @ItemType, @Price, @RoleId, @Payload, 1, @CreatedAt);
            SELECT last_insert_rowid();",
            new
            {
                GuildId = guildId,
                Name = name,
                Description = description,
                ItemType = itemType,
                Price = price,
                RoleId = roleId,
                Payload = payload,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<bool> UpdateItemAsync(long itemId, ulong guildId, string name, string description, long price, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(@"
            UPDATE shop_items
            SET name = @Name, description = @Description, price = @Price
            WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = itemId, GuildId = guildId, Name = name, Description = description, Price = price });
        return rows > 0;
    }

    public async Task<bool> RemoveItemAsync(long itemId, ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM shop_items WHERE id = @Id AND guild_id = @GuildId;",
            new { Id = itemId, GuildId = guildId });
        return rows > 0;
    }

    public async Task<ShopItemEntity?> GetItemByIdAsync(long itemId, ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ShopItemEntity>(
            "SELECT * FROM shop_items WHERE id = @Id AND guild_id = @GuildId AND enabled = 1;",
            new { Id = itemId, GuildId = guildId });
    }

    public async Task<IReadOnlyList<ShopItemEntity>> GetActiveItemsAsync(ulong guildId, CancellationToken ct = default)
    {
        await EnsureDefaultFishingGearAsync(guildId, ct);
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ShopItemEntity>(@"
            SELECT * FROM shop_items
            WHERE guild_id = @GuildId AND enabled = 1
            ORDER BY price ASC, id ASC;",
            new { GuildId = guildId });
        return rows.ToList();
    }

    public async Task<bool> HasPurchasedAsync(ulong guildId, ulong userId, long itemId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM purchases
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_id = @ItemId;",
            new { GuildId = guildId, UserId = userId, ItemId = itemId });
        return count > 0;
    }

    public async Task<(bool Success, string Error)> PurchaseItemAsync(
        ulong guildId,
        ulong userId,
        long itemId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var item = await conn.QuerySingleOrDefaultAsync<ShopItemEntity>(
            "SELECT * FROM shop_items WHERE id = @Id AND guild_id = @GuildId AND enabled = 1;",
            new { Id = itemId, GuildId = guildId },
            transaction: tx);

        if (item is null)
        {
            await tx.RollbackAsync(ct);
            return (false, "Товар не найден или снят с продажи.");
        }

        var alreadyOwned = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM purchases
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_id = @ItemId;",
            new { GuildId = guildId, UserId = userId, ItemId = itemId },
            transaction: tx);

        if (alreadyOwned > 0 && item.ItemType is "role" or "background")
        {
            await tx.RollbackAsync(ct);
            return (false, "Этот товар уже приобретён вами ранее.");
        }

        var now = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @CreatedAt)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, CreatedAt = now },
            transaction: tx);

        var deducted = await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance - @Price
            WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Price;",
            new { GuildId = guildId, UserId = userId, Price = item.Price },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Недостаточно средств на балансе для покупки.");
        }

        await conn.ExecuteAsync(@"
            INSERT INTO purchases (guild_id, user_id, item_id, price_paid, purchased_at)
            VALUES (@GuildId, @UserId, @ItemId, @PricePaid, @PurchasedAt);",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ItemId = item.Id,
                PricePaid = item.Price,
                PurchasedAt = now
            },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, @UserId, NULL, @Amount, 'shop_buy', @Description, @CreatedAt);",
            new
            {
                GuildId = guildId,
                UserId = (ulong?)userId,
                Amount = item.Price,
                Description = $"Покупка товара: {item.Name}",
                CreatedAt = now
            },
            transaction: tx);

        if (item.ItemType == "background" && !string.IsNullOrWhiteSpace(item.Payload))
        {
            await conn.ExecuteAsync(@"
                UPDATE users
                SET selected_background = @Bg
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userId, Bg = item.Payload },
                transaction: tx);
        }
        else if (item.ItemType == "item" && !string.IsNullOrWhiteSpace(item.Payload))
        {
            var parts = item.Payload.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var code = parts[0].ToLowerInvariant();
            var qty = parts.Length > 1 && int.TryParse(parts[1], out var parsedQty) ? Math.Max(1, parsedQty) : 1;

            var def = await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
                "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND LOWER(code) = @Code;",
                new { GuildId = guildId, Code = code },
                transaction: tx);

            if (def is not null)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO inventory_items (guild_id, user_id, item_def_id, quantity, updated_at)
                    VALUES (@GuildId, @UserId, @DefId, @Qty, @Now)
                    ON CONFLICT(guild_id, user_id, item_def_id) DO UPDATE SET
                        quantity = MIN(@MaxStack, inventory_items.quantity + @Qty),
                        updated_at = @Now;",
                    new { GuildId = guildId, UserId = userId, DefId = def.Id, Qty = qty, MaxStack = def.MaxStack, Now = now },
                    transaction: tx);
            }
        }

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<IReadOnlyList<(PurchaseEntity Purchase, ShopItemEntity Item)>> GetUserInventoryAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var purchases = (await conn.QueryAsync<PurchaseEntity>(@"
            SELECT * FROM purchases
            WHERE guild_id = @GuildId AND user_id = @UserId
            ORDER BY id DESC;",
            new { GuildId = guildId, UserId = userId })).ToList();

        if (purchases.Count == 0)
            return [];

        var itemIds = purchases.Select(p => p.ItemId).Distinct().ToArray();
        var items = (await conn.QueryAsync<ShopItemEntity>(
            "SELECT * FROM shop_items WHERE id IN @Ids;",
            new { Ids = itemIds })).ToDictionary(x => x.Id);

        var result = new List<(PurchaseEntity, ShopItemEntity)>();
        foreach (var p in purchases)
        {
            if (items.TryGetValue(p.ItemId, out var item))
                result.Add((p, item));
        }

        return result;
    }
}
