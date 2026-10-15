using System.Text.Json;
using Bot.Data;
using Bot.Models;

namespace Bot.Services;

public sealed class ItemService(ItemRepository itemRepo)
{
    private static readonly HashSet<string> ValidRarities = new(StringComparer.OrdinalIgnoreCase)
    {
        "common",
        "rare",
        "epic",
        "legendary"
    };

    public static string FormatRarityLabel(string rarity) => rarity.ToLowerInvariant() switch
    {
        "legendary" => "Легендарный",
        "epic" => "Эпический",
        "rare" => "Редкий",
        _ => "Обычный"
    };

    public static bool IsValidRarity(string rarity) => ValidRarities.Contains(rarity);

    public async Task EnsureDefaultCatalogAsync(ulong guildId, CancellationToken ct = default)
    {
        var existing = await itemRepo.GetAllDefinitionsAsync(guildId, null, ct);
        if (existing.Count > 0)
            return;

        var xpBoostEffect = JsonSerializer.Serialize(new ItemEffectDto("xp_boost", 50, 60));
        var coinBoostEffect = JsonSerializer.Serialize(new ItemEffectDto("coin_boost", 50, 60));

        var carbonRodRecipe = JsonSerializer.Serialize(new List<CraftIngredientDto>
        {
            new("bamboo_rod", 1),
            new("iron_ore", 5),
            new("river_pearl", 2)
        });

        var luckyAmuletRecipe = JsonSerializer.Serialize(new List<CraftIngredientDto>
        {
            new("river_pearl", 3),
            new("golden_carp", 1)
        });

        var defaults = new (string Code, string Name, string Desc, string Cat, string Rarity, int Stack, long Buy, long Sell, bool Trade, bool Use, string? Effect, string? Recipe)[]
        {
            ("bamboo_rod", "Бамбуковая удочка", "Базовая снасть для начинающих рыболовов (+7% к шансу клёва)", "rod", "common", 5, 500, 200, true, false, null, null),
            ("carbon_rod", "Карбоновый спиннинг", "Профессиональное удилище (+18% к шансу улова и редких рыб)", "rod", "epic", 3, 4500, 1800, true, false, null, carbonRodRecipe),
            ("worm_bait", "Наживка: Червь", "Классическая наживка для речной рыбы (расходуется при забросе)", "bait", "common", 99, 25, 10, true, false, null, null),
            ("shrimp_bait", "Наживка: Креветка", "Привлекает крупную и редкую рыбу (+12% к редкости улова)", "bait", "rare", 99, 90, 35, true, false, null, null),
            ("perch", "Речной окунь", "Обычный улов из тихого залива KRLZ", "fish", "common", 99, 60, 45, true, false, null, null),
            ("pike", "Хищная щука", "Крупная речная рыба, ценится на рынке", "fish", "rare", 50, 180, 130, true, false, null, null),
            ("electric_eel", "Электрический угорь", "Редкий обитатель глубоководных течений", "fish", "epic", 25, 450, 340, true, false, null, null),
            ("golden_carp", "Золотой карп KRLZ", "Легендарный сияющий трофей, приносящий богатство", "fish", "legendary", 10, 1800, 1400, true, false, null, null),
            ("iron_ore", "Железная руда", "Материал для создания прочных снастей и предметов", "material", "common", 99, 120, 50, true, false, null, null),
            ("river_pearl", "Речной жемчуг", "Редкая жемчужина со дна озера для крафта артефактов", "material", "rare", 50, 400, 220, true, false, null, null),
            ("lucky_amulet", "Амулет удачи KRLZ", "Повышает заработок монет на 50% на 60 минут при активации", "consumable", "epic", 20, 2500, 1000, true, true, coinBoostEffect, luckyAmuletRecipe),
            ("xp_elixir", "Эликсир опыта", "Увеличивает получаемый опыт на 50% в течение 60 минут", "consumable", "rare", 20, 1800, 750, true, true, xpBoostEffect, null)
        };

        foreach (var d in defaults)
        {
            await itemRepo.CreateOrUpdateDefinitionAsync(
                guildId,
                d.Code,
                d.Name,
                d.Desc,
                d.Cat,
                d.Rarity,
                d.Stack,
                d.Buy,
                d.Sell,
                d.Trade,
                d.Use,
                d.Effect,
                d.Recipe,
                ct);
        }
    }

    public async Task<IReadOnlyList<ItemDefinitionEntity>> GetCatalogAsync(ulong guildId, string? category = null, CancellationToken ct = default)
    {
        await EnsureDefaultCatalogAsync(guildId, ct);
        return await itemRepo.GetAllDefinitionsAsync(guildId, category, ct);
    }

    public async Task<ItemDefinitionEntity?> FindByCodeOrNameAsync(ulong guildId, string query, CancellationToken ct = default)
    {
        await EnsureDefaultCatalogAsync(guildId, ct);
        var byCode = await itemRepo.GetDefinitionByCodeAsync(guildId, query, ct);
        if (byCode is not null)
            return byCode;

        var all = await itemRepo.GetAllDefinitionsAsync(guildId, null, ct);
        return all.FirstOrDefault(x => x.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(x => x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
