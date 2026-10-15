using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class ModerationService(
    ModerationRepository modRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds,
    ILogger<ModerationService> logger)
{
    public async Task<long> BanAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        ulong targetUserId,
        string reason,
        TimeSpan? duration,
        CancellationToken ct = default)
    {
        var expiresAt = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : (DateTimeOffset?)null;
        var targetMember = await guild.GetGuildUserSafeAsync(targetUserId);

        if (targetMember is not null)
            await TryNotifyUserAsync(targetMember, guild.Name, "Бан на сервере", reason, duration);

        await guild.AddBanAsync(targetUserId, 1, reason);
        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, targetUserId, "ban", ct);
        var id = await modRepo.AddPunishmentAsync(guild.Id, targetUserId, moderator.Id, "ban", reason, expiresAt, ct);

        await SendModLogAsync(guild, "Бан участника", targetUserId, moderator.Id, reason, duration, ct);
        return id;
    }

    public async Task<long> UnbanAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        ulong targetUserId,
        string reason,
        CancellationToken ct = default)
    {
        await guild.RemoveBanAsync(targetUserId);
        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, targetUserId, "ban", ct);
        var id = await modRepo.AddPunishmentAsync(guild.Id, targetUserId, moderator.Id, "unban", reason, null, ct);

        await SendModLogAsync(guild, "Разбан участника", targetUserId, moderator.Id, reason, null, ct);
        return id;
    }

    public async Task<long> KickAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        IGuildUser target,
        string reason,
        CancellationToken ct = default)
    {
        await TryNotifyUserAsync(target, guild.Name, "Кик с сервера", reason, null);
        await target.KickAsync(reason);
        var id = await modRepo.AddPunishmentAsync(guild.Id, target.Id, moderator.Id, "kick", reason, null, ct);

        await SendModLogAsync(guild, "Кик участника", target.Id, moderator.Id, reason, null, ct);
        return id;
    }

    public async Task<long> MuteAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        IGuildUser target,
        TimeSpan duration,
        string reason,
        CancellationToken ct = default)
    {
        var capped = duration > TimeSpan.FromDays(28) ? TimeSpan.FromDays(28) : duration;
        var expiresAt = DateTimeOffset.UtcNow.Add(capped);

        await target.SetTimeOutAsync(capped, new RequestOptions { AuditLogReason = reason });

        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if (cfg.MutedRoleId != 0 && guild.GetRole(cfg.MutedRoleId) is { } muteRole)
            await target.AddRoleAsync(muteRole);

        await TryNotifyUserAsync(target, guild.Name, "Таймаут (Мут)", reason, capped);
        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, target.Id, "mute", ct);
        var id = await modRepo.AddPunishmentAsync(guild.Id, target.Id, moderator.Id, "mute", reason, expiresAt, ct);

        await SendModLogAsync(guild, "Мут участника", target.Id, moderator.Id, reason, capped, ct);
        return id;
    }

    public async Task<long> UnmuteAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        IGuildUser target,
        string reason,
        CancellationToken ct = default)
    {
        await target.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason });

        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if (cfg.MutedRoleId != 0 && guild.GetRole(cfg.MutedRoleId) is { } muteRole && target.RoleIds.Contains(muteRole.Id))
            await target.RemoveRoleAsync(muteRole);

        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, target.Id, "mute", ct);
        var id = await modRepo.AddPunishmentAsync(guild.Id, target.Id, moderator.Id, "unmute", reason, null, ct);

        await TryNotifyUserAsync(target, guild.Name, "Снятие мута", reason, null);
        await SendModLogAsync(guild, "Снятие мута", target.Id, moderator.Id, reason, null, ct);
        return id;
    }

    public async Task<(long WarnId, int ActiveCount, string? EscalationAction)> WarnAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        IGuildUser target,
        string reason,
        CancellationToken ct = default)
    {
        var warnId = await modRepo.AddWarnAsync(guild.Id, target.Id, moderator.Id, reason, ct);
        await modRepo.AddPunishmentAsync(guild.Id, target.Id, moderator.Id, "warn", reason, null, ct);
        var activeCount = await modRepo.GetActiveWarnCountAsync(guild.Id, target.Id, ct);

        await TryNotifyUserAsync(target, guild.Name, $"Предупреждение #{warnId} (активных: {activeCount})", reason, null);
        await SendModLogAsync(guild, $"Предупреждение #{warnId}", target.Id, moderator.Id, reason, null, ct);

        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        string? escalation = null;

        if (cfg.WarnThresholdBan > 0 && activeCount >= cfg.WarnThresholdBan)
        {
            if (HierarchyGuard.CanModerate(guild, guild.CurrentUser, target, out _))
            {
                await BanAsync(guild, guild.CurrentUser, target.Id, $"Автоэскалация: достигнут лимит {activeCount} предупреждений", null, ct);
                escalation = $"Автоматический бан ({activeCount}/{cfg.WarnThresholdBan} пред.)";
            }
        }
        else if (cfg.WarnThresholdMute > 0 && activeCount >= cfg.WarnThresholdMute)
        {
            if (HierarchyGuard.CanModerate(guild, guild.CurrentUser, target, out _))
            {
                var muteDuration = TimeSpan.FromMinutes(Math.Max(5, cfg.WarnMuteMinutes));
                await MuteAsync(guild, guild.CurrentUser, target, muteDuration, $"Автоэскалация: {activeCount} предупреждений", ct);
                escalation = $"Автоматический мут на {TimeParser.FormatDuration(muteDuration)} ({activeCount}/{cfg.WarnThresholdMute} пред.)";
            }
        }

        return (warnId, activeCount, escalation);
    }

    public async Task<bool> UnwarnAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        ulong targetUserId,
        long warnId,
        string reason,
        CancellationToken ct = default)
    {
        var removed = await modRepo.DeactivateWarnAsync(guild.Id, warnId, ct);
        if (!removed)
            return false;

        await modRepo.AddPunishmentAsync(guild.Id, targetUserId, moderator.Id, "unwarn", $"Снят варн #{warnId}: {reason}", null, ct);
        await SendModLogAsync(guild, $"Снятие предупреждения #{warnId}", targetUserId, moderator.Id, reason, null, ct);
        return true;
    }

    public async Task VoiceBanAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        SocketGuildUser target,
        TimeSpan? duration,
        string reason,
        CancellationToken ct = default)
    {
        var expiresAt = duration.HasValue ? DateTimeOffset.UtcNow.Add(duration.Value) : (DateTimeOffset?)null;
        await modRepo.SetVoiceBanAsync(guild.Id, target.Id, moderator.Id, reason, expiresAt, ct);
        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, target.Id, "vban", ct);
        await modRepo.AddPunishmentAsync(guild.Id, target.Id, moderator.Id, "vban", reason, expiresAt, ct);

        if (target.VoiceChannel is not null)
            await target.ModifyAsync(p => p.Channel = null);

        await TryNotifyUserAsync(target, guild.Name, "Голосовой бан", reason, duration);
        await SendModLogAsync(guild, "Голосовой бан", target.Id, moderator.Id, reason, duration, ct);
    }

    public async Task VoiceUnbanAsync(
        SocketGuild guild,
        SocketGuildUser moderator,
        ulong targetUserId,
        string reason,
        CancellationToken ct = default)
    {
        await modRepo.RemoveVoiceBanAsync(guild.Id, targetUserId, ct);
        await modRepo.DeactivatePunishmentsByTypeAsync(guild.Id, targetUserId, "vban", ct);
        await modRepo.AddPunishmentAsync(guild.Id, targetUserId, moderator.Id, "vunban", reason, null, ct);

        await SendModLogAsync(guild, "Снятие голосового бана", targetUserId, moderator.Id, reason, null, ct);
    }

    public async Task SendModLogAsync(
        SocketGuild guild,
        string actionTitle,
        ulong targetId,
        ulong moderatorId,
        string reason,
        TimeSpan? duration,
        CancellationToken ct = default)
    {
        try
        {
            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
            if (cfg.ModLogChannelId == 0 || guild.GetTextChannel(cfg.ModLogChannelId) is not { } logChannel)
                return;

            var embed = embeds.CreateBase($"Модерация: {actionTitle}")
                .AddField("Участник", $"<@{targetId}> (`{targetId}`)", true)
                .AddField("Модератор", $"<@{moderatorId}> (`{moderatorId}`)", true)
                .AddField("Срок", duration.HasValue ? TimeParser.FormatDuration(duration.Value) : "Постоянно / Мгновенно", true)
                .AddField("Причина", string.IsNullOrWhiteSpace(reason) ? "Не указана" : reason, false)
                .Build();

            await logChannel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка отправки модераторского лога в гильдии {GuildId}", guild.Id);
        }
    }

    private async Task TryNotifyUserAsync(
        IUser user,
        string guildName,
        string action,
        string reason,
        TimeSpan? duration)
    {
        try
        {
            var embed = embeds.CreateBase($"Уведомление с сервера {guildName}")
                .AddField("Действие", action, true)
                .AddField("Срок", duration.HasValue ? TimeParser.FormatDuration(duration.Value) : "Не ограничен", true)
                .AddField("Причина", reason, false)
                .Build();

            await user.SendMessageAsync(embed: embed);
        }
        catch
        {
        }
    }
}
