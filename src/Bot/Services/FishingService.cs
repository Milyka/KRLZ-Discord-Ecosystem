using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord.WebSocket;

namespace Bot.Services;

public sealed record ActiveCastSession(
    ulong GuildId,
    ulong UserId,
    string LocationKey,
    string RodName,
    string? BaitCode,
    string? BaitName,
    double CatchBonus,
    double RarityBonus,
    DateTimeOffset ExpiresAt
);

public sealed record PendingTradeOffer(
    long Id,
    ulong GuildId,
    ulong InitiatorId,
    long? InitiatorItemDefId,
    string? InitiatorItemName,
    int InitiatorItemQty,
    long InitiatorCoins,
    ulong PartnerId,
    long? PartnerItemDefId,
    string? PartnerItemName,
    int PartnerItemQty,
    long PartnerCoins,
    bool InitiatorConfirmed,
    bool PartnerConfirmed,
    DateTimeOffset ExpiresAt
);

public sealed class FishingService(
    ItemRepository itemRepo,
    ItemService itemService,
    UserRepository userRepo,
    AchievementService achievementService,
    ProgressionService progressionService,
    SettingsProvider settingsProvider)
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _cooldowns = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), ActiveCastSession> _casts = new();
    private readonly ConcurrentDictionary<long, PendingTradeOffer> _trades = new();
    private long _tradeSeq;

    public static string GetLocationTitle(string key) => key switch
    {
        "deep_trench" => "Глубоководная впадина KRLZ",
        "coral_reef" => "Коралловый риф",
        _ => "Тихий залив KRLZ"
    };

    public async Task<(bool Success, string Message, ActiveCastSession? Session)> StartCastAsync(
        ulong guildId,
        ulong userId,
        string locationKey,
        CancellationToken ct = default)
    {
        var cfg = settingsProvider.Current.Fishing;
        var now = DateTimeOffset.UtcNow;
        var key = (guildId, userId);

        if (_cooldowns.TryGetValue(key, out var until) && until > now)
        {
            var left = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
            return (false, $"Ваши снасти ещё не готовы. Подождите **{left} сек.** перед новым забросом.", null);
        }

        await itemService.EnsureDefaultCatalogAsync(guildId, ct);
        var inv = await itemRepo.GetUserInventoryAsync(guildId, userId, null, ct);

        var carbonRod = inv.FirstOrDefault(x => x.Definition.Code == "carbon_rod" && x.Inventory.Quantity > 0);
        var bambooRod = inv.FirstOrDefault(x => x.Definition.Code == "bamboo_rod" && x.Inventory.Quantity > 0);

        var rodName = "Самодельная леска (без удочки)";
        var catchBonus = 0.0;
        var rarityBonus = 0.0;

        if (carbonRod is not null)
        {
            rodName = carbonRod.Definition.Name;
            catchBonus += 0.18;
            rarityBonus += 0.15;
        }
        else if (bambooRod is not null)
        {
            rodName = bambooRod.Definition.Name;
            catchBonus += 0.07;
            rarityBonus += 0.03;
        }

        var shrimpBait = inv.FirstOrDefault(x => x.Definition.Code == "shrimp_bait" && x.Inventory.Quantity > 0);
        var wormBait = inv.FirstOrDefault(x => x.Definition.Code == "worm_bait" && x.Inventory.Quantity > 0);

        string? baitCode = null;
        string? baitName = null;

        if (shrimpBait is not null)
        {
            var consumed = await itemRepo.TryConsumeItemAsync(guildId, userId, shrimpBait.Definition.Id, 1, ct);
            if (consumed)
            {
                baitCode = shrimpBait.Definition.Code;
                baitName = shrimpBait.Definition.Name;
                catchBonus += 0.10;
                rarityBonus += 0.12;
            }
        }
        else if (wormBait is not null)
        {
            var consumed = await itemRepo.TryConsumeItemAsync(guildId, userId, wormBait.Definition.Id, 1, ct);
            if (consumed)
            {
                baitCode = wormBait.Definition.Code;
                baitName = wormBait.Definition.Name;
                catchBonus += 0.05;
                rarityBonus += 0.04;
            }
        }

        if (locationKey == "coral_reef")
        {
            rarityBonus += 0.05;
        }
        else if (locationKey == "deep_trench")
        {
            catchBonus -= 0.05;
            rarityBonus += 0.12;
        }

        _cooldowns[key] = now.AddSeconds(cfg.CooldownSeconds);

        var session = new ActiveCastSession(
            guildId,
            userId,
            locationKey,
            rodName,
            baitCode,
            baitName ?? "Без наживки",
            catchBonus,
            rarityBonus,
            now.AddSeconds(cfg.HookReactionSeconds));

        _casts[key] = session;
        return (true, string.Empty, session);
    }

    public async Task<(bool Caught, string Title, string Description)> ResolveHookAsync(
        SocketGuild guild,
        ulong userId,
        string currencySymbol,
        ISocketMessageChannel? channel = null,
        CancellationToken ct = default)
    {
        var key = (guild.Id, userId);
        if (!_casts.TryRemove(key, out var session))
        {
            return (false, "Поклёвка упущена", "У вас нет активного заброса или вы уже подсекли рыбу.");
        }

        if (DateTimeOffset.UtcNow > session.ExpiresAt)
        {
            return (false, "Рыба сорвалась!", "Вы слишком долго ждали — рыба съела приманку и уплыла.");
        }

        var cfg = settingsProvider.Current.Fishing;
        var totalChance = Math.Clamp(cfg.BaseCatchChance + session.CatchBonus, 0.20, 0.96);
        var roll = CasinoService.NextCryptoDouble();

        if (roll > totalChance)
        {
            return (false, "Сорвалась с крючка!", $"Рыба дёрнула поплавок в локации **{GetLocationTitle(session.LocationKey)}**, но ушла на глубину.");
        }

        var caughtCode = SelectFishByWeightTable(cfg.LootTable, session.RarityBonus);
        var fishDef = await itemRepo.GetDefinitionByCodeAsync(guild.Id, caughtCode, ct);
        if (fishDef is null)
            return (false, "Ошибка каталога", "Не удалось определить пойманную рыбу.");

        await itemRepo.AddItemToUserAsync(guild.Id, userId, fishDef.Id, 1, null, ct);

        var extraLines = new List<string>();
        var eventRoll = CasinoService.NextCryptoDouble();

        if (eventRoll < cfg.PearlEventChance)
        {
            var pearlDef = await itemRepo.GetDefinitionByCodeAsync(guild.Id, "river_pearl", ct);
            if (pearlDef is not null)
            {
                await itemRepo.AddItemToUserAsync(guild.Id, userId, pearlDef.Id, 1, null, ct);
                extraLines.Add($"• Особое событие: вместе с рыбой вы вытащили со дна **{pearlDef.Name}**!");
            }
        }
        else if (eventRoll < cfg.OreEventChance)
        {
            var oreDef = await itemRepo.GetDefinitionByCodeAsync(guild.Id, "iron_ore", ct);
            if (oreDef is not null)
            {
                await itemRepo.AddItemToUserAsync(guild.Id, userId, oreDef.Id, 1, null, ct);
                extraLines.Add($"• Попутная находка: на крючок зацепилась **{oreDef.Name}** x1.");
            }
        }
        else if (eventRoll < cfg.TreasureEventChance)
        {
            var treasureCoins = CasinoService.NextCryptoInt(60, 220);
            await userRepo.TryModifyBalanceAsync(guild.Id, userId, treasureCoins, "fishing_treasure", "Затонувший кошель на рыбалке", ct);
            extraLines.Add($"• Затонувший кошель: внутри оказалось **+{treasureCoins:N0} {currencySymbol}**!");
        }

        await achievementService.TrackEventAsync(guild, userId, "fish_caught", 1, isAbsoluteValue: false, channel, ct);
        await progressionService.TrackActivityAsync(guild.Id, userId, "fish_caught", 1, 20, ct);

        if (fishDef.Rarity == "legendary")
        {
            await achievementService.TrackEventAsync(guild, userId, "fish_legendary", 1, isAbsoluteValue: false, channel, ct);
        }

        var lines = new List<string>
        {
            $"🐟 **Улов:** **{fishDef.Name}** (`{fishDef.Code}`)",
            $"• **Редкость:** {ItemService.FormatRarityLabel(fishDef.Rarity)}",
            $"• **Цена скупки:** {fishDef.SellPrice:N0} {currencySymbol}",
            $"• **Снасть:** {session.RodName} | **Наживка:** {session.BaitName}"
        };
        lines.AddRange(extraLines);

        return (true, "Отличная подсечка!", string.Join(Environment.NewLine, lines));
    }

    private static string SelectFishByWeightTable(IReadOnlyList<FishLootWeightConfig> table, double rarityBonus)
    {
        if (table.Count == 0)
            return "perch";

        var weighted = new List<(string Code, int EffectiveWeight)>(table.Count);
        var totalWeight = 0;

        foreach (var entry in table)
        {
            var bonusAdd = (int)Math.Round(entry.RarityBonusWeightMultiplier * Math.Max(0.0, rarityBonus));
            var eff = Math.Max(1, entry.BaseWeight + bonusAdd);
            weighted.Add((entry.Code, eff));
            totalWeight += eff;
        }

        var roll = CasinoService.NextCryptoInt(0, totalWeight);
        var cumulative = 0;

        foreach (var item in weighted)
        {
            cumulative += item.EffectiveWeight;
            if (roll < cumulative)
                return item.Code;
        }

        return weighted[0].Code;
    }

    public PendingTradeOffer CreateTradeOffer(
        ulong guildId,
        ulong initiatorId,
        long? itemDefA,
        string? itemNameA,
        int qtyA,
        long coinsA,
        ulong partnerId,
        long? itemDefB,
        string? itemNameB,
        int qtyB,
        long coinsB)
    {
        var id = Interlocked.Increment(ref _tradeSeq);
        var offer = new PendingTradeOffer(
            id,
            guildId,
            initiatorId,
            itemDefA,
            itemNameA,
            qtyA,
            coinsA,
            partnerId,
            itemDefB,
            itemNameB,
            qtyB,
            coinsB,
            InitiatorConfirmed: true,
            PartnerConfirmed: false,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        _trades[id] = offer;
        return offer;
    }

    public PendingTradeOffer? GetTradeOffer(long tradeId) =>
        _trades.TryGetValue(tradeId, out var offer) && offer.ExpiresAt > DateTimeOffset.UtcNow ? offer : null;

    public void RemoveTradeOffer(long tradeId) =>
        _trades.TryRemove(tradeId, out _);
}
