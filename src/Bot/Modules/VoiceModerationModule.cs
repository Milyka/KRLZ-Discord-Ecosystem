using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
[RequireModerator]
public sealed class VoiceModerationModule(
    ModerationService modService,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("vmute", "Отключить микрофон участнику в голосовом канале")]
    public async Task VoiceMuteCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина")] string reason = "Нарушение правил голосового чата")
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        await target.ModifyAsync(p => p.Mute = true);
        await modService.SendModLogAsync(Context.Guild, "Голосовой мут (Server Mute)", target.Id, actor.Id, reason, null);
        await RespondAsync(embed: embeds.Success("Микрофон отключён", $"Участнику <@{target.Id}> отключён микрофон на сервере.\n**Причина:** {reason}"));
    }

    [SlashCommand("vunmute", "Включить микрофон участнику в голосовом канале")]
    public async Task VoiceUnmuteCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина")] string reason = "Снятие голосового мута")
    {
        await target.ModifyAsync(p => p.Mute = false);
        await modService.SendModLogAsync(Context.Guild, "Снятие голосового мута", target.Id, Context.User.Id, reason, null);
        await RespondAsync(embed: embeds.Success("Микрофон включён", $"Участнику <@{target.Id}> возвращено право голоса."));
    }

    [SlashCommand("vkick", "Отключить участника от текущего голосового канала")]
    public async Task VoiceKickCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина")] string reason = "Нарушение порядка в голосовом канале")
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        if (target.VoiceChannel is null)
        {
            await RespondAsync(embed: embeds.Error("Участник сейчас не находится в голосовом канале."), ephemeral: true);
            return;
        }

        await target.ModifyAsync(p => p.Channel = null);
        await modService.SendModLogAsync(Context.Guild, "Отключение от голосового канала", target.Id, actor.Id, reason, null);
        await RespondAsync(embed: embeds.Success("Участник отключён", $"Участник <@{target.Id}> отключён от голосового канала.\n**Причина:** {reason}"));
    }

    [SlashCommand("vmove", "Переместить участника в другой голосовой канал")]
    public async Task VoiceMoveCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("channel", "Целевой голосовой канал")] SocketVoiceChannel channel)
    {
        if (target.VoiceChannel is null)
        {
            await RespondAsync(embed: embeds.Error("Участник не подключён к голосовому каналу."), ephemeral: true);
            return;
        }

        await target.ModifyAsync(p => p.Channel = channel);
        await modService.SendModLogAsync(Context.Guild, "Перемещение в голосовой канал", target.Id, Context.User.Id, $"Перемещён в {channel.Name}", null);
        await RespondAsync(embed: embeds.Success("Участник перемещён", $"Участник <@{target.Id}> перемещён в <#{channel.Id}>."));
    }

    [SlashCommand("vban", "Запретить участнику вход во все голосовые каналы сервера")]
    public async Task VoiceBanCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина запрета")] string reason = "Постоянные нарушения в войсе",
        [Summary("duration", "Срок (например: 30m, 2h, 7d)")] string? duration = null)
    {
        var actor = (SocketGuildUser)Context.User;
        if (!HierarchyGuard.CanModerate(Context.Guild, actor, target, out var err))
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        TimeSpan? span = null;
        if (!string.IsNullOrWhiteSpace(duration))
        {
            if (!TimeParser.TryParseDuration(duration, out var parsed))
            {
                await RespondAsync(embed: embeds.Error("Неверный формат времени. Используйте `30m`, `2h`, `7d`."), ephemeral: true);
                return;
            }
            span = parsed;
        }

        await modService.VoiceBanAsync(Context.Guild, actor, target, span, reason);
        await RespondAsync(embed: embeds.Warning("Выдан голосовой бан",
            $"Участнику <@{target.Id}> заблокирован доступ к голосовым каналам.\n**Срок:** {(span.HasValue ? TimeParser.FormatDuration(span.Value) : "Постоянно")}\n**Причина:** {reason}"));
    }

    [SlashCommand("vunban", "Снять запрет на вход в голосовые каналы")]
    public async Task VoiceUnbanCommandAsync(
        [Summary("user", "Участник")] SocketGuildUser target,
        [Summary("reason", "Причина снятия")] string reason = "Доступ к голосовым каналам восстановлен")
    {
        await modService.VoiceUnbanAsync(Context.Guild, (SocketGuildUser)Context.User, target.Id, reason);
        await RespondAsync(embed: embeds.Success("Голосовой бан снят", $"Участник <@{target.Id}> снова может заходить в голосовые каналы."));
    }
}
