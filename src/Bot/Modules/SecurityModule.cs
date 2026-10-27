using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class SecurityModule(
    SecurityService securityService,
    BackupService backupService,
    SecurityRepository securityRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class CaptchaVerifyModal : IModal
    {
        public string Title => "Проверка безопасности KRLZ";

        [InputLabel("Введите пятизначный код с кнопки")]
        [ModalTextInput("code", TextInputStyle.Short, "12345", minLength: 5, maxLength: 5)]
        public string Code { get; set; } = string.Empty;
    }

    [Group("security", "Управление комплексом защиты сервера (только владелец сервера)")]
    [RequireGuildOwnerCustom]
    public sealed class SecurityPanelSubgroup(
        SecurityRepository securityRepo,
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("panel", "Открыть интерактивную панель управления защитой сервера")]
        public async Task SecurityPanelCommandAsync()
        {
            await DeferAsync(ephemeral: true);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var whitelist = await securityRepo.GetWhitelistAsync(Context.Guild.Id);
            var recentEvents = await securityRepo.GetRecentEventsAsync(Context.Guild.Id, 5);

            var (embed, comp) = BuildSecurityPanel(cfg, settingsProvider, whitelist.Count, recentEvents.Count, embeds);
            await FollowupAsync(embed: embed, components: comp, ephemeral: true);
        }
    }

    [ComponentInteraction("sec:toggle:*", true)]
    [RequireGuildOwnerCustom]
    public async Task HandleSecurityToggleAsync(string module)
    {
        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var updated = module switch
        {
            "antinuke" => cfg with { AntinukeEnabled = !cfg.AntinukeEnabled, UpdatedAt = DateTimeOffset.UtcNow },
            "antiraid" => cfg with { AntiraidEnabled = !cfg.AntiraidEnabled, UpdatedAt = DateTimeOffset.UtcNow },
            "captcha" => cfg with { CaptchaEnabled = !cfg.CaptchaEnabled, UpdatedAt = DateTimeOffset.UtcNow },
            _ => cfg
        };

        await settingsRepo.UpsertAsync(updated);
        await settingsProvider.UpdateAsync(s =>
        {
            s.Security.AntiNukeEnabled = updated.AntinukeEnabled;
            s.Security.AntiRaidEnabled = updated.AntiraidEnabled;
            s.Security.CaptchaEnabled = updated.CaptchaEnabled;
        });

        var whitelist = await securityRepo.GetWhitelistAsync(Context.Guild.Id);
        var recentEvents = await securityRepo.GetRecentEventsAsync(Context.Guild.Id, 5);
        var (embed, comp) = BuildSecurityPanel(updated, settingsProvider, whitelist.Count, recentEvents.Count, embeds);

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Components = comp;
        });
    }

    [Group("whitelist", "Управление белым списком Anti-Nuke (только владелец сервера)")]
    [RequireGuildOwnerCustom]
    public sealed class WhitelistSubgroup(
        SecurityRepository securityRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("add", "Добавить доверенного пользователя или бота в белый список защиты")]
        public async Task AddWhitelistCommandAsync([Summary("user", "Доверенный участник или бот")] SocketGuildUser target)
        {
            await DeferAsync(ephemeral: true);
            var type = target.IsBot ? "bot" : "user";
            await securityRepo.AddWhitelistAsync(Context.Guild.Id, target.Id, type, Context.User.Id);
            await FollowupAsync(embed: embeds.Success("Белый список обновлён",
                $"Субъект <@{target.Id}> (`{type}`) добавлен в доверенный список Anti-Nuke."), ephemeral: true);
        }

        [SlashCommand("remove", "Удалить пользователя или бота из белого списка защиты")]
        public async Task RemoveWhitelistCommandAsync([Summary("user", "Участник или бот")] SocketGuildUser target)
        {
            await DeferAsync(ephemeral: true);
            var removed = await securityRepo.RemoveWhitelistAsync(Context.Guild.Id, target.Id);
            if (!removed)
            {
                await FollowupAsync(embed: embeds.Error("Указанный субъект не числится в белом списке."), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Субъект удалён из белого списка",
                $"Пользователь <@{target.Id}> больше не имеет иммунитета Anti-Nuke."), ephemeral: true);
        }

        [SlashCommand("list", "Показать всех участников и ботов в белом списке защиты")]
        public async Task ListWhitelistCommandAsync()
        {
            await DeferAsync(ephemeral: true);
            var list = await securityRepo.GetWhitelistAsync(Context.Guild.Id);
            if (list.Count == 0)
            {
                await FollowupAsync(embed: embeds.Info("Белый список Anti-Nuke", "Список пуст. Иммунитетом обладает только владелец сервера."), ephemeral: true);
                return;
            }

            var sb = new StringBuilder();
            foreach (var item in list)
            {
                sb.AppendLine($"• <@{item.TargetId}> (`{item.TargetId}`) — `{item.TargetType}` • Добавил: <@{item.AddedById}>");
            }

            await FollowupAsync(embed: embeds.Info($"Белый список Anti-Nuke ({list.Count})", sb.ToString()), ephemeral: true);
        }
    }

    [Group("backup", "Резервное копирование и восстановление структуры сервера (только владелец)")]
    [RequireGuildOwnerCustom]
    public sealed class BackupSubgroup(
        BackupService backupService,
        SecurityRepository securityRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("create", "Создать снимок всех ролей, категорий и каналов сервера")]
        public async Task CreateBackupCommandAsync([Summary("name", "Метка резервной копии")] string name = "manual")
        {
            await DeferAsync(ephemeral: true);
            var (id, snapshot) = await backupService.CreateBackupAsync(Context.Guild, Context.User.Id, name.Trim());
            await FollowupAsync(embed: embeds.Success("Резервная копия создана",
                $"**ID снимка:** `#{id}` (`{name}`)\n" +
                $"**Сохранено ролей:** {snapshot.Roles.Count}\n" +
                $"**Сохранено каналов:** {snapshot.Channels.Count}"), ephemeral: true);
        }

        [SlashCommand("restore", "Восстановить отсутствующие роли и каналы из резервной копии")]
        public async Task RestoreBackupCommandAsync([Summary("backup_id", "ID копии (пусто = последняя копия)")] long? backupId = null)
        {
            await DeferAsync(ephemeral: true);
            var backup = backupId.HasValue
                ? await securityRepo.GetBackupByIdAsync(Context.Guild.Id, backupId.Value)
                : await securityRepo.GetLatestBackupAsync(Context.Guild.Id);

            if (backup is null)
            {
                await FollowupAsync(embed: embeds.Error("Резервная копия не найдена. Создайте её через `/backup create`."), ephemeral: true);
                return;
            }

            var (rolesCount, channelsCount) = await backupService.RestoreBackupAsync(Context.Guild, backup);
            await FollowupAsync(embed: embeds.Success("Восстановление завершено",
                $"Использован снимок `#{backup.Id}` (`{backup.SnapshotName}`).\n" +
                $"**Воссоздано ролей:** {rolesCount}\n" +
                $"**Воссоздано каналов и категорий:** {channelsCount}"), ephemeral: true);
        }
    }

    [SlashCommand("lockdown", "Включить или отключить глобальную блокировку чатов сервера")]
    [RequireGuildOwnerCustom]
    public async Task LockdownCommandAsync(
        [Summary("state", "Состояние режима блокировки"), Choice("Включить (ON)", "on"), Choice("Выключить (OFF)", "off")] string state,
        [Summary("reason", "Причина")] string reason = "Решение владельца сервера")
    {
        await DeferAsync();
        var active = state == "on";
        await securityService.SetLockdownAsync(Context.Guild, active, reason);

        var embed = active
            ? embeds.Warning("Сервер переведён в режим Lockdown", $"Отправка сообщений во всех каналах временно ограничена.\n**Причина:** {reason}")
            : embeds.Success("Режим Lockdown снят", "Штатная работа текстовых каналов восстановлена.");

        await FollowupAsync(embed: embed);
    }

    [SlashCommand("panic", "ЭКСТРЕННАЯ КНОПКА: полная блокировка сервера и снятие опасных прав со всех ролей")]
    [RequireGuildOwnerCustom]
    public async Task PanicCommandAsync()
    {
        await DeferAsync(ephemeral: true);
        await securityService.ExecutePanicModeAsync(Context.Guild, Context.User.Id);
        await FollowupAsync(embed: embeds.Error(
            "Активирован протокол PANIC:\n" +
            "1. Все текстовые каналы закрыты на запись (Lockdown).\n" +
            "2. Со всех ролей ниже бота сняты права Administrator, ManageGuild, ManageRoles, ManageChannels, Ban, Kick.\n" +
            "3. Событие зафиксировано в журнале безопасности.",
            "ПРОТОКОЛ PANIC АКТИВИРОВАН"), ephemeral: true);
    }

    [ComponentInteraction("security:verify_start", true)]
    public async Task HandleVerifyStartButtonAsync()
    {
        var code = securityService.GenerateCaptchaForUser(Context.Guild.Id, Context.User.Id);
        var comp = new ComponentBuilder()
            .WithButton($"Ввести код: {code}", "security:verify_open_modal", ButtonStyle.Primary)
            .Build();

        await RespondAsync(
            embed: embeds.Info("Капча-верификация KRLZ",
                $"Ваш проверочный код: **`{code}`**\nНажмите на кнопку ниже и введите этот код для получения доступа к серверу."),
            components: comp,
            ephemeral: true);
    }

    [ComponentInteraction("security:verify_open_modal", true)]
    public async Task HandleVerifyOpenModalAsync()
    {
        await RespondWithModalAsync<CaptchaVerifyModal>("security:verify_modal");
    }

    [ModalInteraction("security:verify_modal", true)]
    public async Task HandleVerifyModalSubmitAsync(CaptchaVerifyModal modal)
    {
        await DeferAsync(ephemeral: true);
        if (!securityService.ValidateCaptcha(Context.Guild.Id, Context.User.Id, modal.Code))
        {
            await FollowupAsync(embed: embeds.Error("Неверный проверочный код. Нажмите кнопку верификации ещё раз."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var member = (SocketGuildUser)Context.User;

        if (cfg.QuarantineRoleId != 0 && Context.Guild.GetRole(cfg.QuarantineRoleId) is { } qRole && member.Roles.Any(r => r.Id == qRole.Id))
        {
            await member.RemoveRoleAsync(qRole);
        }

        if (cfg.VerifiedRoleId != 0 && Context.Guild.GetRole(cfg.VerifiedRoleId) is { } vRole)
        {
            await member.AddRoleAsync(vRole);
        }

        await FollowupAsync(embed: embeds.Success("Верификация пройдена!", "Добро пожаловать на сервер KRLZ! Доступ к каналам открыт."), ephemeral: true);
    }

    private static (Embed Embed, MessageComponent Components) BuildSecurityPanel(
        Bot.Models.GuildSettingsEntity cfg,
        SettingsProvider settingsProvider,
        int whitelistCount,
        int recentEventsCount,
        EmbedFactory embeds)
    {
        var sec = settingsProvider.Current.Security;
        var embed = embeds.CreateBase("Панель управления защитой KRLZ")
            .AddField("Anti-Nuke", cfg.AntinukeEnabled ? "Активен" : "Отключён", true)
            .AddField("Anti-Raid", cfg.AntiraidEnabled ? "Активен" : "Отключён", true)
            .AddField("Капча при входе", cfg.CaptchaEnabled ? "Активна" : "Отключена", true)
            .AddField("Режим Lockdown", cfg.LockdownActive ? "АКТИВЕН" : "Штатный режим", true)
            .AddField("Доверенных в Whitelist", whitelistCount.ToString(), true)
            .AddField("Недавних инцидентов", recentEventsCount.ToString(), true)
            .AddField("Лимиты Anti-Nuke (в минуту)",
                $"Каналы: `{sec.MaxChannelOpsPerMinute}` | Роли: `{sec.MaxRoleOpsPerMinute}` | Баны/Кики: `{sec.MaxBanKickOpsPerMinute}` | Вебхуки: `{sec.MaxWebhookOpsPerMinute}`", false)
            .AddField("Параметры Anti-Raid",
                $"Порог рейда: `{sec.RaidJoinThreshold}` входов за `{sec.RaidWindowSeconds}` сек. | Мин. возраст аккаунта: `{sec.MinAccountAgeDays}` дн.", false)
            .Build();

        var comp = new ComponentBuilder()
            .WithButton($"Anti-Nuke: {(cfg.AntinukeEnabled ? "ВКЛ" : "ВЫКЛ")}", "sec:toggle:antinuke", cfg.AntinukeEnabled ? ButtonStyle.Success : ButtonStyle.Secondary)
            .WithButton($"Anti-Raid: {(cfg.AntiraidEnabled ? "ВКЛ" : "ВЫКЛ")}", "sec:toggle:antiraid", cfg.AntiraidEnabled ? ButtonStyle.Success : ButtonStyle.Secondary)
            .WithButton($"Капча: {(cfg.CaptchaEnabled ? "ВКЛ" : "ВЫКЛ")}", "sec:toggle:captcha", cfg.CaptchaEnabled ? ButtonStyle.Success : ButtonStyle.Secondary)
            .Build();

        return (embed, comp);
    }
}
