using System.Globalization;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Bot.Services;

public sealed class CommunityEventsService(
    CommunityEventsRepository eventsRepo,
    EmbedFactory embeds,
    ILogger<CommunityEventsService> logger)
{
    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);
    private static readonly SKTypeface FontRegular = LoadTypeface("Inter-Regular.ttf", SKFontStyle.Normal);

    private static readonly SKColor AccentCyan = new(56, 189, 248);
    private static readonly SKColor GlassFill = new(16, 22, 34, 210);
    private static readonly SKColor GlassBorder = new(255, 255, 255, 24);
    private static readonly SKColor TrackFill = new(255, 255, 255, 12);
    private static readonly SKColor TrackBorder = new(255, 255, 255, 20);

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, style);
    }

    public async Task<byte[]> RenderPollChartPngAsync(
        PollV2Entity poll,
        IReadOnlyList<string> options,
        IReadOnlyList<PollV2VoteEntity> votes,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            const int width = 1024;
            var height = 126 + options.Count * 72 + 36;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(14, 16, 25));

            canvas.Save();
            using var cardClip = new SKPath();
            cardClip.AddRoundRect(new SKRect(0, 0, width, height), 26, 26);
            canvas.ClipPath(cardClip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, width, height, "cyberpunk.png");

            var panelRect = new SKRoundRect(new SKRect(28, 24, width - 28, height - 24), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(panelRect, panelPaint);
            canvas.DrawRoundRect(panelRect, borderPaint);

            DrawGlowingPillTab(canvas, 28f, 38f, 5f, 38f, AccentCyan);
            DrawGlowingPillTab(canvas, width - 33f, 38f, 5f, 38f, AccentCyan);

            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 25f, Color = SKColors.White, IsAntialias = true };
            canvas.DrawText(ClipText(poll.Question, titlePaint, width - 120f), 54f, 68f, titlePaint);

            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 13.5f, Color = new SKColor(148, 163, 184), IsAntialias = true };
            var isExpired = poll.EndsAt <= DateTimeOffset.UtcNow || poll.Status == "closed";
            var statusStr = isExpired ? "Завершён" : $"До {poll.EndsAt:dd.MM HH:mm} UTC";
            var modeStr = poll.IsAnonymous ? "   •   🔒 Анонимный" : string.Empty;
            canvas.DrawText($"Всего голосов: {votes.Count}{modeStr}   •   {statusStr}", 54f, 96f, subPaint);

            using var divPaint = new SKPaint { Color = new SKColor(255, 255, 255, 18), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawLine(54f, 114f, width - 54f, 114f, divPaint);

            var total = Math.Max(1, votes.Count);
            var maxVotes = votes.Count > 0
                ? Enumerable.Range(0, options.Count).Select(idx => votes.Count(v => v.OptionIndex == idx)).Max()
                : 0;

            const float startY = 128f;
            const float optH = 56f;
            var optW = width - 108f;

            for (var i = 0; i < options.Count; i++)
            {
                var y = startY + i * 72f;
                var count = votes.Count(v => v.OptionIndex == i);
                var ratio = votes.Count > 0 ? (float)count / total : 0f;
                var isLeading = count > 0 && count == maxVotes;

                var optRect = new SKRoundRect(new SKRect(54f, y, width - 54f, y + optH), 14, 14);

                using var optBg = new SKPaint { Color = TrackFill, IsAntialias = true };
                using var optBorder = new SKPaint
                {
                    Color = isLeading ? new SKColor(56, 189, 248, 90) : TrackBorder,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.2f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(optRect, optBg);

                if (count > 0)
                {
                    canvas.Save();
                    canvas.ClipRoundRect(optRect, SKClipOperation.Intersect, true);

                    var fillW = Math.Max(20f, optW * ratio);
                    using var fillPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = isLeading
                            ? SKShader.CreateLinearGradient(
                                new SKPoint(54f, y),
                                new SKPoint(54f + fillW, y),
                                [new SKColor(56, 189, 248, 200), new SKColor(99, 102, 241, 215)],
                                (float[]?)null,
                                SKShaderTileMode.Clamp)
                            : SKShader.CreateLinearGradient(
                                new SKPoint(54f, y),
                                new SKPoint(54f + fillW, y),
                                [new SKColor(59, 130, 246, 115), new SKColor(79, 70, 229, 135)],
                                (float[]?)null,
                                SKShaderTileMode.Clamp)
                    };
                    canvas.DrawRect(new SKRect(54f, y, 54f + fillW, y + optH), fillPaint);

                    if (isLeading)
                    {
                        using var edgePaint = new SKPaint
                        {
                            Color = new SKColor(125, 211, 252, 230),
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 2.5f,
                            IsAntialias = true
                        };
                        canvas.DrawLine(54f + fillW, y + 2f, 54f + fillW, y + optH - 2f, edgePaint);
                    }

                    canvas.Restore();
                }

                canvas.DrawRoundRect(optRect, optBorder);

                var numRect = new SKRoundRect(new SKRect(70f, y + 14f, 98f, y + 42f), 8, 8);
                using var numBg = new SKPaint
                {
                    Color = isLeading ? new SKColor(56, 189, 248, 45) : new SKColor(255, 255, 255, 16),
                    IsAntialias = true
                };
                using var numBorder = new SKPaint
                {
                    Color = isLeading ? AccentCyan.WithAlpha(170) : new SKColor(255, 255, 255, 35),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(numRect, numBg);
                canvas.DrawRoundRect(numRect, numBorder);

                using var numTextPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 14f,
                    Color = isLeading ? AccentCyan : SKColors.White,
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText($"{i + 1}", 84f, y + 33f, numTextPaint);

                using var optTextPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 16.5f,
                    Color = SKColors.White,
                    IsAntialias = true
                };
                var maxTextW = isLeading ? optW - 350f : optW - 270f;
                var optDisplay = ClipText(options[i], optTextPaint, maxTextW);
                canvas.DrawText(optDisplay, 114f, y + 35f, optTextPaint);

                if (isLeading)
                {
                    var optTextW = optTextPaint.MeasureText(optDisplay);
                    var leadBadgeX = 114f + optTextW + 16f;
                    if (leadBadgeX < width - 240f)
                    {
                        var leadRect = new SKRoundRect(new SKRect(leadBadgeX, y + 17f, leadBadgeX + 66f, y + 39f), 6, 6);
                        using var leadBg = new SKPaint { Color = new SKColor(56, 189, 248, 30), IsAntialias = true };
                        using var leadBorder = new SKPaint { Color = new SKColor(56, 189, 248, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
                        using var leadText = new SKPaint { Typeface = FontBold, TextSize = 11f, Color = AccentCyan, IsAntialias = true, TextAlign = SKTextAlign.Center };
                        canvas.DrawRoundRect(leadRect, leadBg);
                        canvas.DrawRoundRect(leadRect, leadBorder);
                        canvas.DrawText("★ ТОП", leadBadgeX + 33f, y + 33f, leadText);
                    }
                }

                var pctStr = string.Create(CultureInfo.InvariantCulture, $"{ratio * 100.0:F1}%");
                var countStr = $"({count} {GetVoteWord(count)})";

                using var pctPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 17.5f,
                    Color = isLeading ? AccentCyan : SKColors.White,
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Right
                };
                using var countPaint = new SKPaint
                {
                    Typeface = FontRegular,
                    TextSize = 13.5f,
                    Color = new SKColor(148, 163, 184),
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Right
                };

                canvas.DrawText(countStr, width - 74f, y + 34.5f, countPaint);
                var countW = countPaint.MeasureText(countStr);
                canvas.DrawText(pctStr, width - 84f - countW, y + 35f, pctPaint);
            }

            canvas.Restore();

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    private static void DrawBackground(SKCanvas canvas, int width, int height, string selectedBackground)
    {
        var safeFile = Path.GetFileName(string.IsNullOrWhiteSpace(selectedBackground) ? "cyberpunk.png" : selectedBackground);
        var bgPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", safeFile);

        var drewBitmap = false;
        if (File.Exists(bgPath))
        {
            using var bgBitmap = SKBitmap.Decode(bgPath);
            if (bgBitmap is not null)
            {
                var scale = Math.Max((float)width / bgBitmap.Width, (float)height / bgBitmap.Height);
                var scaledW = bgBitmap.Width * scale;
                var scaledH = bgBitmap.Height * scale;
                var dx = (width - scaledW) / 2f;
                var dy = (height - scaledH) / 2f;

                canvas.DrawBitmap(bgBitmap, new SKRect(dx, dy, dx + scaledW, dy + scaledH));
                drewBitmap = true;
            }
        }

        if (!drewBitmap)
        {
            using var shaderPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(width, height),
                    [new SKColor(17, 20, 34), new SKColor(32, 24, 56)],
                    (float[]?)null,
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(new SKRect(0, 0, width, height), shaderPaint);
        }

        using var vignettePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                [
                    new SKColor(0, 0, 0, 0),
                    new SKColor(10, 15, 26, 40),
                    new SKColor(10, 15, 26, 110),
                    new SKColor(10, 15, 26, 170)
                ],
                [0f, 0.35f, 0.65f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), vignettePaint);
    }

    private static void DrawGlowingPillTab(SKCanvas canvas, float x, float y, float w, float h, SKColor? colorOverride = null)
    {
        var col = colorOverride ?? AccentCyan;
        using var glow = new SKPaint
        {
            Color = col.WithAlpha(120),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f)
        };
        var rect = new SKRoundRect(new SKRect(x, y, x + w, y + h), w / 2f, w / 2f);
        canvas.DrawRoundRect(rect, glow);

        using var pill = new SKPaint { Color = col, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRoundRect(rect, pill);
    }

    private static string ClipText(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (paint.MeasureText(text) <= maxWidth)
            return text;

        var span = text.AsSpan();
        for (var len = span.Length - 1; len > 0; len--)
        {
            var candidate = string.Concat(span[..len], "...");
            if (paint.MeasureText(candidate) <= maxWidth)
                return candidate;
        }

        return "...";
    }

    private static string GetVoteWord(int count)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        if (mod100 is >= 11 and <= 19) return "голосов";
        return mod10 switch
        {
            1 => "голос",
            2 or 3 or 4 => "голоса",
            _ => "голосов"
        };
    }

    public async Task ProcessDuePollsAndGiveawaysAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var duePolls = await eventsRepo.GetDueActivePollsAsync(now, ct);
        foreach (var poll in duePolls)
        {
            try
            {
                if (!await eventsRepo.MarkPollClosedAsync(poll.Id, ct))
                    continue;

                var guild = client.GetGuild(poll.GuildId);
                var channel = guild?.GetTextChannel(poll.ChannelId);
                if (channel is null)
                    continue;

                var options = JsonSerializer.Deserialize<List<string>>(poll.OptionsJson) ?? [];
                var votes = await eventsRepo.GetPollVotesAsync(poll.Id, ct);
                var chartBytes = await RenderPollChartPngAsync(poll, options, votes, ct);

                var embed = new EmbedBuilder()
                    .WithColor(new Color(56, 189, 248))
                    .WithImageUrl("attachment://poll-results.png")
                    .Build();

                await using var stream = new MemoryStream(chartBytes);
                await channel.SendFileAsync(stream, "poll-results.png", embed: embed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка завершения опроса #{PollId}", poll.Id);
            }
        }

        var dueGiveaways = await eventsRepo.GetDueActiveGiveawaysAsync(now, ct);
        foreach (var gw in dueGiveaways)
        {
            try
            {
                await FinalizeGiveawayAsync(client, gw, isReroll: false, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка завершения розыгрыша #{GiveawayId}", gw.Id);
            }
        }
    }

    public async Task<IReadOnlyList<ulong>> FinalizeGiveawayAsync(
        DiscordSocketClient client,
        GiveawayEntity gw,
        bool isReroll,
        CancellationToken ct = default)
    {
        var participants = (await eventsRepo.GetGiveawayParticipantIdsAsync(gw.Id, ct)).ToList();
        var existingWinners = isReroll
            ? (await eventsRepo.GetGiveawayWinnerIdsAsync(gw.Id, ct)).ToHashSet()
            : [];

        var pool = participants.Where(u => !existingWinners.Contains(u)).ToList();
        var selected = new List<ulong>();
        var needed = isReroll ? 1 : Math.Max(1, gw.WinnerCount);

        while (pool.Count > 0 && selected.Count < needed)
        {
            var idx = CasinoService.NextCryptoInt(0, pool.Count);
            selected.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        await eventsRepo.RecordGiveawayWinnersAsync(gw.Id, gw.GuildId, selected, isReroll, ct);

        var guild = client.GetGuild(gw.GuildId);
        var channel = guild?.GetTextChannel(gw.ChannelId);
        if (channel is not null)
        {
            if (selected.Count == 0)
            {
                await channel.SendMessageAsync(embed: embeds.Warning(
                    $"Розыгрыш #{gw.Id} — {gw.Prize}",
                    "Не удалось выбрать победителей (недостаточно участников)."));
            }
            else
            {
                var mentions = string.Join(", ", selected.Select(id => $"<@{id}>"));
                var title = isReroll ? $"Перевыбор победителя розыгрыша #{gw.Id}" : $"Розыгрыш #{gw.Id} завершён!";
                await channel.SendMessageAsync(
                    text: mentions,
                    embed: embeds.Success(title, $"**Приз:** {gw.Prize}\n**Победители:** {mentions}\n**Организатор:** <@{gw.HostId}>"));
            }
        }

        return selected;
    }
}
