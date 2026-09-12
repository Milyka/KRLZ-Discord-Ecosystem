using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class ItemRepository(DbConnectionFactory dbFactory)
{
    public Task EnsureDefaultItemsAsync(ulong guildId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<(bool Success, string Error)> AddItemToInventoryAsync(
        ulong guildId, ulong userId, long itemDefId, int quantity, CancellationToken ct = default)
        => AddItemToUserAsync(guildId, userId, itemDefId, quantity, null, ct);

    public async Task<(bool Success, string Error)> AddItemToInventoryAsync(
        ulong guildId, ulong userId, string itemCode, int quantity, CancellationToken ct = default)
    {
        var def = await GetDefinitionByCodeAsync(guildId, itemCode, ct);
        if (def is null)
            return (false, $"Предмет '{itemCode}' не найден.");

        return await AddItemToUserAsync(guildId, userId, def.Id, quantity, null, ct);
    }

    public Task<IReadOnlyList<ItemDefinitionEntity>> GetDefinitionsAsync(ulong guildId, CancellationToken ct = default)
        => GetAllDefinitionsAsync(guildId, null, ct);

    public async Task<ItemDefinitionEntity?> GetDefinitionByCodeAsync(ulong guildId, string code, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND LOWER(code) = LOWER(@Code);",
            new { GuildId = guildId, Code = code.Trim() });
    }

    public async Task<ItemDefinitionEntity?> GetDefinitionByIdAsync(ulong guildId, long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = id });
    }

    public async Task<IReadOnlyList<ItemDefinitionEntity>> GetAllDefinitionsAsync(ulong guildId, string? category = null, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        if (string.IsNullOrWhiteSpace(category))
        {
            var all = await conn.QueryAsync<ItemDefinitionEntity>(
                "SELECT * FROM item_definitions WHERE guild_id = @GuildId ORDER BY category ASC, base_price ASC;",
                new { GuildId = guildId });
            return all.ToList();
        }

        var filtered = await conn.QueryAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND category = @Category ORDER BY base_price ASC;",
            new { GuildId = guildId, Category = category });
        return filtered.ToList();
    }

    public async Task<long> CreateOrUpdateDefinitionAsync(
        ulong guildId,
        string code,
        string name,
        string description,
        string category,
        string rarity,
        int maxStack,
        long basePrice,
        long sellPrice,
        bool tradeable,
        bool usable,
        string? effectJson,
        string? recipeJson,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO item_definitions (
                guild_id, code, name, description, category, rarity, max_stack,
                base_price, sell_price, tradeable, usable, effect_json, recipe_json, created_at
            ) VALUES (
                @GuildId, @Code, @Name, @Description, @Category, @Rarity, @MaxStack,
                @BasePrice, @SellPrice, @Tradeable, @Usable, @EffectJson, @RecipeJson, @CreatedAt
            )
            ON CONFLICT(guild_id, code) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                category = excluded.category,
                rarity = excluded.rarity,
                max_stack = excluded.max_stack,
                base_price = excluded.base_price,
                sell_price = excluded.sell_price,
                tradeable = excluded.tradeable,
                usable = excluded.usable,
                effect_json = excluded.effect_json,
                recipe_json = excluded.recipe_json;
            SELECT id FROM item_definitions WHERE guild_id = @GuildId AND code = @Code;",
            new
            {
                GuildId = guildId,
                Code = code.Trim().ToLowerInvariant(),
                Name = name.Trim(),
                Description = description.Trim(),
                Category = category.Trim().ToLowerInvariant(),
                Rarity = rarity.Trim().ToLowerInvariant(),
                MaxStack = Math.Max(1, maxStack),
                BasePrice = Math.Max(0, basePrice),
                SellPrice = Math.Max(0, sellPrice),
                Tradeable = tradeable ? 1 : 0,
                Usable = usable ? 1 : 0,
                EffectJson = effectJson,
                RecipeJson = recipeJson,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task<bool> DeleteDefinitionAsync(ulong guildId, string code, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM item_definitions WHERE guild_id = @GuildId AND LOWER(code) = LOWER(@Code);",
            new { GuildId = guildId, Code = code.Trim() });
        return rows > 0;
    }

    public async Task<IReadOnlyList<InventoryStackView>> GetUserInventoryAsync(
        ulong guildId,
        ulong userId,
        string? categoryFilter = null,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var invRows = (await conn.QueryAsync<InventoryItemEntity>(@"
            SELECT * FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @UserId AND quantity > 0
            ORDER BY updated_at DESC;",
            new { GuildId = guildId, UserId = userId })).ToList();

        if (invRows.Count == 0)
            return [];

        var defIds = invRows.Select(x => x.ItemDefId).Distinct().ToArray();
        var defs = (await conn.QueryAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE id IN @Ids;",
            new { Ids = defIds })).ToDictionary(d => d.Id);

        var result = new List<InventoryStackView>();
        foreach (var inv in invRows)
        {
            if (!defs.TryGetValue(inv.ItemDefId, out var def))
                continue;

            if (!string.IsNullOrWhiteSpace(categoryFilter) &&
                !def.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new InventoryStackView(inv, def));
        }

        return result;
    }

    public async Task<int> GetUserItemQuantityAsync(ulong guildId, ulong userId, long itemDefId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COALESCE(quantity, 0)
            FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId;",
            new { GuildId = guildId, UserId = userId, ItemDefId = itemDefId });
    }

    public async Task<(bool Success, string Error)> AddItemToUserAsync(
        ulong guildId,
        ulong userId,
        long itemDefId,
        int quantity,
        string? metadataJson = null,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            return (false, "Количество должно быть больше нуля.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var def = await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = itemDefId },
            transaction: tx);

        if (def is null)
        {
            await tx.RollbackAsync(ct);
            return (false, "Определение предмета не найдено.");
        }

        var currentQty = await conn.ExecuteScalarAsync<int?>(@"
            SELECT quantity FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId;",
            new { GuildId = guildId, UserId = userId, ItemDefId = itemDefId },
            transaction: tx) ?? 0;

        if (currentQty + quantity > def.MaxStack)
        {
            await tx.RollbackAsync(ct);
            return (false, $"Превышен максимальный размер стака для предмета {def.Name} ({def.MaxStack} шт.).");
        }

        await conn.ExecuteAsync(@"
            INSERT INTO inventory_items (guild_id, user_id, item_def_id, quantity, metadata_json, updated_at)
            VALUES (@GuildId, @UserId, @ItemDefId, @Quantity, @MetadataJson, @UpdatedAt)
            ON CONFLICT(guild_id, user_id, item_def_id) DO UPDATE SET
                quantity = inventory_items.quantity + @Quantity,
                metadata_json = COALESCE(@MetadataJson, inventory_items.metadata_json),
                updated_at = @UpdatedAt;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ItemDefId = itemDefId,
                Quantity = quantity,
                MetadataJson = metadataJson,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    public async Task<bool> TryConsumeItemAsync(
        ulong guildId,
        ulong userId,
        long itemDefId,
        int quantity,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            return false;

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var updated = await conn.ExecuteAsync(@"
            UPDATE inventory_items
            SET quantity = quantity - @Quantity,
                updated_at = @UpdatedAt
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId AND quantity >= @Quantity;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ItemDefId = itemDefId,
                Quantity = quantity,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            transaction: tx);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await conn.ExecuteAsync(@"
            DELETE FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId AND quantity <= 0;",
            new { GuildId = guildId, UserId = userId, ItemDefId = itemDefId },
            transaction: tx);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<(bool Success, string Error, long TotalEarned)> SellItemAsync(
        ulong guildId,
        ulong userId,
        long itemDefId,
        int quantity,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            return (false, "Количество должно быть больше нуля.", 0);

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var def = await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = itemDefId },
            transaction: tx);

        if (def is null || def.SellPrice <= 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Этот предмет нельзя продать скупщику.", 0);
        }

        var deducted = await conn.ExecuteAsync(@"
            UPDATE inventory_items
            SET quantity = quantity - @Quantity,
                updated_at = @UpdatedAt
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId AND quantity >= @Quantity;",
            new
            {
                GuildId = guildId,
                UserId = userId,
                ItemDefId = itemDefId,
                Quantity = quantity,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            transaction: tx);

        if (deducted == 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "У вас недостаточно предметов в инвентаре.", 0);
        }

        await conn.ExecuteAsync(@"
            DELETE FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @UserId AND item_def_id = @ItemDefId AND quantity <= 0;",
            new { GuildId = guildId, UserId = userId, ItemDefId = itemDefId },
            transaction: tx);

        var totalEarned = def.SellPrice * quantity;
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @UserId, @CreatedAt)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, UserId = userId, CreatedAt = now },
            transaction: tx);

        await conn.ExecuteAsync(@"
            UPDATE users
            SET balance = balance + @TotalEarned
            WHERE guild_id = @GuildId AND user_id = @UserId;",
            new { GuildId = guildId, UserId = userId, TotalEarned = totalEarned },
            transaction: tx);

        await conn.ExecuteAsync(@"
            INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
            VALUES (@GuildId, NULL, @UserId, @Amount, 'item_sell', @Description, @CreatedAt);",
            new
            {
                GuildId = guildId,
                UserId = (ulong?)userId,
                Amount = totalEarned,
                Description = $"Продажа предмета {def.Name} x{quantity}",
                CreatedAt = now
            },
            transaction: tx);

        await tx.CommitAsync(ct);
        return (true, string.Empty, totalEarned);
    }

    public async Task<(bool Success, string Error)> ExecuteAtomicTradeAsync(
        ulong guildId,
        ulong userA,
        long? itemDefA,
        int qtyA,
        long coinsA,
        ulong userB,
        long? itemDefB,
        int qtyB,
        long coinsB,
        CancellationToken ct = default)
    {
        if (coinsA < 0 || coinsB < 0 || qtyA < 0 || qtyB < 0)
            return (false, "Некорректные параметры обмена.");

        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await conn.ExecuteAsync(@"
            INSERT INTO users (guild_id, user_id, created_at)
            VALUES (@GuildId, @U1, @Now), (@GuildId, @U2, @Now)
            ON CONFLICT(guild_id, user_id) DO NOTHING;",
            new { GuildId = guildId, U1 = userA, U2 = userB, Now = now },
            transaction: tx);

        if (coinsA > 0)
        {
            var r = await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance - @Amount
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Amount;",
                new { GuildId = guildId, UserId = userA, Amount = coinsA },
                transaction: tx);
            if (r == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "У первого участника недостаточно валюты для обмена.");
            }

            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Amount
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userB, Amount = coinsA },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @FromId, @ToId, @Amount, 'trade_coins', 'Обмен между игроками', @Now);",
                new { GuildId = guildId, FromId = (ulong?)userA, ToId = (ulong?)userB, Amount = coinsA, Now = now },
                transaction: tx);
        }

        if (coinsB > 0)
        {
            var r = await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance - @Amount
                WHERE guild_id = @GuildId AND user_id = @UserId AND balance >= @Amount;",
                new { GuildId = guildId, UserId = userB, Amount = coinsB },
                transaction: tx);
            if (r == 0)
            {
                await tx.RollbackAsync(ct);
                return (false, "У второго участника недостаточно валюты для обмена.");
            }

            await conn.ExecuteAsync(@"
                UPDATE users SET balance = balance + @Amount
                WHERE guild_id = @GuildId AND user_id = @UserId;",
                new { GuildId = guildId, UserId = userA, Amount = coinsB },
                transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO transactions (guild_id, from_user_id, to_user_id, amount, tx_type, description, created_at)
                VALUES (@GuildId, @FromId, @ToId, @Amount, 'trade_coins', 'Обмен между игроками', @Now);",
                new { GuildId = guildId, FromId = (ulong?)userB, ToId = (ulong?)userA, Amount = coinsB, Now = now },
                transaction: tx);
        }

        if (itemDefA.HasValue && qtyA > 0)
        {
            var ok = await TransferItemInternalAsync(conn, tx, guildId, userA, userB, itemDefA.Value, qtyA, now);
            if (!ok.Success)
            {
                await tx.RollbackAsync(ct);
                return ok;
            }
        }

        if (itemDefB.HasValue && qtyB > 0)
        {
            var ok = await TransferItemInternalAsync(conn, tx, guildId, userB, userA, itemDefB.Value, qtyB, now);
            if (!ok.Success)
            {
                await tx.RollbackAsync(ct);
                return ok;
            }
        }

        await tx.CommitAsync(ct);
        return (true, string.Empty);
    }

    private static async Task<(bool Success, string Error)> TransferItemInternalAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        ulong guildId,
        ulong fromUserId,
        ulong toUserId,
        long itemDefId,
        int quantity,
        DateTimeOffset now)
    {
        var def = await conn.QuerySingleOrDefaultAsync<ItemDefinitionEntity>(
            "SELECT * FROM item_definitions WHERE guild_id = @GuildId AND id = @Id;",
            new { GuildId = guildId, Id = itemDefId },
            transaction: tx);

        if (def is null || !def.Tradeable)
            return (false, "Выбранный предмет нельзя передавать другим игрокам.");

        var deducted = await conn.ExecuteAsync(@"
            UPDATE inventory_items
            SET quantity = quantity - @Quantity, updated_at = @Now
            WHERE guild_id = @GuildId AND user_id = @FromId AND item_def_id = @ItemDefId AND quantity >= @Quantity;",
            new { GuildId = guildId, FromId = fromUserId, ItemDefId = itemDefId, Quantity = quantity, Now = now },
            transaction: tx);

        if (deducted == 0)
            return (false, $"Недостаточно предмета {def.Name} для обмена.");

        await conn.ExecuteAsync(@"
            DELETE FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @FromId AND item_def_id = @ItemDefId AND quantity <= 0;",
            new { GuildId = guildId, FromId = fromUserId, ItemDefId = itemDefId },
            transaction: tx);

        var targetQty = await conn.ExecuteScalarAsync<int?>(@"
            SELECT quantity FROM inventory_items
            WHERE guild_id = @GuildId AND user_id = @ToId AND item_def_id = @ItemDefId;",
            new { GuildId = guildId, ToId = toUserId, ItemDefId = itemDefId },
            transaction: tx) ?? 0;

        if (targetQty + quantity > def.MaxStack)
            return (false, $"У получателя превышен лимит стака предмета {def.Name} ({def.MaxStack}).");

        await conn.ExecuteAsync(@"
            INSERT INTO inventory_items (guild_id, user_id, item_def_id, quantity, updated_at)
            VALUES (@GuildId, @ToId, @ItemDefId, @Quantity, @Now)
            ON CONFLICT(guild_id, user_id, item_def_id) DO UPDATE SET
                quantity = inventory_items.quantity + @Quantity,
                updated_at = @Now;",
            new { GuildId = guildId, ToId = toUserId, ItemDefId = itemDefId, Quantity = quantity, Now = now },
            transaction: tx);

        return (true, string.Empty);
    }
}
