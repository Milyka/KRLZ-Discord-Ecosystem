using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ShopModule(
    ShopRepository shopRepo,
    ItemService itemService,
    InventoryService inventoryService,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private const int PageSize = 5;
    private const int InvPageSize = 8;

    [Group("shop", "Магазин ролей, фонов профиля и уникальных предметов сервера")]
    public sealed class ShopSubgroup(
        ShopRepository shopRepo,
        ItemService itemService,
        GuildSettingsRepository settingsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("view", "Открыть витрину магазина KRLZ")]
        public async Task ViewShopCommandAsync()
        {
            await itemService.EnsureDefaultCatalogAsync(Context.Guild.Id);
            var items = await shopRepo.GetActiveItemsAsync(Context.Guild.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var (embed, comp) = BuildShopPage(items, 0, cfg.CurrencySymbol, embeds);
            await RespondAsync(embed: embed, components: comp);
        }

        [SlashCommand("add", "Добавить новый товар в магазин (для администраторов)")]
        [RequireAdmin]
        public async Task AddShopItemCommandAsync(
            [Summary("name", "Название товара")] string name,
            [Summary("price", "Цена в валюте сервера")] [MinValue(0)] long price,
            [Summary("type", "Тип товара"), Choice("Роль", "role"), Choice("Фон профиля", "background"), Choice("Предмет / Снасть", "item")] string type,
            [Summary("description", "Описание товара")] string description,
            [Summary("role", "Выдаваемая роль (если тип = Роль)")] SocketRole? role = null,
            [Summary("payload", "Код предмета (например bamboo_rod, worm_bait:5) или файл фона")] string? payload = null)
        {
            if (type == "role" && role is null)
            {
                await RespondAsync(embed: embeds.Error("Для товара типа 'Роль' необходимо указать параметр `role`."), ephemeral: true);
                return;
            }

            var id = await shopRepo.AddItemAsync(Context.Guild.Id, name.Trim(), description.Trim(), type, price, role?.Id, payload?.Trim());
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            await RespondAsync(embed: embeds.Success("Товар добавлен в магазин",
                $"**ID:** `#{id}`\n**Название:** {name}\n**Тип:** `{type}`\n**Цена:** {price:N0} {cfg.CurrencySymbol}"));
        }

        [SlashCommand("edit", "Редактировать существующий товар магазина (для администраторов)")]
        [RequireAdmin]
        public async Task EditShopItemCommandAsync(
            [Summary("item_id", "ID товара")] long itemId,
            [Summary("name", "Новое название")] string name,
            [Summary("price", "Новая цена")] [MinValue(0)] long price,
            [Summary("description", "Новое описание")] string description)
        {
            var updated = await shopRepo.UpdateItemAsync(itemId, Context.Guild.Id, name.Trim(), description.Trim(), price);
            if (!updated)
            {
                await RespondAsync(embed: embeds.Error($"Товар `#{itemId}` не найден."), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Success("Товар обновлён", $"Параметры товара `#{itemId}` (**{name}**) сохранены."));
        }

        [SlashCommand("remove", "Удалить товар из магазина (для администраторов)")]
        [RequireAdmin]
        public async Task RemoveShopItemCommandAsync([Summary("item_id", "ID товара")] long itemId)
        {
            var removed = await shopRepo.RemoveItemAsync(itemId, Context.Guild.Id);
            if (!removed)
            {
                await RespondAsync(embed: embeds.Error($"Товар `#{itemId}` не найден."), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Success("Товар удалён", $"Товар `#{itemId}` снят с продажи."));
        }
    }

    [SlashCommand("inventory", "Открыть инвентарь предметов, улова и покупок с фильтрами и пагинацией")]
    public async Task InventoryCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser? target = null,
        [Summary("category", "Фильтр по категории"),
         Choice("Все предметы", "all"),
         Choice("Рыба и улов", "fish"),
         Choice("Удилища", "rod"),
         Choice("Наживка", "bait"),
         Choice("Материалы для крафта", "material"),
         Choice("Расходники и бусты", "consumable"),
         Choice("Покупки из магазина (роли и фоны)", "purchases")] string category = "all")
    {
        var member = target ?? (SocketGuildUser)Context.User;
        var (embed, comp) = await BuildInventoryViewAsync(member.Id, member.DisplayName, category, 0);
        await RespondAsync(embed: embed, components: comp);
    }

    [ComponentInteraction("inv:page:*:*:*", true)]
    public async Task HandleInventoryPageAsync(string userIdStr, string category, string pageStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !int.TryParse(pageStr, out var page))
            return;

        var memberName = Context.Guild.GetUser(userId)?.DisplayName ?? $"ID {userId}";
        var (embed, comp) = await BuildInventoryViewAsync(userId, memberName, category, page);
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embed;
            m.Components = comp;
        });
    }

    [ComponentInteraction("shop:page:*", true)]
    public async Task HandleShopPageButtonAsync(string pageStr)
    {
        if (!int.TryParse(pageStr, out var page))
            return;

        var items = await shopRepo.GetActiveItemsAsync(Context.Guild.Id);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var (embed, comp) = BuildShopPage(items, page, cfg.CurrencySymbol, embeds);

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embed;
            m.Components = comp;
        });
    }

    [ComponentInteraction("shop:select_buy", true)]
    public async Task HandleSelectBuyAsync(string[] values)
    {
        if (!long.TryParse(values.FirstOrDefault(), out var itemId))
            return;

        var item = await shopRepo.GetItemByIdAsync(itemId, Context.Guild.Id);
        if (item is null)
        {
            await RespondAsync(embed: embeds.Error("Товар не найден."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var confirmEmbed = embeds.CreateBase("Подтверждение покупки")
            .WithDescription($"Вы действительно хотите приобрести **{item.Name}** за **{item.Price:N0} {cfg.CurrencySymbol}**?\n\n*{item.Description}*")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Подтвердить покупку", $"shop:confirm:{item.Id}", ButtonStyle.Success)
            .WithButton("Отмена", "shop:cancel", ButtonStyle.Secondary)
            .Build();

        await RespondAsync(embed: confirmEmbed, components: comp, ephemeral: true);
    }

    [ComponentInteraction("shop:confirm:*", true)]
    public async Task HandleConfirmBuyAsync(string itemIdStr)
    {
        if (!long.TryParse(itemIdStr, out var itemId))
            return;

        var item = await shopRepo.GetItemByIdAsync(itemId, Context.Guild.Id);
        if (item is null)
        {
            await RespondAsync(embed: embeds.Error("Товар больше недоступен."), ephemeral: true);
            return;
        }

        var (ok, err) = await shopRepo.PurchaseItemAsync(Context.Guild.Id, Context.User.Id, itemId);
        if (!ok)
        {
            await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
            {
                m.Embed = embeds.Error(err);
                m.Components = null;
            });
            return;
        }

        var member = (SocketGuildUser)Context.User;
        if (item.ItemType == "role" && item.RoleId.HasValue && Context.Guild.GetRole(item.RoleId.Value) is { } role)
        {
            if (Context.Guild.CurrentUser.Hierarchy > role.Position)
                await member.AddRoleAsync(role);
        }

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Success("Покупка успешно совершена!",
                $"Вы приобрели **{item.Name}**.\nПроверьте свои предметы через `/inventory` или карточку `/profile`.");
            m.Components = null;
        });
    }

    [ComponentInteraction("shop:cancel", true)]
    public async Task HandleCancelBuyAsync()
    {
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Info("Покупка отменена", "Операция отменена пользователем.");
            m.Components = null;
        });
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildInventoryViewAsync(
        ulong userId,
        string displayName,
        string category,
        int page)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var lines = new List<string>();

        if (category == "purchases")
        {
            var purchases = await shopRepo.GetUserInventoryAsync(Context.Guild.Id, userId);
            foreach (var (p, item) in purchases)
            {
                var roleTag = item.RoleId.HasValue ? $" (<@&{item.RoleId.Value}>)" : string.Empty;
                lines.Add($"• **{item.Name}**{roleTag} — `{item.ItemType}` • Куплено за {p.PricePaid:N0} {cfg.CurrencySymbol}");
            }
        }
        else
        {
            var filter = category == "all" ? null : category;
            var stacks = await inventoryService.GetInventoryAsync(Context.Guild.Id, userId, filter);
            foreach (var s in stacks)
            {
                var useHint = s.Definition.Usable ? " • `[/use]`" : string.Empty;
                lines.Add($"• **{s.Definition.Name}** (`{s.Definition.Code}`) x**{s.Inventory.Quantity}** — [{ItemService.FormatRarityLabel(s.Definition.Rarity)}] • Скупка: {s.Definition.SellPrice:N0} {cfg.CurrencySymbol}{useHint}");
            }
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)InvPageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var slice = lines.Skip(page * InvPageSize).Take(InvPageSize).ToList();

        var desc = slice.Count > 0
            ? string.Join(Environment.NewLine, slice)
            : "В выбранной категории пока нет предметов. Загляните в `/shop view` или отправляйтесь на `/fish`!";

        var embed = embeds.Info($"Инвентарь — {displayName} [{category}] (Стр. {page + 1}/{totalPages})", desc);
        var comp = new ComponentBuilder()
            .WithButton("Назад", $"inv:page:{userId}:{category}:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
            .WithButton("Вперёд", $"inv:page:{userId}:{category}:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages - 1)
            .Build();

        return (embed, comp);
    }

    private static (Embed Embed, MessageComponent Components) BuildShopPage(
        IReadOnlyList<ShopItemEntity> items,
        int page,
        string currencySymbol,
        EmbedFactory embeds)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)PageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var slice = items.Skip(page * PageSize).Take(PageSize).ToList();
        var builder = embeds.CreateBase($"Магазин сервера KRLZ (Стр. {page + 1}/{totalPages})");

        if (slice.Count == 0)
        {
            builder.WithDescription("В магазине пока нет доступных товаров. Администратор может добавить их через `/shop add`.");
            return (builder.Build(), new ComponentBuilder().Build());
        }

        var select = new SelectMenuBuilder()
            .WithCustomId("shop:select_buy")
            .WithPlaceholder("Выберите товар для покупки...");

        foreach (var item in slice)
        {
            var typeTag = item.ItemType switch
            {
                "role" => "Роль",
                "background" => "Фон профиля",
                _ => "Предмет"
            };
            var extra = item.RoleId.HasValue ? $" • <@&{item.RoleId.Value}>" : string.Empty;
            builder.AddField(
                $"#{item.Id} • {item.Name} — {item.Price:N0} {currencySymbol}",
                $"Тип: `{typeTag}`{extra}\n{item.Description}",
                false);

            select.AddOption($"{item.Name} ({item.Price:N0} {currencySymbol})", item.Id.ToString(), item.Description.Length > 80 ? item.Description[..80] : item.Description);
        }

        var comp = new ComponentBuilder()
            .WithSelectMenu(select)
            .WithButton("Назад", $"shop:page:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0, row: 1)
            .WithButton("Вперёд", $"shop:page:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages - 1, row: 1)
            .Build();

        return (builder.Build(), comp);
    }
}
