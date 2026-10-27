using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ModerationModule(
    ModerationService modService,
    ModerationRepository modRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class DurationAutocompleteHandler : AutocompleteHandler
    {
        private static readonly (string Name, string Value)[] Presets =
        [
            ("10 минут", "10m"),
            ("30 минут", "30m"),
            ("1 час", "1h"),
            ("2 часа", "2h"),
            ("12 часов", "12h"),
            ("1 день", "1d"),
            ("7 дней", "7d"),
            ("30 дней", "30d")
        ];

        public override Task<AutocompletionResult> GenerateSuggestionsAsync(
            IInteractionContext context,
            IAutocompleteInteraction autocompleteInteraction,
            IParameterInfo parameter,
            IServiceProvider services)
        {
            var current = autocompleteInteraction.Data.Current.Value?.ToString() ?? string.Empty;
            var results = Presets
                .Where(p => p.Name.Contains(current, StringComparison.OrdinalIgnoreCase) ||
                            p.Value.Contains(current, StringComparison.OrdinalIgnoreCase))
                .Select(p => new AutocompleteResult(p.Name, p.Value));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
    }

    public sealed class PunishReasonModal : IModal
    {
        public string Title => "Подтверждение наказания";

        [InputLabel("Причина наказания")]
        [ModalTextInput("reason", TextInputStyle.Paragraph, "Укажите подробную причину...", minLength: 2, maxLength: 300)]
        public string Reason { get; set; } = string.Empty;

        [InputLabel("Срок (например: 10m, 2h, 7d, пусто = навсегда)")]
        [ModalTextInput("duration", TextInputStyle.Short, "1h", minLength: 0, maxLength: 12)]
        public string? Duration { get; set; }
    }

    [SlashCommand("ban", "Заблокировать участника на сервере (только для администраторов)")]
    [RequireAdmin]
    public async Task BanCommandAsync(
        [Summary("user", "Участник для блокировки")] SocketGuildUser target,
        [Summary("reason", "Причина блокировки")] string reason = "Нарушение правил сервера",
        [Summary("duration", "Срок блокировки (10m, 2h, 7d)"), Autocomplete(typeof(DurationAutocompleteHandler))] string? duration = null)
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        TimeSpan? parsedDuration = null;
        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!TimeParser.TryParseDuration(duration, out var d))
            {
                await RespondAsync(embed: embeds.Error("Некорректный формат времени. Примеры: `10m`, `2h`, `7d`."), ephemeral: true);
                return;
            }
            parsedDuration = d;
        }

        await modService.BanAsync(Context.Guild, actor, target.Id, reason, parsedDuration);
        var durationText = parsedDuration.HasValue ? TimeParser.FormatDuration(parsedDuration.Value) : "Навсегда";
        await RespondAsync(embed: embeds.Success("Участник заблокирован",
            $"**Участник:** <@{target.Id}> (`{target.Id}`)\n**Срок:** {durationText}\n**Причина:** {reason}"));
    }

    [SlashCommand("unban", "Разблокировать пользователя по его Discord ID (только для администраторов)")]
    [RequireAdmin]
    public async Task UnbanCommandAsync(
        [Summary("user_id", "ID пользователя Discord")] string userIdRaw,
        [Summary("reason", "Причина разблокировки")] string reason = "Амнистия / Истечение срока")
    {
        if (!ulong.TryParse(userIdRaw.Trim(), out var targetId))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректный числовой ID пользователя."), ephemeral: true);
            return;
        }

        var actor = (SocketGuildUser)Context.User;
        await modService.UnbanAsync(Context.Guild, actor, targetId, reason);
        await RespondAsync(embed: embeds.Success("Пользователь разблокирован",
            $"**Пользователь:** <@{targetId}> (`{targetId}`)\n**Причина:** {reason}"));
    }

    [SlashCommand("kick", "Исключить участника с сервера")]
    [RequireModerator]
    public async Task KickCommandAsync(
        [Summary("user", "Участник для исключения")] SocketGuildUser target,
        [Summary("reason", "Причина исключения")] string reason = "Нарушение правил сервера")
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        await modService.KickAsync(Context.Guild, actor, target, reason);
        await RespondAsync(embed: embeds.Success("Участник исключён",
            $"**Участник:** <@{target.Id}>\n**Причина:** {reason}"));
    }

    [SlashCommand("mute", "Выдать текстовый и голосовой таймаут участнику")]
    [RequireModerator]
    public async Task MuteCommandAsync(
        [Summary("user", "Участник для мута")] SocketGuildUser target,
        [Summary("duration", "Длительность (10m, 2h, 7d)"), Autocomplete(typeof(DurationAutocompleteHandler))] string duration,
        [Summary("reason", "Причина мута")] string reason = "Нарушение правил чата")
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        if (!TimeParser.TryParseDuration(duration, out var span))
        {
            await RespondAsync(embed: embeds.Error("Неверный формат длительности. Используйте `10m`, `2h`, `7d`."), ephemeral: true);
            return;
        }

        await modService.MuteAsync(Context.Guild, actor, target, span, reason);
        await RespondAsync(embed: embeds.Success("Участник получил мут",
            $"**Участник:** <@{target.Id}>\n**Длительность:** {TimeParser.FormatDuration(span)}\n**Причина:** {reason}"));
    }

    [SlashCommand("unmute", "Снять таймаут и роль мута с участника")]
    [RequireModerator]
    public async Task UnmuteCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина снятия мута")] string reason = "Срок наказания пересмотрен")
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        await modService.UnmuteAsync(Context.Guild, actor, target, reason);
        await RespondAsync(embed: embeds.Success("Мут снят",
            $"С участника <@{target.Id}> сняты ограничения чата.\n**Причина:** {reason}"));
    }

    [SlashCommand("warn", "Выдать предупреждение участнику")]
    [RequireModerator]
    public async Task WarnCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина предупреждения")] string reason)
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var (warnId, activeCount, escalation) = await modService.WarnAsync(Context.Guild, actor, target, reason);
        var desc = $"**Участник:** <@{target.Id}>\n**Предупреждение:** `#{warnId}` (активных: **{activeCount}**)\n**Причина:** {reason}";
        if (!string.IsNullOrWhiteSpace(escalation))
            desc += $"\n**Автоэскалация:** {escalation}";

        await RespondAsync(embed: embeds.Warning("Выдано предупреждение", desc));
    }

    [SlashCommand("unwarn", "Снять предупреждение по номеру")]
    [RequireModerator]
    public async Task UnwarnCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("warn_id", "Номер предупреждения")] long warnId,
        [Summary("reason", "Причина снятия")] string reason = "Апелляция одобрена")
    {
        var actor = (SocketGuildUser)Context.User;
        var ok = await modService.UnwarnAsync(Context.Guild, actor, target.Id, warnId, reason);
        if (!ok)
        {
            await RespondAsync(embed: embeds.Error($"Активное предупреждение `#{warnId}` не найдено."), ephemeral: true);
            return;
        }

        await RespondAsync(embed: embeds.Success("Предупреждение снято",
            $"Предупреждение `#{warnId}` для <@{target.Id}> деактивировано.\n**Причина:** {reason}"));
    }

    [SlashCommand("warns", "Посмотреть активные предупреждения участника")]
    [RequireModerator]
    public async Task WarnsCommandAsync([Summary("user", "Участник")] SocketGuildUser? target = null)
    {
        var member = target ?? (SocketGuildUser)Context.User;
        var warns = await modRepo.GetActiveWarnsAsync(Context.Guild.Id, member.Id);

        if (warns.Count == 0)
        {
            await RespondAsync(embed: embeds.Info($"Предупреждения {member.DisplayName}", "Активных предупреждений нет."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var w in warns)
        {
            sb.AppendLine($"**#{w.Id}** • Модератор: <@{w.ModeratorId}> • {TimestampTag.FromDateTimeOffset(w.CreatedAt, TimestampTagStyles.ShortDateTime)}");
            sb.AppendLine($"Причина: `{w.Reason}`\n");
        }

        await RespondAsync(embed: embeds.Info($"Активные предупреждения ({warns.Count}) — {member.DisplayName}", sb.ToString()), ephemeral: true);
    }

    [SlashCommand("clear", "Очистить сообщения в текущем канале")]
    [RequireModerator]
    public async Task ClearCommandAsync(
        [Summary("amount", "Количество сообщений (1-100)")] [MinValue(1), MaxValue(100)] int amount,
        [Summary("user", "Фильтр по автору сообщений")] SocketGuildUser? filterUser = null)
    {
        await DeferAsync(ephemeral: true);
        if (Context.Channel is not SocketTextChannel textChannel)
        {
            await FollowupAsync(embed: embeds.Error("Команда доступна только в текстовых каналах."), ephemeral: true);
            return;
        }

        var fetched = await textChannel.GetMessagesAsync(Math.Min(100, amount * 2)).FlattenAsync();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var toDelete = fetched
            .Where(m => m.CreatedAt > cutoff && (filterUser is null || m.Author.Id == filterUser.Id))
            .Take(amount)
            .ToList();

        if (toDelete.Count == 0)
        {
            await FollowupAsync(embed: embeds.Error("Подходящих сообщений (моложе 14 дней) для удаления не найдено."), ephemeral: true);
            return;
        }

        await textChannel.DeleteMessagesAsync(toDelete);
        await modService.SendModLogAsync(
            Context.Guild,
            "Очистка чата",
            filterUser?.Id ?? 0,
            Context.User.Id,
            $"Удалено {toDelete.Count} сообщ. в канале #{textChannel.Name}",
            null);

        await FollowupAsync(embed: embeds.Success("Очистка завершена",
            $"Удалено сообщений: **{toDelete.Count}**" + (filterUser is not null ? $" (от <@{filterUser.Id}>)" : string.Empty)), ephemeral: true);
    }

    [SlashCommand("slowmode", "Установить медленный режим в текстовом канале")]
    [RequireModerator]
    public async Task SlowmodeCommandAsync(
        [Summary("seconds", "Интервал в секундах (0 для отключения)")] [MinValue(0), MaxValue(21600)] int seconds)
    {
        if (Context.Channel is not SocketTextChannel textChannel)
        {
            await RespondAsync(embed: embeds.Error("Команда работает только в текстовом канале."), ephemeral: true);
            return;
        }

        await textChannel.ModifyAsync(p => p.SlowModeInterval = seconds);
        var msg = seconds == 0
            ? $"Медленный режим в канале <#{textChannel.Id}> отключён."
            : $"Медленный режим в канале <#{textChannel.Id}> установлен на **{seconds} сек.**";
        await RespondAsync(embed: embeds.Success("Настройка канала", msg));
    }

    [SlashCommand("lock", "Закрыть текущий канал для отправки сообщений участниками")]
    [RequireModerator]
    public async Task LockChannelCommandAsync([Summary("reason", "Причина блокировки канала")] string reason = "Канал временно закрыт модератором")
    {
        if (Context.Channel is not SocketTextChannel textChannel)
        {
            await RespondAsync(embed: embeds.Error("Команда доступна только в текстовом канале."), ephemeral: true);
            return;
        }

        var perms = textChannel.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        await textChannel.AddPermissionOverwriteAsync(Context.Guild.EveryoneRole, perms.Modify(sendMessages: PermValue.Deny));
        await RespondAsync(embed: embeds.Warning("Канал закрыт", $"Отправка сообщений в <#{textChannel.Id}> ограничена.\n**Причина:** {reason}"));
    }

    [SlashCommand("unlock", "Открыть текущий канал для отправки сообщений")]
    [RequireModerator]
    public async Task UnlockChannelCommandAsync()
    {
        if (Context.Channel is not SocketTextChannel textChannel)
        {
            await RespondAsync(embed: embeds.Error("Команда доступна только в текстовом канале."), ephemeral: true);
            return;
        }

        var perms = textChannel.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        await textChannel.AddPermissionOverwriteAsync(Context.Guild.EveryoneRole, perms.Modify(sendMessages: PermValue.Inherit));
        await RespondAsync(embed: embeds.Success("Канал открыт", $"Отправка сообщений в <#{textChannel.Id}> снова доступна."));
    }

    [SlashCommand("history", "История всех наказаний конкретного участника")]
    [RequireModerator]
    public async Task HistoryCommandAsync([Summary("user", "Участник")] SocketGuildUser target)
    {
        var items = await modRepo.GetUserHistoryAsync(Context.Guild.Id, target.Id, 15);
        if (items.Count == 0)
        {
            await RespondAsync(embed: embeds.Info($"История {target.DisplayName}", "Нарушений не зафиксировано."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var p in items)
        {
            sb.AppendLine($"**#{p.Id}** [`{p.Type.ToUpperInvariant()}`] • Мод: <@{p.ModeratorId}> • {TimestampTag.FromDateTimeOffset(p.CreatedAt, TimestampTagStyles.ShortDateTime)}");
            sb.AppendLine($"Причина: `{p.Reason}`");
        }

        await RespondAsync(embed: embeds.Info($"История наказаний — {target.DisplayName}", sb.ToString()), ephemeral: true);
    }

    [SlashCommand("modlog", "Последние действия модераторов на сервере")]
    [RequireModerator]
    public async Task ModlogCommandAsync()
    {
        var items = await modRepo.GetRecentModlogAsync(Context.Guild.Id, 15);
        if (items.Count == 0)
        {
            await RespondAsync(embed: embeds.Info("Журнал модерации", "Записей пока нет."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var p in items)
        {
            sb.AppendLine($"**#{p.Id}** [`{p.Type.ToUpperInvariant()}`] Участник: <@{p.UserId}> | Мод: <@{p.ModeratorId}> ({TimestampTag.FromDateTimeOffset(p.CreatedAt, TimestampTagStyles.Relative)})");
            sb.AppendLine($"Причина: `{p.Reason}`");
        }

        await RespondAsync(embed: embeds.Info("Журнал модерации KRLZ", sb.ToString()), ephemeral: true);
    }

    [SlashCommand("punish-panel", "Открыть интерактивную панель быстрых наказаний для участника")]
    [RequireModerator]
    public async Task PunishPanelCommandAsync([Summary("user", "Целевой участник")] SocketGuildUser target)
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var activeWarns = await modRepo.GetActiveWarnCountAsync(Context.Guild.Id, target.Id);
        var embed = embeds.CreateBase($"Панель модерации — {target.DisplayName}")
            .WithThumbnailUrl(target.GetAvatarUrl() ?? target.GetDefaultAvatarUrl())
            .AddField("Участник", $"<@{target.Id}> (`{target.Id}`)", true)
            .AddField("Активных варнов", activeWarns.ToString(), true)
            .AddField("Вход на сервер", target.JoinedAt.HasValue ? TimestampTag.FromDateTimeOffset(target.JoinedAt.Value, TimestampTagStyles.Relative) : "Неизвестно", true)
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Предупреждение", $"modpanel:warn:{target.Id}", ButtonStyle.Primary)
            .WithButton("Мут 1ч", $"modpanel:mute1h:{target.Id}", ButtonStyle.Secondary)
            .WithButton("Кик", $"modpanel:kick:{target.Id}", ButtonStyle.Danger)
            .WithButton("Бан (только админ)", $"modpanel:ban:{target.Id}", ButtonStyle.Danger)
            .WithButton("Снять мут", $"modpanel:unmute:{target.Id}", ButtonStyle.Success)
            .Build();

        await RespondAsync(embed: embed, components: comp, ephemeral: true);
    }

    [ComponentInteraction("modpanel:*:*", true)]
    [RequireModerator]
    public async Task HandlePunishPanelButtonAsync(string action, string targetIdStr)
    {
        if (!ulong.TryParse(targetIdStr, out var targetId))
            return;

        if (action == "unmute")
        {
            var member = await Context.Guild.GetGuildUserSafeAsync(targetId);
            if (member is null)
            {
                await RespondAsync(embed: embeds.Error("Участник не найден на сервере."), ephemeral: true);
                return;
            }

            await modService.UnmuteAsync(Context.Guild, (SocketGuildUser)Context.User, member, "Снято через панель модерации");
            await RespondAsync(embed: embeds.Success("Мут снят", $"С участника <@{targetId}> снят таймаут."), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<PunishReasonModal>($"modmodal:{action}:{targetId}");
    }

    [ModalInteraction("modmodal:*:*", true)]
    [RequireModerator]
    public async Task HandlePunishModalSubmitAsync(string action, string targetIdStr, PunishReasonModal modal)
    {
        if (!ulong.TryParse(targetIdStr, out var targetId))
            return;

        var actor = (SocketGuildUser)Context.User;
        var target = await Context.Guild.GetGuildUserSafeAsync(targetId);

        if (target is not null && !HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(modal.Reason) ? "Нарушение правил сервера" : modal.Reason.Trim();

        switch (action)
        {
            case "warn" when target is not null:
                var (wId, count, esc) = await modService.WarnAsync(Context.Guild, actor, target, reason);
                await RespondAsync(embed: embeds.Success("Варн выдан", $"Выдано предупреждение `#{wId}` (всего: {count}). {esc}"), ephemeral: true);
                break;

            case "mute1h" when target is not null:
                var span = TimeParser.TryParseDuration(modal.Duration, out var customSpan) ? customSpan : TimeSpan.FromHours(1);
                await modService.MuteAsync(Context.Guild, actor, target, span, reason);
                await RespondAsync(embed: embeds.Success("Мут выдан", $"Участник <@{target.Id}> получил мут на {TimeParser.FormatDuration(span)}."), ephemeral: true);
                break;

            case "kick" when target is not null:
                await modService.KickAsync(Context.Guild, actor, target, reason);
                await RespondAsync(embed: embeds.Success("Участник исключён", $"Участник <@{target.Id}> кикнут."), ephemeral: true);
                break;

            case "ban":
                if (actor.Id != Context.Guild.OwnerId && !actor.GuildPermissions.Administrator)
                {
                    await RespondAsync(embed: embeds.Error("Команда бана доступна только администраторам или владельцу сервера."), ephemeral: true);
                    return;
                }
                TimeSpan? banDur = TimeParser.TryParseDuration(modal.Duration, out var parsedBan) ? parsedBan : null;
                await modService.BanAsync(Context.Guild, actor, targetId, reason, banDur);
                await RespondAsync(embed: embeds.Success("Участник забанен", $"Пользователь <@{targetId}> заблокирован."), ephemeral: true);
                break;

            default:
                await RespondAsync(embed: embeds.Error("Участник покинул сервер."), ephemeral: true);
                break;
        }
    }
}
