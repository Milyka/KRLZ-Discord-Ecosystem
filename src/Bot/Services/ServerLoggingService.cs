using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class ServerLoggingService(
    VerificationAndLogsRepository logsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<ServerLoggingService> logger)
{
    public async Task HandleMessageReceivedAsync(SocketUserMessage message, CancellationToken ct = default)
    {
        if (message.Author.IsBot || message.Channel is not SocketGuildChannel guildChannel)
            return;

        var attachments = message.Attachments.Count > 0
            ? string.Join(", ", message.Attachments.Select(a => a.Url))
            : null;

        await logsRepo.UpsertMessageLogAsync(
            message.Id,
            guildChannel.Guild.Id,
            guildChannel.Id,
            message.Author.Id,
            message.Author.Username,
            message.Content ?? string.Empty,
            attachments,
            ct);
    }

    public async Task HandleMessageUpdatedAsync(
        Cacheable<IMessage, ulong> beforeCache,
        SocketMessage after,
        ISocketMessageChannel channel,
        CancellationToken ct = default)
    {
        if (after.Author.IsBot || channel is not SocketGuildChannel guildChannel)
            return;

        var guild = guildChannel.Guild;
        var stored = await logsRepo.GetMessageLogAsync(after.Id, ct);
        var beforeContent = beforeCache.HasValue
            ? beforeCache.Value.Content
            : stored?.EditedContent ?? stored?.Content ?? "[Сообщение вне кэша]";

        var afterContent = after.Content ?? string.Empty;
        if (string.Equals(beforeContent, afterContent, StringComparison.Ordinal))
            return;

        var attachments = after.Attachments.Count > 0
            ? string.Join(", ", after.Attachments.Select(a => a.Url))
            : null;

        await logsRepo.UpsertMessageLogAsync(
            after.Id, guild.Id, channel.Id, after.Author.Id, after.Author.Username, afterContent, attachments, ct);

        var logChId = settingsProvider.Current.Logging.MessageLogChannelId;
        if (logChId == 0 || guild.GetTextChannel(logChId) is not { } logChannel)
            return;

        try
        {
            var embed = embeds.CreateBase("Сообщение отредактировано", null, new Color(254, 231, 92))
                .AddField("Автор", $"<@{after.Author.Id}> (`{after.Author.Id}`)", true)
                .AddField("Канал", $"<#{channel.Id}>", true)
                .AddField("До изменения", Truncate(beforeContent, 950), false)
                .AddField("После изменения", Truncate(afterContent, 950), false)
                .Build();

            await logChannel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ошибка отправки лога редактирования сообщения");
        }
    }

    public async Task HandleMessageDeletedAsync(
        Cacheable<IMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        CancellationToken ct = default)
    {
        var stored = await logsRepo.GetMessageLogAsync(cachedMessage.Id, ct);
        await logsRepo.MarkMessageDeletedAsync(cachedMessage.Id, ct);

        SocketGuild? guild = null;
        if (cachedChannel.HasValue && cachedChannel.Value is SocketGuildChannel sgc)
            guild = sgc.Guild;

        if (guild is null)
            return;

        var logChId = settingsProvider.Current.Logging.MessageLogChannelId;
        if (logChId == 0 || guild.GetTextChannel(logChId) is not { } logChannel)
            return;

        var authorId = cachedMessage.HasValue ? cachedMessage.Value.Author.Id : stored?.AuthorId ?? 0;
        var content = cachedMessage.HasValue
            ? cachedMessage.Value.Content
            : stored?.EditedContent ?? stored?.Content ?? "[Содержимое не сохранено в кэше]";

        var attachments = stored?.Attachments;
        if (cachedMessage.HasValue && cachedMessage.Value.Attachments.Count > 0)
            attachments = string.Join(", ", cachedMessage.Value.Attachments.Select(a => a.Url));

        try
        {
            var builder = embeds.CreateBase("Сообщение удалено", null, new Color(237, 66, 69))
                .AddField("Автор", authorId != 0 ? $"<@{authorId}> (`{authorId}`)" : "Неизвестен", true)
                .AddField("Канал", $"<#{cachedChannel.Id}>", true)
                .AddField("Текст сообщения", Truncate(string.IsNullOrWhiteSpace(content) ? "[Без текста]" : content, 950), false);

            if (!string.IsNullOrWhiteSpace(attachments))
                builder.AddField("Вложения", Truncate(attachments, 900), false);

            await logChannel.SendMessageAsync(embed: builder.Build());
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ошибка отправки лога удаления сообщения");
        }
    }

    public async Task HandleGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> beforeCache,
        SocketGuildUser after,
        CancellationToken ct = default)
    {
        if (!beforeCache.HasValue)
            return;

        var before = beforeCache.Value;
        var guild = after.Guild;
        var memberLogId = settingsProvider.Current.Logging.MemberLogChannelId;
        var logChannel = memberLogId != 0 ? guild.GetTextChannel(memberLogId) : null;

        if (!string.Equals(before.Nickname, after.Nickname, StringComparison.Ordinal))
        {
            await logsRepo.AddNicknameHistoryAsync(guild.Id, after.Id, before.Nickname ?? before.Username, after.Nickname ?? after.Username, ct);

            if (logChannel is not null)
            {
                var embed = embeds.Info("Смена никнейма участника",
                    $"**Участник:** <@{after.Id}> (`{after.Id}`)\n" +
                    $"**Было:** `{before.Nickname ?? before.Username}`\n" +
                    $"**Стало:** `{after.Nickname ?? after.Username}`");
                try { await logChannel.SendMessageAsync(embed: embed); } catch { }
            }
        }

        var beforeRoles = before.Roles.Select(r => r.Id).ToHashSet();
        var afterRoles = after.Roles.Select(r => r.Id).ToHashSet();

        var addedRoles = afterRoles.Except(beforeRoles).Select(id => $"<@&{id}>").ToList();
        var removedRoles = beforeRoles.Except(afterRoles).Select(id => $"<@&{id}>").ToList();

        if ((addedRoles.Count > 0 || removedRoles.Count > 0) && logChannel is not null)
        {
            var lines = new List<string> { $"**Участник:** <@{after.Id}> (`{after.Id}`)" };
            if (addedRoles.Count > 0) lines.Add($"**Выданы роли:** {string.Join(", ", addedRoles)}");
            if (removedRoles.Count > 0) lines.Add($"**Сняты роли:** {string.Join(", ", removedRoles)}");

            try
            {
                await logChannel.SendMessageAsync(embed: embeds.Info("Изменение ролей участника", string.Join(Environment.NewLine, lines)));
            }
            catch
            {
            }
        }
    }

    public async Task HandleVoiceLogAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot || user is not SocketGuildUser member)
            return;

        var voiceLogId = settingsProvider.Current.Logging.VoiceLogChannelId;
        if (voiceLogId == 0 || member.Guild.GetTextChannel(voiceLogId) is not { } logChannel)
            return;

        string? description = null;
        if (before.VoiceChannel is null && after.VoiceChannel is not null)
            description = $"Участник <@{member.Id}> подключился к голосовому каналу <#{after.VoiceChannel.Id}>.";
        else if (before.VoiceChannel is not null && after.VoiceChannel is null)
            description = $"Участник <@{member.Id}> вышел из голосового канала <#{before.VoiceChannel.Id}>.";
        else if (before.VoiceChannel is not null && after.VoiceChannel is not null && before.VoiceChannel.Id != after.VoiceChannel.Id)
            description = $"Участник <@{member.Id}> перешёл из <#{before.VoiceChannel.Id}> в <#{after.VoiceChannel.Id}>.";

        if (description is null)
            return;

        try
        {
            await logChannel.SendMessageAsync(embed: embeds.Info("Голосовая активность", description));
        }
        catch
        {
        }
    }

    public async Task HandleChannelCreatedOrDeletedAsync(SocketChannel channel, bool created)
    {
        if (channel is not SocketGuildChannel gc)
            return;

        var chLogId = settingsProvider.Current.Logging.ChannelEventsLogChannelId;
        if (chLogId == 0 || gc.Guild.GetTextChannel(chLogId) is not { } logChannel)
            return;

        var title = created ? "Создан новый канал" : "Канал удалён";
        var color = created ? new Color(87, 242, 135) : new Color(237, 66, 69);

        try
        {
            var embed = embeds.CreateBase(title, $"**Название:** `{gc.Name}` (`{gc.Id}`)", color).Build();
            await logChannel.SendMessageAsync(embed: embed);
        }
        catch
        {
        }
    }

    public async Task PurgeExpiredMessageLogsAsync(CancellationToken ct = default)
    {
        var days = Math.Max(1, settingsProvider.Current.Logging.RetentionDays);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        await logsRepo.PurgeOldLogsAsync(cutoff, ct);
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen] + "...";
}
