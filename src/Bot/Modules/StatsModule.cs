using System.Diagnostics;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class StatsModule(
    UserRepository userRepo,
    ProfileCardService profileCardService,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private const int PageSize = 10;

    [SlashCommand("top", "Графическая таблица лидеров сервера по войсу, сообщениям, балансу, уровню или репутации")]
    public async Task TopCommandAsync(
        [Summary("type", "Категория рейтинга"),
         Choice("Время в голосовых каналах", "voice"),
         Choice("Количество сообщений", "messages"),
         Choice("Баланс валюты", "balance"),
         Choice("Уровень и опыт", "level"),
         Choice("Репутация участников", "reputation")] string type = "level",
        [Summary("period", "Период статистики"),
         Choice("За всё время", "all"),
         Choice("За неделю", "week"),
         Choice("За сутки", "day")] string period = "all")
    {
        await DeferAsync();
        var (png, embed, comp) = await BuildLeaderboardVisualAsync(type, period, 0);
        await using var stream = new MemoryStream(png);
        await FollowupWithFileAsync(stream, "leaderboard.png", embed: embed, components: comp);
    }

    [ComponentInteraction("top:page:*:*:*", true)]
    public async Task HandleTopPaginationAsync(string type, string period, string pageStr)
    {
        if (!int.TryParse(pageStr, out var page))
            return;

        await DeferAsync();
        var (png, embed, comp) = await BuildLeaderboardVisualAsync(type, period, page);
        await using var stream = new MemoryStream(png);

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Attachments = new List<FileAttachment> { new(stream, "leaderboard.png") };
            m.Components = comp;
        });
    }

    [SlashCommand("serverinfo", "Подробная информация о сервере KRLZ")]
    public async Task ServerInfoCommandAsync()
    {
        var g = Context.Guild;
        var textCount = g.TextChannels.Count;
        var voiceCount = g.VoiceChannels.Count;
        var categoryCount = g.CategoryChannels.Count;

        var embed = embeds.CreateBase($"Информация о сервере — {g.Name}")
            .WithThumbnailUrl(g.IconUrl)
            .AddField("ID сервера", $"`{g.Id}`", true)
            .AddField("Владелец", $"<@{g.OwnerId}>", true)
            .AddField("Дата создания", TimestampTag.FromDateTimeOffset(g.CreatedAt, TimestampTagStyles.ShortDate), true)
            .AddField("Участники", $"Всего: **{g.MemberCount}**", true)
            .AddField("Каналы", $"Текст: **{textCount}** | Голос: **{voiceCount}** | Категорий: **{categoryCount}**", true)
            .AddField("Бусты", $"Уровень **{g.PremiumTier}** ({g.PremiumSubscriptionCount} бустов)", true)
            .AddField("Ролей", g.Roles.Count.ToString(), true)
            .AddField("Эмодзи", g.Emotes.Count.ToString(), true)
            .Build();

        await RespondAsync(embed: embed);
    }

    [SlashCommand("stats", "Сводная статистика активности сервера и состояния бота")]
    public async Task StatsCommandAsync()
    {
        var totals = await userRepo.GetGuildTotalsAsync(Context.Guild.Id);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        using var proc = Process.GetCurrentProcess();
        var ramMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var uptime = DateTime.UtcNow - proc.StartTime.ToUniversalTime();

        var embed = embeds.CreateBase($"Статистика комплекса {Context.Guild.Name}")
            .AddField("Участников в базе", totals.TotalUsers.ToString("N0"), true)
            .AddField("Оборот валюты", $"{totals.TotalBalance:N0} {cfg.CurrencySymbol}", true)
            .AddField("Всего сообщений", totals.TotalMessages.ToString("N0"), true)
            .AddField("Суммарное время в войсе", TimeParser.FormatVoiceSeconds(totals.TotalVoiceSeconds), true)
            .AddField("Пинг шлюза Discord", $"{Context.Client.Latency} мс", true)
            .AddField("Память / Аптайм", $"{ramMb:F1} МБ • {TimeParser.FormatDuration(uptime)}", true)
            .Build();

        await RespondAsync(embed: embed);
    }

    private async Task<(byte[] PngBytes, Embed Embed, MessageComponent Components)> BuildLeaderboardVisualAsync(
        string type,
        string period,
        int page)
    {
        page = Math.Max(0, page);
        var entries = await userRepo.GetTopAsync(Context.Guild.Id, type, period, PageSize, page * PageSize);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var authorRank = type switch
        {
            "balance" => await userRepo.GetUserRankByBalanceAsync(Context.Guild.Id, Context.User.Id),
            "reputation" => await userRepo.GetUserRankByReputationAsync(Context.Guild.Id, Context.User.Id),
            _ => await userRepo.GetUserRankByXpAsync(Context.Guild.Id, Context.User.Id)
        };

        var titleMetric = type switch
        {
            "voice" => "Время в голосовых каналах",
            "messages" => "Активность в чате",
            "balance" => $"Баланс ({cfg.CurrencyName})",
            "reputation" => "Репутация участников",
            _ => "Уровень и опыт"
        };

        var periodTitle = period switch
        {
            "day" => "За 24 часа",
            "week" => "За 7 дней",
            _ => "За всё время"
        };

        var visualRowTasks = entries.Select(async (row, i) =>
        {
            var rank = page * PageSize + i + 1;
            IGuildUser? member = Context.Guild.GetUser(row.UserId);
            if (member is null)
            {
                try
                {
                    member = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, row.UserId);
                }
                catch
                {
                }
            }

            IUser? restUser = member;
            if (restUser is null)
            {
                try
                {
                    restUser = await Context.Client.Rest.GetUserAsync(row.UserId);
                }
                catch
                {
                }
            }

            var displayName = member?.DisplayName ?? restUser?.GlobalName ?? restUser?.Username ?? $"ID {row.UserId}";
            var avatarUrl = member?.GetAvatarUrl(ImageFormat.Png, 128)
                ?? restUser?.GetAvatarUrl(ImageFormat.Png, 128)
                ?? member?.GetDefaultAvatarUrl()
                ?? restUser?.GetDefaultAvatarUrl();

            var formattedVal = type switch
            {
                "voice" => TimeParser.FormatVoiceSeconds(row.Value),
                "messages" => $"{row.Value:N0} сообщ.",
                "balance" => $"{row.Value:N0} {cfg.CurrencySymbol}",
                "reputation" => $"{(row.Value > 0 ? "+" : "")}{row.Value:N0} реп.",
                _ => $"Ур. {row.Level} ({row.Value:N0} XP)"
            };

            return new LeaderboardVisualRowDto(rank, row.UserId, displayName, avatarUrl, formattedVal);
        });

        var visualRows = (await Task.WhenAll(visualRowTasks)).ToList();

        var png = await profileCardService.RenderLeaderboardCardAsync(
            $"ТОП СЕРВЕРА KRLZ — {titleMetric.ToUpperInvariant()}",
            $"{periodTitle} • Страница {page + 1}",
            visualRows,
            authorRank);

        var embed = new EmbedBuilder()
            .WithColor(embeds.GetThemeColor())
            .WithImageUrl("attachment://leaderboard.png")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Назад", $"top:page:{type}:{period}:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
            .WithButton("Вперёд", $"top:page:{type}:{period}:{page + 1}", ButtonStyle.Secondary, disabled: entries.Count < PageSize)
            .Build();

        return (png, embed, comp);
    }
}
