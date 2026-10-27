using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

public sealed class TicketsModule(
    TicketService ticketService,
    TicketRepository ticketRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class TicketCreateModal : IModal
    {
        public string Title => "Создание обращения в поддержку";

        [InputLabel("Краткая суть обращения")]
        [ModalTextInput("subject", TextInputStyle.Paragraph, "Опишите ваш вопрос или проблему...", minLength: 5, maxLength: 400)]
        public string Subject { get; set; } = string.Empty;
    }

    public sealed class TicketCloseModal : IModal
    {
        public string Title => "Закрытие тикета";

        [InputLabel("Причина закрытия / Итог решения")]
        [ModalTextInput("reason", TextInputStyle.Paragraph, "Вопрос решён...", minLength: 3, maxLength: 300)]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class TicketAddUserModal : IModal
    {
        public string Title => "Добавление участника в тикет";

        [InputLabel("Discord ID пользователя")]
        [ModalTextInput("user_id", TextInputStyle.Short, "123456789012345678", minLength: 15, maxLength: 22)]
        public string UserId { get; set; } = string.Empty;
    }

    [SlashCommand("ticket-setup", "Опубликовать интерактивную панель создания тикетов")]
    [RequireContext(ContextType.Guild)]
    [RequireAdmin]
    public async Task TicketSetupCommandAsync([Summary("channel", "Канал для публикации панели")] SocketTextChannel? targetChannel = null)
    {
        var channel = targetChannel ?? (SocketTextChannel)Context.Channel;
        var embed = embeds.CreateBase("Служба поддержки KRLZ")
            .WithDescription(
                "Если у вас возник вопрос, жалоба на участника или вы хотите подать заявку в команду сервера — выберите подходящую категорию в меню ниже или нажмите кнопку быстрого открытия.\n\n" +
                "• **Общий вопрос** — помощь по механикам и правилам сервера\n" +
                "• **Жалоба на участника** — рассмотрение нарушений\n" +
                "• **Заявка в стафф / на роль** — набор в модерацию и ивентеры")
            .Build();

        var select = new SelectMenuBuilder()
            .WithCustomId("ticket:category_select")
            .WithPlaceholder("Выберите категорию обращения...")
            .AddOption("Общий вопрос", "question", "Консультация по работе сервера KRLZ")
            .AddOption("Жалоба на участника", "report", "Сообщить о нарушении правил")
            .AddOption("Заявка в команду", "application", "Анкета на должность или роль");

        var comp = new ComponentBuilder()
            .WithSelectMenu(select)
            .WithButton("Быстрый тикет (Вопрос)", "ticket:quick_open", ButtonStyle.Primary)
            .Build();

        await channel.SendMessageAsync(embed: embed, components: comp);
        await RespondAsync(embed: embeds.Success("Панель опубликована", $"Панель тикетов размещена в канале <#{channel.Id}>."), ephemeral: true);
    }

    [ComponentInteraction("ticket:category_select", true)]
    public async Task HandleCategorySelectAsync(string[] values)
    {
        var category = values.FirstOrDefault() ?? "question";
        await RespondWithModalAsync<TicketCreateModal>($"ticket:create_modal:{category}");
    }

    [ComponentInteraction("ticket:quick_open", true)]
    public async Task HandleQuickOpenButtonAsync()
    {
        await RespondWithModalAsync<TicketCreateModal>("ticket:create_modal:question");
    }

    [ModalInteraction("ticket:create_modal:*", true)]
    public async Task HandleCreateTicketModalAsync(string category, TicketCreateModal modal)
    {
        await DeferAsync(ephemeral: true);
        var member = (SocketGuildUser)Context.User;
        var (ok, msg, _) = await ticketService.CreateTicketAsync(Context.Guild, member, category, modal.Subject.Trim());

        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Обращение создано", msg), ephemeral: true);
    }

    [ComponentInteraction("ticket:claim", true)]
    [RequireModerator]
    public async Task HandleClaimTicketAsync()
    {
        var ticket = await ticketRepo.GetByChannelAsync(Context.Channel.Id);
        if (ticket is null || ticket.Status != "open")
        {
            await RespondAsync(embed: embeds.Error("Тикет не найден или уже закрыт."), ephemeral: true);
            return;
        }

        await ticketRepo.SetClaimedByAsync(ticket.Id, Context.User.Id);
        await RespondAsync(embed: embeds.Success("Тикет взят в работу", $"Модератор <@{Context.User.Id}> назначен ответственным за обращение `#{ticket.TicketNumber:D4}`."));
    }

    [ComponentInteraction("ticket:adduser", true)]
    [RequireModerator]
    public async Task HandleAddUserButtonAsync()
    {
        await RespondWithModalAsync<TicketAddUserModal>("ticket:adduser_modal");
    }

    [ModalInteraction("ticket:adduser_modal", true)]
    [RequireModerator]
    public async Task HandleAddUserModalAsync(TicketAddUserModal modal)
    {
        if (!ulong.TryParse(modal.UserId.Trim(), out var userId))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректный Discord ID пользователя."), ephemeral: true);
            return;
        }

        var targetUser = await Context.Guild.GetGuildUserSafeAsync(userId);
        if (targetUser is null)
        {
            await RespondAsync(embed: embeds.Error("Участник с указанным ID не найден на сервере."), ephemeral: true);
            return;
        }

        if (Context.Channel is SocketTextChannel textChannel)
        {
            await textChannel.AddPermissionOverwriteAsync(targetUser, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow));
        }

        await RespondAsync(embed: embeds.Success("Участник добавлен", $"Пользователь <@{targetUser.Id}> подключён к обсуждению тикета."));
    }

    [ComponentInteraction("ticket:escalate", true)]
    [RequireModerator]
    public async Task HandleEscalateTicketAsync()
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var mention = cfg.AdminRoleId != 0 ? $"<@&{cfg.AdminRoleId}>" : $"<@{Context.Guild.OwnerId}>";

        await RespondAsync(
            text: mention,
            embed: embeds.Warning("Запрос старшей администрации",
                $"Модератор <@{Context.User.Id}> запросил подключение старшего администратора в данный тикет."));
    }

    [ComponentInteraction("ticket:close", true)]
    public async Task HandleCloseButtonAsync()
    {
        var ticket = await ticketRepo.GetByChannelAsync(Context.Channel.Id);
        if (ticket is null)
        {
            await RespondAsync(embed: embeds.Error("Этот канал не является активным тикетом."), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var isStaff = member.Id == Context.Guild.OwnerId ||
                      member.GuildPermissions.Administrator ||
                      (cfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == cfg.AdminRoleId)) ||
                      (cfg.ModeratorRoleId != 0 && member.Roles.Any(r => r.Id == cfg.ModeratorRoleId));

        if (!isStaff && ticket.OwnerId != member.Id)
        {
            await RespondAsync(embed: embeds.Error("Закрыть тикет может только его автор или сотрудник поддержки."), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<TicketCloseModal>("ticket:close_modal");
    }

    [ModalInteraction("ticket:close_modal", true)]
    public async Task HandleCloseModalSubmitAsync(TicketCloseModal modal)
    {
        await DeferAsync(ephemeral: true);
        if (Context.Channel is not SocketTextChannel textChannel)
            return;

        var member = (SocketGuildUser)Context.User;
        await ticketService.CloseTicketAsync(Context.Guild, textChannel, member, modal.Reason.Trim());
    }

    [ComponentInteraction("ticket:rate:*:*", true)]
    public async Task HandleTicketRatingAsync(string ticketIdStr, string ratingStr)
    {
        if (!long.TryParse(ticketIdStr, out var ticketId) || !int.TryParse(ratingStr, out var rating))
            return;

        rating = Math.Clamp(rating, 1, 5);
        var ticket = await ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            await RespondAsync(embed: embeds.Error("Тикет не найден."), ephemeral: true);
            return;
        }

        if (ticket.Rating.HasValue)
        {
            await RespondAsync(embed: embeds.Info("Оценка уже учтена", $"Вы уже поставили оценку **{ticket.Rating.Value}/5** этому обращению."), ephemeral: true);
            return;
        }

        await ticketRepo.SetRatingAsync(ticketId, rating);
        await RespondAsync(embed: embeds.Success("Спасибо за отзыв!", $"Ваша оценка **{rating} ★** для тикета `#{ticket.TicketNumber:D4}` сохранена."), ephemeral: true);
    }
}
