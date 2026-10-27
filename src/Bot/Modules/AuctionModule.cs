using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
[Group("auction", "Аукционный дом и биржа кастомных ролей сервера KRLZ")]
public sealed class AuctionModule(
    AuctionRepository auctionRepo,
    AuctionService auctionService,
    ItemService itemService,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private const int PageSize = 5;

    public sealed class BidModal : IModal
    {
        public string Title => "Сделать ставку на лот";

        [InputLabel("Сумма вашей ставки")]
        [ModalTextInput("amount", TextInputStyle.Short, "1000", minLength: 1, maxLength: 15)]
        public string Amount { get; set; } = string.Empty;
    }

    [SlashCommand("create", "Выставить предмет инвентаря или свою кастомную роль на торги")]
    public async Task CreateLotCommandAsync(
        [Summary("type", "Тип выставляемого лота"), Choice("Предмет из инвентаря", "item"), Choice("Личная кастомная роль", "role")] string type,
        [Summary("start_price", "Стартовая цена лота")] [MinValue(10)] long startPrice,
        [Summary("min_step", "Минимальный шаг повышения ставки")] [MinValue(5)] long minStep,
        [Summary("duration", "Длительность торгов (например: 30m, 6h, 1d, 3d)")] string duration = "12h",
        [Summary("item_code", "Код или название предмета (если тип = Предмет)")] string? itemQuery = null,
        [Summary("quantity", "Количество предметов")] [MinValue(1)] int quantity = 1,
        [Summary("role", "Ваша кастомная роль (если тип = Роль)")] SocketRole? role = null,
        [Summary("buyout_price", "Цена мгновенного выкупа (необязательно)")] [MinValue(20)] long? buyoutPrice = null)
    {
        if (!TimeParser.TryParseDuration(duration, out var span) || span < TimeSpan.FromMinutes(5) || span > TimeSpan.FromDays(7))
        {
            await RespondAsync(embed: embeds.Error("Длительность торгов должна быть от `5m` до `7d` (например: `2h`, `12h`, `2d`)."), ephemeral: true);
            return;
        }

        if (buyoutPrice.HasValue && buyoutPrice.Value <= startPrice)
        {
            await RespondAsync(embed: embeds.Error("Цена мгновенного выкупа должна быть строго выше стартовой цены."), ephemeral: true);
            return;
        }

        var aucCfg = settingsProvider.Current.Auction;
        var activeCount = await auctionRepo.GetActiveLotCountForSellerAsync(Context.Guild.Id, Context.User.Id);
        if (activeCount >= aucCfg.MaxActiveLotsPerUser)
        {
            await RespondAsync(embed: embeds.Error($"У вас уже открыто максимальное количество активных лотов ({aucCfg.MaxActiveLotsPerUser})."), ephemeral: true);
            return;
        }

        var endsAt = DateTimeOffset.UtcNow.Add(span);
        var guildCfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var targetChannelId = aucCfg.ChannelId != 0 ? aucCfg.ChannelId : 1566933458171994242UL;
        var targetChannel = Context.Guild.GetTextChannel(targetChannelId) ?? (ITextChannel)Context.Channel;

        if (type == "item")
        {
            if (string.IsNullOrWhiteSpace(itemQuery))
            {
                await RespondAsync(embed: embeds.Error("Для продажи предмета укажите его код или название в поле `item_code`."), ephemeral: true);
                return;
            }

            var def = await itemService.FindByCodeOrNameAsync(Context.Guild.Id, itemQuery);
            if (def is null)
            {
                await RespondAsync(embed: embeds.Error($"Предмет `{itemQuery}` не найден."), ephemeral: true);
                return;
            }

            var (ok, err, lot) = await auctionRepo.CreateItemLotAsync(
                Context.Guild.Id, Context.User.Id, def.Id, quantity, startPrice, minStep, buyoutPrice, endsAt);

            if (!ok || lot is null)
            {
                await RespondAsync(embed: embeds.Error(err), ephemeral: true);
                return;
            }

            await targetChannel.SendMessageAsync(
                embed: BuildLotCreatedEmbed(lot, guildCfg.CurrencySymbol),
                components: BuildSingleLotComponents(lot));

            var confirmationMsg = targetChannel.Id != Context.Channel.Id
                ? $"Ваш лот `#{lot.Id}` (**{lot.LotTitle}**) успешно выставлен на торги и опубликован в канале <#{targetChannel.Id}>!"
                : $"Лот `#{lot.Id}` (**{lot.LotTitle}**) успешно выставлен на аукцион!";

            await RespondAsync(embed: embeds.Success("Лот выставлен на аукцион", confirmationMsg), ephemeral: true);
            return;
        }

        if (role is null)
        {
            await RespondAsync(embed: embeds.Error("Для продажи кастомной роли выберите её в параметре `role`."), ephemeral: true);
            return;
        }

        var (roleOk, roleErr, roleLot) = await auctionRepo.CreateRoleLotAsync(
            Context.Guild.Id, Context.User.Id, role.Id, startPrice, minStep, buyoutPrice, endsAt);

        if (!roleOk || roleLot is null)
        {
            await RespondAsync(embed: embeds.Error(roleErr), ephemeral: true);
            return;
        }

        await targetChannel.SendMessageAsync(
            embed: BuildLotCreatedEmbed(roleLot, guildCfg.CurrencySymbol),
            components: BuildSingleLotComponents(roleLot));

        var roleConfirmationMsg = targetChannel.Id != Context.Channel.Id
            ? $"Ваш лот `#{roleLot.Id}` (**{roleLot.LotTitle}**) успешно выставлен на торги и опубликован в канале <#{targetChannel.Id}>!"
            : $"Лот `#{roleLot.Id}` (**{roleLot.LotTitle}**) успешно выставлен на аукцион!";

        await RespondAsync(embed: embeds.Success("Лот выставлен на аукцион", roleConfirmationMsg), ephemeral: true);
    }

    [SlashCommand("list", "Посмотреть список активных лотов на аукционе и бирже ролей")]
    public async Task ListAuctionsCommandAsync([Summary("page", "Номер страницы")] [MinValue(1)] int page = 1)
    {
        var targetChannelId = settingsProvider.Current.Auction.ChannelId != 0 ? settingsProvider.Current.Auction.ChannelId : 1566933458171994242UL;
        var (embed, comp) = await BuildAuctionPageAsync(page - 1);
        await RespondAsync(embed: embed, components: comp, ephemeral: Context.Channel.Id == targetChannelId);
    }

    [ComponentInteraction("auc:page:*", true)]
    public async Task HandlePageButtonAsync(string pageStr)
    {
        if (!int.TryParse(pageStr, out var page))
            return;

        var (embed, comp) = await BuildAuctionPageAsync(page);
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embed;
            m.Components = comp;
        });
    }

    [SlashCommand("bid", "Сделать ставку на выбранный лот аукциона")]
    public async Task BidCommandAsync(
        [Summary("lot_id", "Номер лота")] long lotId,
        [Summary("amount", "Сумма вашей ставки")] [MinValue(1)] long amount)
    {
        await ProcessBidInternalAsync(lotId, amount, isModalOrButton: false);
    }

    [ComponentInteraction("auc:bid_btn:*", true)]
    public async Task HandleBidButtonAsync(string lotIdStr)
    {
        if (!long.TryParse(lotIdStr, out var lotId))
            return;

        await RespondWithModalAsync<BidModal>($"auc:bid_modal:{lotId}");
    }

    [ComponentInteraction("auc:select_lot", true)]
    public async Task HandleSelectLotMenuAsync(string[] values)
    {
        if (!long.TryParse(values.FirstOrDefault(), out var lotId))
            return;

        var lot = await auctionRepo.GetByIdAsync(Context.Guild.Id, lotId);
        if (lot is null || lot.Status != "active")
        {
            await RespondAsync(embed: embeds.Error("Лот не найден или уже завершён."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await RespondAsync(
            embed: BuildLotCreatedEmbed(lot, cfg.CurrencySymbol),
            components: BuildSingleLotComponents(lot),
            ephemeral: true);
    }

    [ModalInteraction("auc:bid_modal:*", true)]
    public async Task HandleBidModalSubmitAsync(string lotIdStr, BidModal modal)
    {
        if (!long.TryParse(lotIdStr, out var lotId) || !long.TryParse(modal.Amount.Trim(), out var amount) || amount <= 0)
        {
            await RespondAsync(embed: embeds.Error("Укажите корректное положительное число для ставки."), ephemeral: true);
            return;
        }

        await ProcessBidInternalAsync(lotId, amount, isModalOrButton: true);
    }

    [SlashCommand("buyout", "Мгновенно выкупить лот по фиксированной цене выкупа")]
    public async Task BuyoutCommandAsync([Summary("lot_id", "Номер лота")] long lotId)
    {
        await ProcessBuyoutInternalAsync(lotId);
    }

    [ComponentInteraction("auc:buyout_btn:*", true)]
    public async Task HandleBuyoutButtonAsync(string lotIdStr)
    {
        if (!long.TryParse(lotIdStr, out var lotId))
            return;

        await ProcessBuyoutInternalAsync(lotId);
    }

    [SlashCommand("cancel", "Отменить свой лот (если по нему ещё нет ставок)")]
    public async Task CancelLotCommandAsync([Summary("lot_id", "Номер лота")] long lotId)
    {
        var member = (SocketGuildUser)Context.User;
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var isAdmin = member.Id == Context.Guild.OwnerId ||
                      member.GuildPermissions.Administrator ||
                      (cfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == cfg.AdminRoleId));

        var (ok, err, cancelled) = await auctionRepo.CancelLotAsync(Context.Guild.Id, lotId, member.Id, isAdmin);
        if (!ok || cancelled is null)
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var targetChannelId = settingsProvider.Current.Auction.ChannelId != 0 ? settingsProvider.Current.Auction.ChannelId : 1566933458171994242UL;
        await RespondAsync(embed: embeds.Warning("Лот снят с торгов",
            $"Лот `#{cancelled.Id}` (**{cancelled.LotTitle}**) отменён, выставленное имущество возвращено владельцу."),
            ephemeral: Context.Channel.Id == targetChannelId);
    }

    private async Task ProcessBidInternalAsync(long lotId, long amount, bool isModalOrButton)
    {
        var aucCfg = settingsProvider.Current.Auction;
        var (ok, err, outbidUser, outbidAmount, newEndsAt) = await auctionRepo.PlaceBidAsync(
            Context.Guild.Id,
            lotId,
            Context.User.Id,
            amount,
            aucCfg.AntiSnipeWindowSeconds,
            aucCfg.AntiSnipeExtendSeconds);

        if (!ok)
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var lot = await auctionRepo.GetByIdAsync(Context.Guild.Id, lotId);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (outbidUser.HasValue && lot is not null)
        {
            await auctionService.NotifyOutbidUserAsync(Context.Guild, outbidUser.Value, lot, amount, outbidAmount);
        }

        var desc = string.Join(Environment.NewLine,
            $"Участник <@{Context.User.Id}> сделал ставку на лот `#{lotId}` (**{lot?.LotTitle}**).",
            $"• **Новая лидирующая ставка:** `{amount:N0} {cfg.CurrencySymbol}`",
            $"• **Окончание торгов:** {TimestampTag.FromDateTimeOffset(newEndsAt, TimestampTagStyles.Relative)}");

        var targetChannelId = aucCfg.ChannelId != 0 ? aucCfg.ChannelId : 1566933458171994242UL;
        await RespondAsync(embed: embeds.Success("Ставка принята!", desc), ephemeral: isModalOrButton || Context.Channel.Id == targetChannelId);
    }

    private async Task ProcessBuyoutInternalAsync(long lotId)
    {
        var feePct = settingsProvider.Current.Auction.CommissionPercent;
        var (ok, err, lot, refundedBidder, refundedAmount, sellerPayout) = await auctionRepo.BuyoutAsync(
            Context.Guild.Id, lotId, Context.User.Id, feePct);

        if (!ok || lot is null)
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        if (lot.LotType == "role" && lot.CustomRoleId.HasValue)
        {
            await auctionService.TransferDiscordRoleOwnershipAsync(
                Context.Guild, lot.CustomRoleId.Value, lot.SellerId, Context.User.Id);
        }

        if (refundedBidder.HasValue && refundedAmount > 0)
        {
            await auctionService.NotifyOutbidUserAsync(Context.Guild, refundedBidder.Value, lot, lot.CurrentBid, refundedAmount);
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var summary = string.Join(Environment.NewLine,
            $"Покупатель <@{Context.User.Id}> мгновенно выкупил лот `#{lot.Id}` (**{lot.LotTitle}**)!",
            $"• **Сумма сделки:** `{lot.CurrentBid:N0} {cfg.CurrencySymbol}`",
            $"• **Выплата продавцу (<@{lot.SellerId}>):** `{sellerPayout:N0} {cfg.CurrencySymbol}` (комиссия {feePct}%)");

        var targetChannelId = settingsProvider.Current.Auction.ChannelId != 0 ? settingsProvider.Current.Auction.ChannelId : 1566933458171994242UL;
        await RespondAsync(embed: embeds.Success("Лот выкуплен!", summary), ephemeral: Context.Channel.Id == targetChannelId);
    }

    private Embed BuildLotCreatedEmbed(AuctionEntity lot, string currencySymbol)
    {
        var currentStr = lot.CurrentBid > 0
            ? $"{lot.CurrentBid:N0} {currencySymbol} (<@{lot.HighestBidderId}>)"
            : $"Нет ставок (старт: {lot.StartPrice:N0} {currencySymbol})";

        var buyoutStr = lot.BuyoutPrice.HasValue
            ? $"{lot.BuyoutPrice.Value:N0} {currencySymbol}"
            : "Не установлен";

        return embeds.CreateBase($"Аукционный лот #{lot.Id} — {lot.LotTitle}")
            .AddField("Продавец", $"<@{lot.SellerId}>", true)
            .AddField("Мин. шаг ставки", $"{lot.MinStep:N0} {currencySymbol}", true)
            .AddField("Мгновенный выкуп", buyoutStr, true)
            .AddField("Текущая ставка", currentStr, true)
            .AddField("Завершение", TimestampTag.FromDateTimeOffset(lot.EndsAt, TimestampTagStyles.Relative), true)
            .Build();
    }

    private static MessageComponent BuildSingleLotComponents(AuctionEntity lot)
    {
        var builder = new ComponentBuilder()
            .WithButton("Сделать ставку", $"auc:bid_btn:{lot.Id}", ButtonStyle.Primary);

        if (lot.BuyoutPrice.HasValue)
            builder.WithButton($"Выкупить ({lot.BuyoutPrice.Value:N0})", $"auc:buyout_btn:{lot.Id}", ButtonStyle.Success);

        return builder.Build();
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildAuctionPageAsync(int page)
    {
        var total = await auctionRepo.CountActiveAuctionsAsync(Context.Guild.Id);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 0, totalPages - 1);

        var lots = await auctionRepo.GetActiveAuctionsAsync(Context.Guild.Id, PageSize, page * PageSize);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        var builder = embeds.CreateBase($"Аукционный дом и биржа ролей KRLZ (Стр. {page + 1}/{totalPages})");
        if (lots.Count == 0)
        {
            builder.WithDescription("Сейчас нет активных лотов. Выставьте предмет или роль через `/auction create`!");
            return (builder.Build(), new ComponentBuilder().Build());
        }

        var select = new SelectMenuBuilder()
            .WithCustomId("auc:select_lot")
            .WithPlaceholder("Выберите лот для ставки или выкупа...");

        foreach (var lot in lots)
        {
            var priceLine = lot.CurrentBid > 0
                ? $"Ставка: **{lot.CurrentBid:N0} {cfg.CurrencySymbol}** (<@{lot.HighestBidderId}>)"
                : $"Старт: **{lot.StartPrice:N0} {cfg.CurrencySymbol}**";

            var buyoutLine = lot.BuyoutPrice.HasValue ? $" | Выкуп: **{lot.BuyoutPrice.Value:N0} {cfg.CurrencySymbol}**" : string.Empty;
            var details = string.Join(Environment.NewLine,
                $"{priceLine} (шаг: +{lot.MinStep:N0}){buyoutLine}",
                $"Продавец: <@{lot.SellerId}> • До конца: {TimestampTag.FromDateTimeOffset(lot.EndsAt, TimestampTagStyles.Relative)}");

            builder.AddField($"Лот #{lot.Id} • {lot.LotTitle}", details, false);
            select.AddOption($"#{lot.Id} {lot.LotTitle}", lot.Id.ToString(), $"Мин. ставка: {(lot.CurrentBid == 0 ? lot.StartPrice : lot.CurrentBid + lot.MinStep):N0} {cfg.CurrencySymbol}");
        }

        var comp = new ComponentBuilder()
            .WithSelectMenu(select)
            .WithButton("Назад", $"auc:page:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0, row: 1)
            .WithButton("Вперёд", $"auc:page:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages - 1, row: 1)
            .Build();

        return (builder.Build(), comp);
    }
}
