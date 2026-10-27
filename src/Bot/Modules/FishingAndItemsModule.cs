using System.Text;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class FishingAndItemsModule(
    FishingService fishingService,
    ItemService itemService,
    InventoryService inventoryService,
    ItemRepository itemRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("fish", "Забросить удочку и сыграть в мини-игру с подсечкой")]
    public async Task FishCommandAsync(
        [Summary("location", "Рыболовная локация"),
         Choice("Тихий залив KRLZ (высокий шанс клёва)", "calm_bay"),
         Choice("Коралловый риф (+5% к редким видам)", "coral_reef"),
         Choice("Глубоководная впадина (+12% к редким и легендарным)", "deep_trench")] string location = "calm_bay")
    {
        await DeferAsync();
        var (ok, err, session) = await fishingService.StartCastAsync(Context.Guild.Id, Context.User.Id, location);
        if (!ok || session is null)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var lines = new[]
        {
            $"🎣 Вы забросили снасть в локации **{FishingService.GetLocationTitle(location)}**!",
            $"• **Удилище:** {session.RodName}",
            $"• **Наживка:** {session.BaitName}",
            string.Empty,
            "Поплавок ушёл под воду! Нажмите **Подсечь!** до того, как рыба сорвётся!"
        };

        var comp = new ComponentBuilder()
            .WithButton("🎣 Подсечь!", $"fish:hook:{Context.User.Id}", ButtonStyle.Success)
            .Build();

        await FollowupAsync(embed: embeds.Info("Рыбалка KRLZ — Поклёвка!", string.Join(Environment.NewLine, lines)), components: comp);
    }

    [ComponentInteraction("fish:hook:*", true)]
    public async Task HandleFishHookButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
        {
            await RespondAsync(embed: embeds.Error("Эта удочка принадлежит другому рыболову."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var (caught, title, desc) = await fishingService.ResolveHookAsync(
            Context.Guild, userId, cfg.CurrencySymbol, Context.Channel);

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = caught ? embeds.Success(title, desc) : embeds.Warning(title, desc);
            m.Components = null;
        });
    }

    [SlashCommand("sell", "Продать пойманную рыбу или предмет из инвентаря скупщику")]
    public async Task SellCommandAsync(
        [Summary("item", "Код или название предмета (или 'all_fish' для продажи всей рыбы)")] string itemQuery,
        [Summary("quantity", "Количество для продажи")] [MinValue(1)] int quantity = 1)
    {
        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (itemQuery.Trim().Equals("all_fish", StringComparison.OrdinalIgnoreCase))
        {
            var fishStacks = await inventoryService.GetInventoryAsync(Context.Guild.Id, Context.User.Id, "fish");
            if (fishStacks.Count == 0)
            {
                await FollowupAsync(embed: embeds.Error("В вашем инвентаре сейчас нет пойманной рыбы."), ephemeral: true);
                return;
            }

            long totalCoins = 0;
            var totalFish = 0;
            foreach (var stack in fishStacks)
            {
                var (soldOk, _, earned) = await itemRepo.SellItemAsync(
                    Context.Guild.Id, Context.User.Id, stack.Definition.Id, stack.Inventory.Quantity);
                if (soldOk)
                {
                    totalCoins += earned;
                    totalFish += stack.Inventory.Quantity;
                }
            }

            await FollowupAsync(embed: embeds.Success("Улов продан скупщику",
                $"Вы продали **{totalFish} шт.** рыбы и выручили **{totalCoins:N0} {cfg.CurrencySymbol}**!"));
            return;
        }

        var (ok, msg, coins) = await inventoryService.SellItemByCodeAsync(Context.Guild.Id, Context.User.Id, itemQuery, quantity);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Продажа завершена",
            $"{msg}\nПолучено: **+{coins:N0} {cfg.CurrencySymbol}**."));
    }

    [SlashCommand("craft", "Открыть мастерскую крафта предметов и снастей KRLZ")]
    public async Task CraftCommandAsync()
    {
        await DeferAsync();
        var catalog = await itemService.GetCatalogAsync(Context.Guild.Id);
        var craftables = catalog.Where(x => !string.IsNullOrWhiteSpace(x.RecipeJson)).ToList();

        if (craftables.Count == 0)
        {
            await FollowupAsync(embed: embeds.Info("Мастерская KRLZ", "Доступных рецептов крафта пока нет."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        var select = new SelectMenuBuilder()
            .WithCustomId("craft:select")
            .WithPlaceholder("Выберите предмет для создания...");

        foreach (var item in craftables.Take(25))
        {
            var ingredients = JsonSerializer.Deserialize<List<CraftIngredientDto>>(item.RecipeJson!) ?? [];
            var ingStr = string.Join(", ", ingredients.Select(i => $"`{i.ItemCode}` x{i.Quantity}"));

            sb.AppendLine($"• **{item.Name}** (`{item.Code}`) [{ItemService.FormatRarityLabel(item.Rarity)}]");
            sb.AppendLine($"  Требуется: {ingStr}");
            sb.AppendLine($"  *{item.Description}*\n");

            select.AddOption(item.Name, item.Code, $"Редкость: {ItemService.FormatRarityLabel(item.Rarity)}");
        }

        var comp = new ComponentBuilder().WithSelectMenu(select).Build();
        await FollowupAsync(embed: embeds.Info("Мастерская крафта KRLZ", sb.ToString()), components: comp);
    }

    [ComponentInteraction("craft:select", true)]
    public async Task HandleCraftSelectAsync(string[] values)
    {
        var code = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code))
            return;

        await DeferAsync();
        var (ok, msg) = await inventoryService.CraftItemByCodeAsync(Context.Guild.Id, Context.User.Id, code);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Предмет создан!", msg));
    }

    [SlashCommand("use", "Использовать расходуемый предмет или буст из инвентаря")]
    public async Task UseItemCommandAsync([Summary("item", "Код или название предмета")] string itemQuery)
    {
        await DeferAsync();
        var (ok, msg) = await inventoryService.UseItemByCodeAsync(Context.Guild.Id, Context.User.Id, itemQuery);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Предмет использован", msg));
    }

    [SlashCommand("trade", "Предложить защищённый обмен предметами и валютой другому участнику")]
    public async Task TradeCommandAsync(
        [Summary("partner", "Второй участник сделки")] SocketGuildUser partner,
        [Summary("my_item", "Код вашего предмета (необязательно)")] string? myItemQuery = null,
        [Summary("my_qty", "Количество вашего предмета")] [MinValue(1)] int myQty = 1,
        [Summary("my_coins", "Ваша доплата в валюте")] [MinValue(0)] long myCoins = 0,
        [Summary("their_item", "Код запрашиваемого у партнёра предмета (необязательно)")] string? theirItemQuery = null,
        [Summary("their_qty", "Количество предмета партнёра")] [MinValue(1)] int theirQty = 1,
        [Summary("their_coins", "Запрашиваемая сумма валюты у партнёра")] [MinValue(0)] long theirCoins = 0)
    {
        if (partner.IsBot || partner.Id == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Выберите другого реального участника сервера для обмена."), ephemeral: true);
            return;
        }

        await DeferAsync();
        ItemDefinitionEntity? defA = null;
        if (!string.IsNullOrWhiteSpace(myItemQuery))
        {
            defA = await itemService.FindByCodeOrNameAsync(Context.Guild.Id, myItemQuery);
            if (defA is null || !defA.Tradeable)
            {
                await FollowupAsync(embed: embeds.Error($"Предмет `{myItemQuery}` не найден или не подлежит обмену."), ephemeral: true);
                return;
            }
        }

        ItemDefinitionEntity? defB = null;
        if (!string.IsNullOrWhiteSpace(theirItemQuery))
        {
            defB = await itemService.FindByCodeOrNameAsync(Context.Guild.Id, theirItemQuery);
            if (defB is null || !defB.Tradeable)
            {
                await FollowupAsync(embed: embeds.Error($"Предмет `{theirItemQuery}` не найден или не подлежит обмену."), ephemeral: true);
                return;
            }
        }

        if (defA is null && myCoins <= 0 && defB is null && theirCoins <= 0)
        {
            await FollowupAsync(embed: embeds.Error("Укажите хотя бы один предмет или сумму валюты для обмена."), ephemeral: true);
            return;
        }

        var offer = fishingService.CreateTradeOffer(
            Context.Guild.Id,
            Context.User.Id,
            defA?.Id,
            defA?.Name,
            defA is not null ? myQty : 0,
            myCoins,
            partner.Id,
            defB?.Id,
            defB?.Name,
            defB is not null ? theirQty : 0,
            theirCoins);

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var sideALine = FormatTradeSide(offer.InitiatorItemName, offer.InitiatorItemQty, offer.InitiatorCoins, cfg.CurrencySymbol);
        var sideBLine = FormatTradeSide(offer.PartnerItemName, offer.PartnerItemQty, offer.PartnerCoins, cfg.CurrencySymbol);

        var desc = string.Join(Environment.NewLine,
            $"**Предложение от <@{Context.User.Id}>:** {sideALine}",
            $"**Взамен от <@{partner.Id}>:** {sideBLine}",
            string.Empty,
            $"Участник <@{partner.Id}>, подтвердите сделку кнопкой ниже (предложение действует 5 минут):");

        var comp = new ComponentBuilder()
            .WithButton("Подтвердить обмен", $"trade:accept:{offer.Id}", ButtonStyle.Success)
            .WithButton("Отклонить", $"trade:cancel:{offer.Id}", ButtonStyle.Danger)
            .Build();

        await FollowupAsync(text: $"<@{partner.Id}>", embed: embeds.Info($"Торговая сделка #{offer.Id}", desc), components: comp);
    }

    [ComponentInteraction("trade:*:*", true)]
    public async Task HandleTradeButtonAsync(string action, string tradeIdStr)
    {
        if (!long.TryParse(tradeIdStr, out var tradeId))
            return;

        await DeferAsync();
        var offer = fishingService.GetTradeOffer(tradeId);
        if (offer is null)
        {
            await FollowupAsync(embed: embeds.Error("Срок действия предложения обмена истёк."), ephemeral: true);
            return;
        }

        if (action == "cancel")
        {
            if (Context.User.Id != offer.InitiatorId && Context.User.Id != offer.PartnerId)
            {
                await FollowupAsync(embed: embeds.Error("Вы не являетесь стороной этой сделки."), ephemeral: true);
                return;
            }

            fishingService.RemoveTradeOffer(tradeId);
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Warning("Обмен отменён", $"Сделка `#{offer.Id}` отменена участником <@{Context.User.Id}>.");
                m.Components = null;
            });
            return;
        }

        if (Context.User.Id != offer.PartnerId)
        {
            await FollowupAsync(embed: embeds.Error("Подтвердить сделку должен второй участник обмена."), ephemeral: true);
            return;
        }

        fishingService.RemoveTradeOffer(tradeId);
        var (ok, err) = await itemRepo.ExecuteAtomicTradeAsync(
            Context.Guild.Id,
            offer.InitiatorId,
            offer.InitiatorItemDefId,
            offer.InitiatorItemQty,
            offer.InitiatorCoins,
            offer.PartnerId,
            offer.PartnerItemDefId,
            offer.PartnerItemQty,
            offer.PartnerCoins);

        if (!ok)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Error($"Сделка не состоялась: {err}");
                m.Components = null;
            });
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Обмен успешно совершён!",
                $"Сделка `#{offer.Id}` между <@{offer.InitiatorId}> и <@{offer.PartnerId}> завершена!");
            m.Components = null;
        });
    }

    [Group("item", "Административное управление каталогом предметов и выдача предметов")]
    [RequireAdmin]
    public sealed class ItemAdminSubgroup(
        ItemRepository itemRepo,
        InventoryService inventoryService,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("create", "Создать новый предмет в каталоге сервера")]
        public async Task CreateItemDefCommandAsync(
            [Summary("code", "Уникальный латинский код предмета (например ruby_ring)")] string code,
            [Summary("name", "Отображаемое название")] string name,
            [Summary("category", "Категория"),
             Choice("Рыба (fish)", "fish"),
             Choice("Удилище (rod)", "rod"),
             Choice("Наживка (bait)", "bait"),
             Choice("Материал (material)", "material"),
             Choice("Расходник / Буст (consumable)", "consumable")] string category,
            [Summary("rarity", "Редкость"),
             Choice("Обычный", "common"),
             Choice("Редкий", "rare"),
             Choice("Эпический", "epic"),
             Choice("Легендарный", "legendary")] string rarity,
            [Summary("buy_price", "Базовая цена")] [MinValue(0)] long buyPrice,
            [Summary("sell_price", "Цена продажи скупщику")] [MinValue(0)] long sellPrice,
            [Summary("description", "Описание предмета")] string description,
            [Summary("max_stack", "Максимум в стаке")] [MinValue(1), MaxValue(999)] int maxStack = 99,
            [Summary("usable", "Можно ли активировать через /use")] bool usable = false,
            [Summary("effect_json", "JSON эффекта для /use (необязательно)")] string? effectJson = null,
            [Summary("recipe_json", "JSON рецепта для /craft (необязательно)")] string? recipeJson = null)
        {
            await DeferAsync();
            var id = await itemRepo.CreateOrUpdateDefinitionAsync(
                Context.Guild.Id, code, name, description, category, rarity,
                maxStack, buyPrice, sellPrice, true, usable, effectJson, recipeJson);

            await FollowupAsync(embed: embeds.Success("Предмет сохранён в каталоге",
                $"**ID:** `#{id}` • **Код:** `{code.ToLowerInvariant()}`\n**Название:** {name}\n**Редкость:** {ItemService.FormatRarityLabel(rarity)}"));
        }

        [SlashCommand("edit", "Изменить параметры существующего предмета каталога")]
        public async Task EditItemDefCommandAsync(
            [Summary("code", "Код предмета")] string code,
            [Summary("name", "Новое название")] string name,
            [Summary("buy_price", "Новая базовая цена")] [MinValue(0)] long buyPrice,
            [Summary("sell_price", "Новая цена скупки")] [MinValue(0)] long sellPrice,
            [Summary("description", "Новое описание")] string description)
        {
            await DeferAsync();
            var existing = await itemRepo.GetDefinitionByCodeAsync(Context.Guild.Id, code);
            if (existing is null)
            {
                await FollowupAsync(embed: embeds.Error($"Предмет с кодом `{code}` не найден."), ephemeral: true);
                return;
            }

            await itemRepo.CreateOrUpdateDefinitionAsync(
                Context.Guild.Id,
                existing.Code,
                name,
                description,
                existing.Category,
                existing.Rarity,
                existing.MaxStack,
                buyPrice,
                sellPrice,
                existing.Tradeable,
                existing.Usable,
                existing.EffectJson,
                existing.RecipeJson);

            await FollowupAsync(embed: embeds.Success("Предмет обновлён", $"Параметры предмета `{existing.Code}` (**{name}**) сохранены."));
        }

        [SlashCommand("remove", "Удалить предмет из каталога сервера")]
        public async Task RemoveItemDefCommandAsync([Summary("code", "Код предмета")] string code)
        {
            await DeferAsync();
            var deleted = await itemRepo.DeleteDefinitionAsync(Context.Guild.Id, code);
            if (!deleted)
            {
                await FollowupAsync(embed: embeds.Error($"Предмет с кодом `{code}` не найден."), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Warning("Предмет удалён", $"Предмет `{code}` удалён из каталога сервера."));
        }

        [SlashCommand("give", "Выдать предмет из каталога в инвентарь участника")]
        public async Task GiveItemCommandAsync(
            [Summary("user", "Получатель")] SocketGuildUser target,
            [Summary("code", "Код или название предмета")] string code,
            [Summary("quantity", "Количество")] [MinValue(1)] int quantity = 1)
        {
            await DeferAsync();
            var (ok, msg) = await inventoryService.GiveItemByCodeAsync(Context.Guild.Id, target.Id, code, quantity);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Предмет выдан", $"Участнику <@{target.Id}>: {msg}"));
        }
    }

    private static string FormatTradeSide(string? itemName, int qty, long coins, string currencySymbol)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(itemName) && qty > 0)
            parts.Add($"**{itemName}** x{qty}");
        if (coins > 0)
            parts.Add($"**{coins:N0} {currencySymbol}**");
        return parts.Count > 0 ? string.Join(" + ", parts) : "Ничего";
    }
}
