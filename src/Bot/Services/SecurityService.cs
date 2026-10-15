using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class SecurityService(
    SecurityRepository securityRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<SecurityService> logger)
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId, string Category), List<DateTimeOffset>> _actionTracker = new();
    private readonly ConcurrentDictionary<ulong, List<DateTimeOffset>> _joinTracker = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), string> _captchaCodes = new();

    public async Task<bool> IsTrustedActorAsync(SocketGuild guild, ulong userId, CancellationToken ct = default)
    {
        if (userId == guild.OwnerId || userId == guild.CurrentUser.Id)
            return true;

        return await securityRepo.IsWhitelistedAsync(guild.Id, userId, ct);
    }

    public async Task HandleAuditLogEntryAsync(SocketGuild guild, SocketAuditLogEntry entry, CancellationToken ct = default)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if (!cfg.AntinukeEnabled)
            return;

        var actorId = entry.User?.Id ?? 0;
        if (actorId == 0 || await IsTrustedActorAsync(guild, actorId, ct))
            return;

        var secCfg = settingsProvider.Current.Security;

        switch (entry.Action)
        {
            case ActionType.ChannelCreated or ActionType.ChannelDeleted:
                await CheckThresholdAndPunishAsync(guild, actorId, "channel_ops", secCfg.MaxChannelOpsPerMinute,
                    $"Массовые операции с каналами ({entry.Action})", ct);
                break;

            case ActionType.RoleCreated or ActionType.RoleDeleted:
                await CheckThresholdAndPunishAsync(guild, actorId, "role_ops", secCfg.MaxRoleOpsPerMinute,
                    $"Массовые операции с ролями ({entry.Action})", ct);
                break;

            case ActionType.Ban or ActionType.Kick:
                await CheckThresholdAndPunishAsync(guild, actorId, "bankick_ops", secCfg.MaxBanKickOpsPerMinute,
                    $"Массовые баны или кики ({entry.Action})", ct);
                break;

            case ActionType.WebhookCreated or ActionType.WebhookDeleted:
                await CheckThresholdAndPunishAsync(guild, actorId, "webhook_ops", secCfg.MaxWebhookOpsPerMinute,
                    $"Подозрительные операции с вебхуками ({entry.Action})", ct);
                break;

            case ActionType.EmojiCreated or ActionType.EmojiDeleted:
                await CheckThresholdAndPunishAsync(guild, actorId, "emoji_ops", secCfg.MaxRoleOpsPerMinute,
                    $"Массовое изменение эмодзи ({entry.Action})", ct);
                break;

            case ActionType.BotAdded:
                await NeutralizeOffenderAsync(guild, actorId, "Добавление бота вне белого списка",
                    "Немедленный бан нарушителя и добавленного бота", ct);
                if (entry.Data is SocketBotAddAuditLogData botData && botData.Target.Id != 0)
                {
                    try { await guild.AddBanAsync(botData.Target.Id, 0, "Anti-Nuke: несанкционированный бот"); } catch { }
                }
                break;

            case ActionType.GuildUpdated:
                await NeutralizeOffenderAsync(guild, actorId, "Несанкционированное изменение настроек гильдии",
                    "Снятие всех ролей и бан", ct);
                break;
        }
    }

    public async Task InspectRoleUpdatedAsync(SocketRole before, SocketRole after, CancellationToken ct = default)
    {
        var guild = after.Guild;
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if (!cfg.AntinukeEnabled)
            return;

        var dangerousAdded =
            (!before.Permissions.Administrator && after.Permissions.Administrator) ||
            (!before.Permissions.ManageGuild && after.Permissions.ManageGuild) ||
            (!before.Permissions.ManageRoles && after.Permissions.ManageRoles) ||
            (!before.Permissions.BanMembers && after.Permissions.BanMembers) ||
            (!before.Permissions.ManageWebhooks && after.Permissions.ManageWebhooks);

        if (!dangerousAdded)
            return;

        try
        {
            var logs = await guild.GetAuditLogsAsync(1, actionType: ActionType.RoleUpdated).FlattenAsync();
            var latest = logs.FirstOrDefault();
            var actorId = latest?.User?.Id ?? 0;

            if (actorId != 0 && await IsTrustedActorAsync(guild, actorId, ct))
                return;

            await after.ModifyAsync(p => p.Permissions = before.Permissions);

            if (actorId != 0)
            {
                await NeutralizeOffenderAsync(guild, actorId,
                    $"Попытка выдачи опасных прав роли {after.Name}",
                    "Автоматический откат прав роли, снятие ролей и бан нарушителя", ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка отката опасных прав роли {RoleId}", after.Id);
        }
    }

    public async Task HandleMemberJoinedAsync(SocketGuildUser user, CancellationToken ct = default)
    {
        var guild = user.Guild;
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        var secCfg = settingsProvider.Current.Security;
        var now = DateTimeOffset.UtcNow;

        if (cfg.AntiraidEnabled)
        {
            var list = _joinTracker.GetOrAdd(guild.Id, _ => []);
            int recentJoins;
            lock (list)
            {
                list.RemoveAll(t => (now - t).TotalSeconds > secCfg.RaidWindowSeconds);
                list.Add(now);
                recentJoins = list.Count;
            }

            if (recentJoins >= secCfg.RaidJoinThreshold && !cfg.LockdownActive)
            {
                await SetLockdownAsync(guild, true, "Авто-защита Anti-Raid: зафиксирован массовый вход участников", ct);
            }

            var accountAgeDays = (now - user.CreatedAt).TotalDays;
            if (accountAgeDays < secCfg.MinAccountAgeDays)
            {
                await SendSecurityLogAsync(guild, user.Id, "Фильтр возраста аккаунта",
                    $"Возраст аккаунта {accountAgeDays:F1} дн. (минимум {secCfg.MinAccountAgeDays} дн.)", "Кик при входе", ct);
                try { await user.KickAsync("Anti-Raid: слишком новый аккаунт"); } catch { }
                return;
            }

            if (secCfg.BlockDefaultAvatars && string.IsNullOrWhiteSpace(user.AvatarId))
            {
                await SendSecurityLogAsync(guild, user.Id, "Фильтр стандартного аватара",
                    "Участник без установленного аватара", "Кик при входе", ct);
                try { await user.KickAsync("Anti-Raid: отсутствует аватар профиля"); } catch { }
                return;
            }
        }

        if (cfg.CaptchaEnabled && cfg.QuarantineRoleId != 0 && guild.GetRole(cfg.QuarantineRoleId) is { } quarantineRole)
        {
            try
            {
                await user.AddRoleAsync(quarantineRole);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось выдать карантинную роль участнику {UserId}", user.Id);
            }
        }

        if (cfg.WelcomeChannelId != 0 && guild.GetTextChannel(cfg.WelcomeChannelId) is { } welcomeChannel)
        {
            var embed = embeds.CreateBase($"Добро пожаловать на {guild.Name}!")
                .WithThumbnailUrl(user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl())
                .WithDescription(cfg.CaptchaEnabled
                    ? $"Приветствуем, <@{user.Id}>! Для получения доступа к каналам нажмите кнопку **Пройти верификацию** ниже."
                    : $"Приветствуем, <@{user.Id}>! Рады видеть вас в нашем сообществе.")
                .AddField("Участник по счёту", guild.MemberCount.ToString(), true)
                .AddField("Дата регистрации", TimestampTag.FromDateTimeOffset(user.CreatedAt, TimestampTagStyles.Relative), true)
                .Build();

            ComponentBuilder? comp = null;
            if (cfg.CaptchaEnabled)
            {
                comp = new ComponentBuilder()
                    .WithButton("Пройти верификацию", "ver:start_captcha", ButtonStyle.Success);
            }

            await welcomeChannel.SendMessageAsync(embed: embed, components: comp?.Build());
        }
    }

    public string GenerateCaptchaForUser(ulong guildId, ulong userId)
    {
        var code = Random.Shared.Next(10000, 99999).ToString();
        _captchaCodes[(guildId, userId)] = code;
        return code;
    }

    public bool ValidateCaptcha(ulong guildId, ulong userId, string input)
    {
        if (!_captchaCodes.TryGetValue((guildId, userId), out var expected))
            return false;

        if (!string.Equals(expected, input?.Trim(), StringComparison.Ordinal))
            return false;

        _captchaCodes.TryRemove((guildId, userId), out _);
        return true;
    }

    public async Task SetLockdownAsync(SocketGuild guild, bool active, string reason, CancellationToken ct = default)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        await settingsRepo.UpsertAsync(cfg with { LockdownActive = active, UpdatedAt = DateTimeOffset.UtcNow }, ct);

        foreach (var textChannel in guild.TextChannels)
        {
            try
            {
                var current = textChannel.GetPermissionOverwrite(guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
                var updated = current.Modify(sendMessages: active ? PermValue.Deny : PermValue.Inherit);
                await textChannel.AddPermissionOverwriteAsync(guild.EveryoneRole, updated);
            }
            catch
            {
            }
        }

        await SendSecurityLogAsync(guild, guild.CurrentUser.Id,
            active ? "Активирован режим Lockdown" : "Режим Lockdown отключён",
            reason,
            active ? "Ограничена отправка сообщений для всех участников" : "Права каналов восстановлены",
            ct);
    }

    public async Task ExecutePanicModeAsync(SocketGuild guild, ulong triggeredById, CancellationToken ct = default)
    {
        await SetLockdownAsync(guild, true, "Экстренный режим PANIC активирован владельцем", ct);

        foreach (var role in guild.Roles.Where(r => !r.IsEveryone && !r.IsManaged && r.Position < guild.CurrentUser.Hierarchy))
        {
            if (role.Permissions.Administrator || role.Permissions.ManageGuild || role.Permissions.ManageRoles ||
                role.Permissions.ManageChannels || role.Permissions.BanMembers || role.Permissions.KickMembers)
            {
                try
                {
                    var safePerms = role.Permissions.Modify(
                        administrator: false,
                        manageGuild: false,
                        manageRoles: false,
                        manageChannels: false,
                        banMembers: false,
                        kickMembers: false,
                        manageWebhooks: false);
                    await role.ModifyAsync(p => p.Permissions = safePerms);
                }
                catch
                {
                }
            }
        }

        await SendSecurityLogAsync(guild, triggeredById, "АКТИВИРОВАН РЕЖИМ PANIC",
            "Принудительная блокировка сервера и снятие опасных полномочий со всех ролей",
            "Lockdown + сброс административных флагов ролей", ct);
    }

    private async Task CheckThresholdAndPunishAsync(
        SocketGuild guild,
        ulong actorId,
        string category,
        int maxPerMinute,
        string description,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (guild.Id, actorId, category);
        var list = _actionTracker.GetOrAdd(key, _ => []);

        int count;
        lock (list)
        {
            list.RemoveAll(t => (now - t).TotalSeconds > 60);
            list.Add(now);
            count = list.Count;
        }

        if (count > maxPerMinute)
        {
            await NeutralizeOffenderAsync(guild, actorId,
                $"{description} ({count} действий за минуту при лимите {maxPerMinute})",
                "Снятие всех ролей, бан нарушителя и уведомление владельца", ct);
        }
    }

    private async Task NeutralizeOffenderAsync(
        SocketGuild guild,
        ulong offenderId,
        string details,
        string actionTaken,
        CancellationToken ct)
    {
        try
        {
            var member = await guild.GetGuildUserSafeAsync(offenderId);
            if (member is not null && member.Hierarchy < guild.CurrentUser.Hierarchy)
            {
                var removableRoleIds = member.RoleIds.Where(rId => rId != guild.EveryoneRole.Id && guild.GetRole(rId) is { IsManaged: false }).ToList();
                if (removableRoleIds.Count > 0)
                    await member.RemoveRolesAsync(removableRoleIds);
            }

            await guild.AddBanAsync(offenderId, 0, $"Anti-Nuke KRLZ: {details}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось забанить нарушителя Anti-Nuke {OffenderId}", offenderId);
        }

        await SendSecurityLogAsync(guild, offenderId, "Срабатывание защиты Anti-Nuke", details, actionTaken, ct);

        try
        {
            var owner = guild.Owner;
            if (owner is not null)
            {
                var alertEmbed = embeds.Error(
                    $"**Сервер:** {guild.Name}\n**Нарушитель:** <@{offenderId}> (`{offenderId}`)\n**Событие:** {details}\n**Принятые меры:** {actionTaken}",
                    "КРИТИЧЕСКОЕ УВЕДОМЛЕНИЕ ANTI-NUKE");
                await owner.SendMessageAsync(embed: alertEmbed);
            }
        }
        catch
        {
        }
    }

    public async Task SendSecurityLogAsync(
        SocketGuild guild,
        ulong offenderId,
        string eventType,
        string details,
        string actionTaken,
        CancellationToken ct = default)
    {
        await securityRepo.LogSecurityEventAsync(guild.Id, offenderId, eventType, details, actionTaken, ct);

        try
        {
            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
            if (cfg.SecurityLogChannelId == 0 || guild.GetTextChannel(cfg.SecurityLogChannelId) is not { } channel)
                return;

            var embed = embeds.CreateBase($"Безопасность: {eventType}", null, new Color(237, 66, 69))
                .AddField("Субъект", $"<@{offenderId}> (`{offenderId}`)", true)
                .AddField("Принятые меры", actionTaken, true)
                .AddField("Подробности", details, false)
                .Build();

            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка отправки записи в security-лог гильдии {GuildId}", guild.Id);
        }
    }
}
