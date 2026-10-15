using System.Collections.Concurrent;
using System.Text.Json;
using Bot.Data;
using Bot.Models;

namespace Bot.Services;

public sealed class InventoryService(
    ItemRepository itemRepo,
    ItemService itemService)
{
    private sealed record ActiveBoost(string EffectType, int MultiplierPercent, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId, string EffectType), ActiveBoost> _activeBoosts = new();

    public async Task<IReadOnlyList<InventoryStackView>> GetInventoryAsync(
        ulong guildId,
        ulong userId,
        string? categoryFilter = null,
        CancellationToken ct = default)
    {
        await itemService.EnsureDefaultCatalogAsync(guildId, ct);
        return await itemRepo.GetUserInventoryAsync(guildId, userId, categoryFilter, ct);
    }

    public async Task<(bool Success, string Message)> GiveItemByCodeAsync(
        ulong guildId,
        ulong userId,
        string itemCode,
        int quantity,
        CancellationToken ct = default)
    {
        var def = await itemService.FindByCodeOrNameAsync(guildId, itemCode, ct);
        if (def is null)
            return (false, $"Предмет `{itemCode}` не найден в каталоге.");

        var (ok, err) = await itemRepo.AddItemToUserAsync(guildId, userId, def.Id, quantity, null, ct);
        return ok
            ? (true, $"Предмет **{def.Name}** x{quantity} добавлен в инвентарь.")
            : (false, err);
    }

    public async Task<(bool Success, string Message, long EarnedCoins)> SellItemByCodeAsync(
        ulong guildId,
        ulong userId,
        string itemQuery,
        int quantity,
        CancellationToken ct = default)
    {
        var def = await itemService.FindByCodeOrNameAsync(guildId, itemQuery, ct);
        if (def is null)
            return (false, $"Предмет `{itemQuery}` не найден.", 0);

        var (ok, err, earned) = await itemRepo.SellItemAsync(guildId, userId, def.Id, quantity, ct);
        if (!ok)
            return (false, err, 0);

        return (true, $"Вы продали **{def.Name}** x{quantity}.", earned);
    }

    public async Task<(bool Success, string Message)> UseItemByCodeAsync(
        ulong guildId,
        ulong userId,
        string itemQuery,
        CancellationToken ct = default)
    {
        var def = await itemService.FindByCodeOrNameAsync(guildId, itemQuery, ct);
        if (def is null)
            return (false, $"Предмет `{itemQuery}` не найден.");

        if (!def.Usable || string.IsNullOrWhiteSpace(def.EffectJson))
            return (false, $"Предмет **{def.Name}** не является расходуемым бустом.");

        var effect = JsonSerializer.Deserialize<ItemEffectDto>(def.EffectJson);
        if (effect is null)
            return (false, "Не удалось прочитать эффект предмета.");

        var consumed = await itemRepo.TryConsumeItemAsync(guildId, userId, def.Id, 1, ct);
        if (!consumed)
            return (false, $"У вас нет предмета **{def.Name}** в инвентаре.");

        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, effect.DurationMinutes));
        _activeBoosts[(guildId, userId, effect.EffectType)] = new ActiveBoost(effect.EffectType, effect.MultiplierPercent, expires);

        var effectTitle = effect.EffectType == "xp_boost" ? "получаемому опыту" : "заработку валюты";
        return (true, $"Вы активировали **{def.Name}**! Бонус **+{effect.MultiplierPercent}%** к {effectTitle} действует до <t:{expires.ToUnixTimeSeconds()}:t>.");
    }

    public async Task<(bool Success, string Message)> CraftItemByCodeAsync(
        ulong guildId,
        ulong userId,
        string itemQuery,
        CancellationToken ct = default)
    {
        var def = await itemService.FindByCodeOrNameAsync(guildId, itemQuery, ct);
        if (def is null)
            return (false, $"Рецепт `{itemQuery}` не найден.");

        if (string.IsNullOrWhiteSpace(def.RecipeJson))
            return (false, $"Предмет **{def.Name}** нельзя создать через крафт.");

        var ingredients = JsonSerializer.Deserialize<List<CraftIngredientDto>>(def.RecipeJson);
        if (ingredients is null || ingredients.Count == 0)
            return (false, "Список ингредиентов рецепта пуст.");

        var resolvedIngredients = new List<(ItemDefinitionEntity Def, int Qty)>();
        foreach (var ing in ingredients)
        {
            var ingDef = await itemRepo.GetDefinitionByCodeAsync(guildId, ing.ItemCode, ct);
            if (ingDef is null)
                return (false, $"Не найден ингредиент `{ing.ItemCode}`.");

            var owned = await itemRepo.GetUserItemQuantityAsync(guildId, userId, ingDef.Id, ct);
            if (owned < ing.Quantity)
                return (false, $"Недостаточно материала **{ingDef.Name}**: требуется {ing.Quantity} шт., у вас {owned} шт.");

            resolvedIngredients.Add((ingDef, ing.Quantity));
        }

        foreach (var (ingDef, qty) in resolvedIngredients)
        {
            var ok = await itemRepo.TryConsumeItemAsync(guildId, userId, ingDef.Id, qty, ct);
            if (!ok)
                return (false, $"Ошибка списания материала **{ingDef.Name}**.");
        }

        var added = await itemRepo.AddItemToUserAsync(guildId, userId, def.Id, 1, null, ct);
        if (!added.Success)
            return (false, added.Error);

        return (true, $"Вы успешно создали предмет **{def.Name}** ([{ItemService.FormatRarityLabel(def.Rarity)}])!");
    }

    public double GetMultiplier(ulong guildId, ulong userId, string effectType)
    {
        if (!_activeBoosts.TryGetValue((guildId, userId, effectType), out var boost))
            return 1.0;

        if (boost.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _activeBoosts.TryRemove((guildId, userId, effectType), out _);
            return 1.0;
        }

        return 1.0 + boost.MultiplierPercent / 100.0;
    }
}
