using System.Text;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class VerificationAndAutomodModule(
    VerificationAndAutomodService verService,
    VerificationAndLogsRepository logsRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class VisualCaptchaInputModal : IModal
    {
        public string Title => "Графическая капча KRLZ";

        [InputLabel("Введите 6 символов с картинки")]
        [ModalTextInput("code", TextInputStyle.Short, "AB3K9M", minLength: 4, maxLength: 8)]
        public string Code { get; set; } = string.Empty;
    }

    public sealed class VerificationApplicationModal : IModal
    {
        public string Title => "Анкета вступления на сервер KRLZ";

        [InputLabel("Откуда вы узнали о сервере KRLZ?")]
        [ModalTextInput("source", TextInputStyle.Short, "От друзей / из поиска...", minLength: 2, maxLength: 150)]
        public string Source { get; set; } = string.Empty;

        [InputLabel("Чем планируете заниматься на сервере?")]
        [ModalTextInput("goals", TextInputStyle.Paragraph, "Общение, игры, участие в кланах...", minLength: 5, maxLength: 400)]
        public string Goals { get; set; } = string.Empty;
    }

    public sealed class ApplicationRejectModal : IModal
    {
        public string Title => "Отклонение заявки на верификацию";

        [InputLabel("Причина отклонения (будет отправлена в ЛС)")]
        [ModalTextInput("reason", TextInputStyle.Paragraph, "Недостаточно информации в анкете...", minLength: 3, maxLength: 300)]
        public string Reason { get; set; } = string.Empty;
    }

    [SlashCommand("verify-panel", "Опубликовать панель верификации (графическая капча + анкета)")]
    [RequireAdmin]
    public async Task VerifyPanelCommandAsync([Summary("channel", "Канал для публикации")] SocketTextChannel? targetChannel = null)
    {
        await DeferAsync(ephemeral: true);
        var channel = targetChannel ?? (SocketTextChannel)Context.Channel;
        var mode = settingsProvider.Current.Verification.Mode;

        var desc = string.Join(Environment.NewLine,
            "Добро пожаловать на сервер **KRLZ**!",
            "Для получения роли верифицированного участника и открытия всех каналов пройдите проверку ниже:",
            string.Empty,
            "• **Графическая капча** — мгновенный допуск после ввода защитного кода с картинки",
            "• **Анкета участника** — короткая заявка, рассматриваемая модераторами сервера",
            string.Empty,
            "📜 *Проходя верификацию, вы безоговорочно подтверждаете согласие с правилами сообщества KRLZ.*",
            $"*(Текущий приоритетный режим: `{mode}`)*");

        var comp = new ComponentBuilder()
            .WithButton("Пройти картинку-капчу", "ver:start_captcha", ButtonStyle.Success)
            .WithButton("Заполнить анкету", "ver:start_app", ButtonStyle.Primary)
            .Build();

        await channel.SendMessageAsync(embed: embeds.Info("Верификация участников KRLZ", desc), components: comp);
        await FollowupAsync(embed: embeds.Success("Панель опубликована", $"Панель верификации отправлена в <#{channel.Id}>."), ephemeral: true);
    }

    [ComponentInteraction("ver:start_captcha", true)]
    public async Task HandleStartVisualCaptchaAsync()
    {
        await DeferAsync(ephemeral: true);

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var member = (SocketGuildUser)Context.User;
        if (cfg.VerifiedRoleId != 0 && member.Roles.Any(r => r.Id == cfg.VerifiedRoleId))
        {
            await FollowupAsync(embed: embeds.Info("Верификация пройдена", "Вы уже являетесь верифицированным участником сервера KRLZ!"), ephemeral: true);
            return;
        }

        if (!verService.CheckAndApplyCaptchaCooldown(member.Id, out var waitSec))
        {
            await FollowupAsync(
                embed: embeds.Warning("Слишком частые запросы", $"⏳ Пожалуйста, подождите **{waitSec} сек.** перед повторным запросом капчи."),
                ephemeral: true);
            return;
        }

        var (_, pngBytes) = await verService.GenerateVisualCaptchaAsync(Context.Guild.Id, Context.User.Id);

        var isMsgContentEnabled = settingsProvider.Current.EnableMessageContentIntent;
        var hint = isMsgContentEnabled
            ? "Внимательно прочитайте 6 символов на изображении ниже и **отправьте код сообщением прямо в этот чат**.\n\n🔒 *Ваше сообщение будет мгновенно проверено и сразу удалено, чтобы не засорять канал.*"
            : $"Внимательно прочитайте 6 символов на изображении ниже и отправьте код в этот чат с упоминанием бота (`@{Context.Client.CurrentUser.Username} КОД`), либо нажмите кнопку **«Ввести через окно»** ниже.\n\n🔒 *Сообщение сразу проверяется и удаляется.*";

        var embed = embeds.CreateBase("Графическая проверка безопасности")
            .WithDescription(hint)
            .WithImageUrl("attachment://captcha.png")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Обновить картинку", "ver:start_captcha", ButtonStyle.Secondary)
            .WithButton("Ввести через окно", "ver:open_captcha_modal", ButtonStyle.Secondary)
            .Build();

        await using var stream = new MemoryStream(pngBytes);
        await FollowupWithFileAsync(stream, "captcha.png", embed: embed, components: comp, ephemeral: true);
    }

    [ComponentInteraction("ver:open_captcha_modal", true)]
    public async Task HandleOpenCaptchaModalAsync()
    {
        await RespondWithModalAsync<VisualCaptchaInputModal>("ver:submit_captcha");
    }

    [ModalInteraction("ver:submit_captcha", true)]
    public async Task HandleSubmitCaptchaModalAsync(VisualCaptchaInputModal modal)
    {
        await DeferAsync(ephemeral: true);
        var member = (SocketGuildUser)Context.User;
        var (passed, msg) = await verService.VerifyCaptchaInputAsync(Context.Guild, member, modal.Code);

        if (!passed)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Верификация пройдена!", msg), ephemeral: true);
    }

    [ComponentInteraction("ver:start_app", true)]
    public async Task HandleStartApplicationAsync()
    {
        var member = (SocketGuildUser)Context.User;
        var verRoleId = settingsProvider.Current.Roles.VerifiedRoleId;
        if (verRoleId != 0 && member.Roles.Any(r => r.Id == verRoleId))
        {
            await RespondAsync(embed: embeds.Info("Верификация пройдена", "Вы уже являетесь верифицированным участником сервера KRLZ!"), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<VerificationApplicationModal>("ver:submit_app");
    }

    [ModalInteraction("ver:submit_app", true)]
    public async Task HandleSubmitApplicationModalAsync(VerificationApplicationModal modal)
    {
        await DeferAsync(ephemeral: true);
        var pending = await logsRepo.GetPendingApplicationByUserIdAsync(Context.Guild.Id, Context.User.Id);
        if (pending is not null)
        {
            await FollowupAsync(embed: embeds.Warning("Заявка на рассмотрении", $"Ваша анкета `#{pending.Id}` уже ожидает проверки модерацией."), ephemeral: true);
            return;
        }

        var payload = JsonSerializer.Serialize(new { modal.Source, modal.Goals });
        var appId = await logsRepo.CreateApplicationAsync(Context.Guild.Id, Context.User.Id, payload);

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var reviewChId = settingsProvider.Current.Verification.ApplicationReviewChannelId;
        SocketTextChannel? reviewChannel = null;

        if (reviewChId != 0 && reviewChId != Context.Channel.Id)
            reviewChannel = Context.Guild.GetTextChannel(reviewChId);

        if (reviewChannel is null && cfg.ModLogChannelId != 0 && cfg.ModLogChannelId != Context.Channel.Id)
            reviewChannel = Context.Guild.GetTextChannel(cfg.ModLogChannelId);

        if (reviewChannel is not null)
        {
            var reviewEmbed = embeds.CreateBase($"Анкета на верификацию #{appId}")
                .WithThumbnailUrl(Context.User.GetAvatarUrl() ?? Context.User.GetDefaultAvatarUrl())
                .AddField("Участник", $"<@{Context.User.Id}> (`{Context.User.Id}`)", true)
                .AddField("Дата регистрации", TimestampTag.FromDateTimeOffset(Context.User.CreatedAt, TimestampTagStyles.Relative), true)
                .AddField("Откуда узнал о сервере", modal.Source.Trim(), false)
                .AddField("Цели на сервере", modal.Goals.Trim(), false)
                .Build();

            var comp = new ComponentBuilder()
                .WithButton("Одобрить заявку", $"ver:app_approve:{appId}", ButtonStyle.Success)
                .WithButton("Отклонить", $"ver:app_reject:{appId}", ButtonStyle.Danger)
                .Build();

            await reviewChannel.SendMessageAsync(embed: reviewEmbed, components: comp);
        }

        await FollowupAsync(embed: embeds.Success("Анкета отправлена!",
            $"Ваша заявка `#{appId}` передана модераторам сервера KRLZ в закрытый канал рассмотрения. Уведомление о решении придёт вам в личные сообщения."), ephemeral: true);
    }

    [ComponentInteraction("ver:app_approve:*", true)]
    [RequireModerator]
    public async Task HandleApproveAppButtonAsync(string appIdStr)
    {
        if (!long.TryParse(appIdStr, out var appId))
            return;

        await DeferAsync();
        var app = await logsRepo.GetApplicationByIdAsync(appId);
        if (app is null || app.Status != "pending")
        {
            await FollowupAsync(embed: embeds.Error("Заявка не найдена или уже рассмотрена."), ephemeral: true);
            return;
        }

        await logsRepo.ReviewApplicationAsync(appId, Context.User.Id, "approved", "Одобрено модератором");
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var member = await Context.Guild.GetGuildUserSafeAsync(app.UserId);

        if (member is not null)
        {
            if (cfg.QuarantineRoleId != 0 && Context.Guild.GetRole(cfg.QuarantineRoleId) is { } qRole && member.RoleIds.Contains(qRole.Id))
                await member.RemoveRoleAsync(qRole.Id);

            if (cfg.VerifiedRoleId != 0 && Context.Guild.GetRole(cfg.VerifiedRoleId) is { } vRole && Context.Guild.CurrentUser.Hierarchy > vRole.Position)
                await member.AddRoleAsync(vRole.Id);

            try
            {
                await member.SendMessageAsync(embed: embeds.Success("Ваша анкета одобрена!",
                    $"Модератор сервера **{Context.Guild.Name}** одобрил вашу заявку `#{appId}`. Добро пожаловать!"));
            }
            catch
            {
            }
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success($"Анкета #{appId} одобрена", $"Участник <@{app.UserId}> верифицирован модератором <@{Context.User.Id}>.");
            m.Components = null;
        });
    }

    [ComponentInteraction("ver:app_reject:*", true)]
    [RequireModerator]
    public async Task HandleRejectAppButtonAsync(string appIdStr)
    {
        if (!long.TryParse(appIdStr, out var appId))
            return;

        var msgId = ((SocketMessageComponent)Context.Interaction).Message.Id;
        await RespondWithModalAsync<ApplicationRejectModal>($"ver:app_reject_modal:{appId}:{msgId}");
    }

    [ModalInteraction("ver:app_reject_modal:*:*", true)]
    [RequireModerator]
    public async Task HandleRejectAppModalSubmitAsync(string appIdStr, string msgIdStr, ApplicationRejectModal modal)
    {
        if (!long.TryParse(appIdStr, out var appId))
            return;

        await DeferAsync(ephemeral: true);
        var app = await logsRepo.GetApplicationByIdAsync(appId);
        if (app is null || app.Status != "pending")
        {
            await FollowupAsync(embed: embeds.Error("Заявка уже рассмотрена."), ephemeral: true);
            return;
        }

        var reason = modal.Reason.Trim();
        await logsRepo.ReviewApplicationAsync(appId, Context.User.Id, "rejected", reason);

        var member = await Context.Guild.GetGuildUserSafeAsync(app.UserId);
        if (member is not null)
        {
            try
            {
                await member.SendMessageAsync(embed: embeds.Error(
                    $"Ваша заявка `#{appId}` на сервере **{Context.Guild.Name}** была отклонена.\n**Причина:** {reason}",
                    "Анкета отклонена"));
            }
            catch
            {
            }
        }

        if (ulong.TryParse(msgIdStr, out var msgId) && await Context.Channel.GetMessageAsync(msgId) is IUserMessage reviewMsg)
        {
            await reviewMsg.ModifyAsync(m =>
            {
                m.Embed = embeds.Warning($"Анкета #{appId} отклонена",
                    $"Участник: <@{app.UserId}>\nМодератор: <@{Context.User.Id}>\nПричина: {reason}");
                m.Components = null;
            });
        }

        await FollowupAsync(embed: embeds.Success("Решение сохранено", $"Анкета `#{appId}` отклонена модератором."), ephemeral: true);
    }

    [Group("automod", "Настройка фильтра слов, регулярных выражений и порогов токсичности")]
    [RequireAdmin]
    public sealed class AutomodSubgroup(
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("config", "Настроить пороги токсичности и длительность мута автомодерации")]
        public async Task AutomodConfigCommandAsync(
            [Summary("enabled", "Включить автомодерацию")] bool enabled,
            [Summary("warn_threshold", "Порог индекса токсичности для варна (10-200)")] [MinValue(10), MaxValue(200)] int warnThreshold = 50,
            [Summary("mute_threshold", "Порог индекса токсичности для мута (20-300)")] [MinValue(20), MaxValue(300)] int muteThreshold = 85,
            [Summary("mute_minutes", "Длительность авто-мута в минутах")] [MinValue(5), MaxValue(1440)] int muteMinutes = 30)
        {
            await DeferAsync(ephemeral: true);
            await settingsProvider.UpdateAsync(s =>
            {
                s.Automod.Enabled = enabled;
                s.Automod.ToxicityWarnThreshold = warnThreshold;
                s.Automod.ToxicityMuteThreshold = muteThreshold;
                s.Automod.MuteDurationMinutes = muteMinutes;
            });

            await FollowupAsync(embed: embeds.Success("Автомодерация настроена",
                $"**Статус:** {(enabled ? "Включена" : "Отключена")}\n" +
                $"**Порог варна:** {warnThreshold} ед. | **Порог мута:** {muteThreshold} ед.\n" +
                $"**Длительность мута:** {muteMinutes} мин."), ephemeral: true);
        }

        [Group("words", "Управление списком запрещённых слов и корней")]
        public sealed class AutomodWordsSubgroup(
            SettingsProvider settingsProvider,
            EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
        {
            [SlashCommand("add", "Добавить запрещённое слово или шаблон в фильтр автомодерации")]
            public async Task AddWordCommandAsync([Summary("word", "Запрещённое слово")] string word)
            {
                await DeferAsync(ephemeral: true);
                var clean = word.Trim().ToLowerInvariant();
                await settingsProvider.UpdateAsync(s =>
                {
                    if (!s.Automod.BlockedWords.Contains(clean, StringComparer.OrdinalIgnoreCase))
                        s.Automod.BlockedWords.Add(clean);
                });

                await FollowupAsync(embed: embeds.Success("Слово добавлено в фильтр", $"Слово `{clean}` добавлено в чёрный список автомодерации."), ephemeral: true);
            }

            [SlashCommand("remove", "Удалить слово из фильтра автомодерации")]
            public async Task RemoveWordCommandAsync([Summary("word", "Слово для удаления")] string word)
            {
                await DeferAsync(ephemeral: true);
                var clean = word.Trim();
                var removed = false;
                await settingsProvider.UpdateAsync(s =>
                {
                    removed = s.Automod.BlockedWords.RemoveAll(w => w.Equals(clean, StringComparison.OrdinalIgnoreCase)) > 0;
                });

                if (!removed)
                {
                    await FollowupAsync(embed: embeds.Error($"Слово `{clean}` не найдено в списке."), ephemeral: true);
                    return;
                }

                await FollowupAsync(embed: embeds.Success("Слово удалено", $"Слово `{clean}` исключено из фильтра."), ephemeral: true);
            }

            [SlashCommand("list", "Показать текущий список запрещённых слов и словаря токсичности")]
            public async Task ListWordsCommandAsync()
            {
                var am = settingsProvider.Current.Automod;
                var blocked = am.BlockedWords.Count > 0 ? string.Join(", ", am.BlockedWords.Select(w => $"`{w}`")) : "Список пуст";
                var lexicon = string.Join(", ", am.ToxicityLexicon.Select(kv => $"`{kv.Key}` ({kv.Value})"));

                var desc = string.Join(Environment.NewLine,
                    $"**Запрещённые слова ({am.BlockedWords.Count}):**",
                    blocked,
                    string.Empty,
                    "**Весовой словарь токсичности:**",
                    lexicon);

                await RespondAsync(embed: embeds.Info("Фильтры автомодерации KRLZ", desc), ephemeral: true);
            }
        }
    }

    [SlashCommand("namehistory", "Посмотреть историю смены никнеймов участника на сервере")]
    public async Task NameHistoryCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
    {
        await DeferAsync();
        var member = target ?? (SocketGuildUser)Context.User;
        var history = await logsRepo.GetNicknameHistoryAsync(Context.Guild.Id, member.Id, 15);

        if (history.Count == 0)
        {
            await FollowupAsync(embed: embeds.Info($"История никнеймов — {member.DisplayName}", "Записей о смене никнейма пока нет."));
            return;
        }

        var sb = new StringBuilder();
        foreach (var h in history)
        {
            sb.AppendLine($"• {TimestampTag.FromDateTimeOffset(h.ChangedAt, TimestampTagStyles.ShortDateTime)}: `{h.OldNickname ?? "-"}` ➔ **`{h.NewNickname ?? "-"}`**");
        }

        await FollowupAsync(embed: embeds.Info($"История никнеймов — {member.DisplayName}", sb.ToString()));
    }
}
