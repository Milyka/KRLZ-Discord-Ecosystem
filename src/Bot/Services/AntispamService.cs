using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bot.Data;
using Bot.Infrastructure;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed partial class AntispamService(
    GuildSettingsRepository settingsRepo,
    ModerationService moderationService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<AntispamService> logger)
{
    private sealed record MessageSample(string Content, DateTimeOffset Timestamp);

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), List<MessageSample>> _recentMessages = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), int> _strikes = new();

    [GeneratedRegex(@"(discord\.gg/|discord\.com/invite/|discordapp\.com/invite/)[a-zA-Z0-9-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InviteRegex();

    [GeneratedRegex(@"https?://(?<host>[^/\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<a?:\w+:\d+>", RegexOptions.Compiled)]
    private static partial Regex CustomEmojiRegex();

    public async Task<bool> InspectMessageAsync(SocketUserMessage message, CancellationToken ct = default)
    {
        if (message.Author.IsBot || message.Author is not SocketGuildUser member || message.Channel is not SocketTextChannel channel)
            return false;

        var guild = member.Guild;
        if (member.Id == guild.OwnerId || member.GuildPermissions.Administrator || member.GuildPermissions.ManageMessages)
            return false;

        var dbCfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        if (!dbCfg.AntispamEnabled)
            return false;

        if (dbCfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == dbCfg.AdminRoleId))
            return false;
        if (dbCfg.ModeratorRoleId != 0 && member.Roles.Any(r => r.Id == dbCfg.ModeratorRoleId))
            return false;

        var localCfg = settingsProvider.Current.Antispam;
        if (localCfg.WhitelistedChannelIds.Contains(channel.Id))
            return false;
        if (member.Roles.Any(r => localCfg.WhitelistedRoleIds.Contains(r.Id)))
            return false;

        var content = message.Content ?? string.Empty;
        string? violationReason = null;

        if (message.MentionedEveryone)
            violationReason = "Попытка массового упоминания everyone/here";

        if (violationReason is null && dbCfg.AntispamBlockInvites && InviteRegex().IsMatch(content))
            violationReason = "Размещение приглашений на сторонние серверы Discord";

        if (violationReason is null && dbCfg.AntispamBlockLinks)
        {
            var matches = UrlRegex().Matches(content);
            foreach (Match m in matches)
            {
                var host = m.Groups["host"].Value.ToLowerInvariant();
                var allowed = localCfg.AllowedDomains.Any(d =>
                    host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    violationReason = $"Размещение ссылки вне белого списка ({host})";
                    break;
                }
            }
        }

        if (violationReason is null && message.MentionedUsers.Count + message.MentionedRoles.Count > localCfg.MaxMentionsPerMessage)
            violationReason = $"Превышен лимит упоминаний в одном сообщении ({localCfg.MaxMentionsPerMessage})";

        if (violationReason is null)
        {
            var emojiCount = CustomEmojiRegex().Matches(content).Count + content.EnumerateRunes().Count(r => r.Value >= 0x1F300);
            if (emojiCount > localCfg.MaxEmojisPerMessage)
                violationReason = $"Превышен лимит эмодзи в сообщении ({emojiCount}/{localCfg.MaxEmojisPerMessage})";
        }

        if (violationReason is null && content.Length >= 14)
        {
            var letters = content.Where(char.IsLetter).ToList();
            if (letters.Count >= 10)
            {
                var upperRatio = (double)letters.Count(char.IsUpper) / letters.Count;
                if (upperRatio > localCfg.MaxCapsRatio)
                    violationReason = "Злоупотребление верхним регистром (CAPS)";
            }
        }

        var now = DateTimeOffset.UtcNow;
        var key = (guild.Id, member.Id);
        var history = _recentMessages.GetOrAdd(key, _ => []);

        lock (history)
        {
            history.RemoveAll(x => (now - x.Timestamp).TotalSeconds > Math.Max(15, dbCfg.AntispamWindowSeconds * 3));

            if (violationReason is null)
            {
                var inWindow = history.Count(x => (now - x.Timestamp).TotalSeconds <= dbCfg.AntispamWindowSeconds) + 1;
                if (inWindow > dbCfg.AntispamMaxMessages)
                    violationReason = $"Превышен лимит частоты сообщений ({inWindow} за {dbCfg.AntispamWindowSeconds} сек.)";
            }

            if (violationReason is null && content.Length >= 6 && history.Count >= 2)
            {
                var recentSimilar = history.TakeLast(3).Count(prev =>
                    CalculateSimilarity(prev.Content, content) >= localCfg.MaxSimilarityScore);
                if (recentSimilar >= 2)
                    violationReason = "Флуд однотипными или повторяющимися сообщениями";
            }

            history.Add(new MessageSample(content, now));
        }

        if (violationReason is null)
            return false;

        try
        {
            await message.DeleteAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось удалить спам-сообщение в канале {ChannelId}", channel.Id);
        }

        var strikeCount = _strikes.AddOrUpdate(key, 1, (_, old) => old + 1);
        await ApplyEscalationAsync(guild, channel, member, violationReason, strikeCount, ct);
        return true;
    }

    private async Task ApplyEscalationAsync(
        SocketGuild guild,
        SocketTextChannel channel,
        SocketGuildUser offender,
        string reason,
        int strike,
        CancellationToken ct)
    {
        try
        {
            string actionLabel;
            if (strike == 1)
            {
                await moderationService.WarnAsync(guild, guild.CurrentUser, offender, $"[Антиспам] {reason}", ct);
                actionLabel = "Предупреждение (1-е нарушение)";
            }
            else if (strike == 2)
            {
                await moderationService.MuteAsync(guild, guild.CurrentUser, offender, TimeSpan.FromMinutes(15), $"[Антиспам] {reason}", ct);
                actionLabel = "Таймаут на 15 минут (2-е нарушение)";
            }
            else if (strike == 3)
            {
                await moderationService.KickAsync(guild, guild.CurrentUser, offender, $"[Антиспам] {reason}", ct);
                actionLabel = "Кик с сервера (3-е нарушение)";
            }
            else
            {
                await moderationService.BanAsync(guild, guild.CurrentUser, offender.Id, $"[Антиспам] {reason}", TimeSpan.FromDays(7), ct);
                actionLabel = "Бан на 7 дней (4+ нарушения)";
            }

            var notice = embeds.Warning("Антиспам система KRLZ", $"Участник <@{offender.Id}> нарушил правила чата.\n**Причина:** {reason}\n**Мера:** {actionLabel}");
            var sent = await channel.SendMessageAsync(embed: notice);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
                try { await sent.DeleteAsync(); } catch { }
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка применения эскалации антиспама к {UserId}", offender.Id);
        }
    }

    public static double CalculateSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
            return 1.0;

        var dist = LevenshteinDistance(a.ToLowerInvariant(), b.ToLowerInvariant());
        return 1.0 - (double)dist / maxLen;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
