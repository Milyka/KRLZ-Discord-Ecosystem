using System.Collections.Concurrent;
using System.Globalization;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using SkiaSharp;

namespace Bot.Services;

public sealed record LeaderboardVisualRowDto(
    int Rank,
    ulong UserId,
    string DisplayName,
    string? AvatarUrl,
    string FormattedValue
);

public sealed class ProfileCardService
{
    private const int CardWidth = 1024;
    private const int CardHeight = 576;

    private static readonly SKTypeface FontRegular = LoadTypeface("Inter-Regular.ttf", SKFontStyle.Normal);
    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);

    private static readonly SKColor AccentCyan = new(56, 189, 248);
    private static readonly SKColor AccentBlue = new(37, 99, 235);
    private static readonly SKColor GlassFill = new(16, 22, 34, 195);
    private static readonly SKColor GlassBorder = new(255, 255, 255, 22);
    private static readonly SKColor PillFill = new(255, 255, 255, 14);

    private static readonly ConcurrentDictionary<string, SKBitmap> BackgroundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, (byte[] Bytes, DateTimeOffset ExpiresAt)> _avatarCache = new();

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, style);
    }

    public async Task<byte[]> RenderProfileCardAsync(
        SocketGuildUser member,
        UserProfileEntity profile,
        int rank,
        string currencySymbol,
        string? partnerName,
        string? partnerAvatarUrl,
        CustomRoleEntity? customRole,
        ClanEntity? clan = null,
        IReadOnlyList<AchievementEntity>? equippedBadges = null,
        long reputation = 0,
        string? accentColorHex = null,
        int unlockedAchievementsCount = 0,
        int totalAchievementsCount = 0,
        UserPetEntity? pet = null,
        CancellationToken ct = default)
    {
        var avatarUrl = member.GetAvatarUrl(ImageFormat.Png, 256) ?? member.GetDefaultAvatarUrl();
        var avatarBytes = await GetAvatarBytesAsync(avatarUrl, ct);

        byte[]? partnerAvatarBytes = null;
        if (!string.IsNullOrWhiteSpace(partnerAvatarUrl))
        {
            partnerAvatarBytes = await GetAvatarBytesAsync(partnerAvatarUrl, ct);
        }

        var accent = ResolveAccentColor(accentColorHex, customRole?.ColorHex);

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CardWidth, CardHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var cardClip = new SKPath();
            cardClip.AddRoundRect(new SKRect(0, 0, CardWidth, CardHeight), 28, 28);
            canvas.ClipPath(cardClip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, CardWidth, CardHeight, profile.SelectedBackground);
            DrawCenterAvatarAndName(canvas, member, avatarBytes, profile, customRole, pet, accent);
            DrawMainStatsPanel(canvas, profile, rank, currencySymbol, accent);
            DrawBottomCards(
                canvas,
                partnerName,
                partnerAvatarBytes,
                clan,
                equippedBadges,
                reputation,
                unlockedAchievementsCount,
                totalAchievementsCount,
                accent);

            canvas.Restore();

            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderRankCardAsync(
        SocketGuildUser member,
        UserProfileEntity profile,
        int rank,
        CancellationToken ct = default)
    {
        var avatarUrl = member.GetAvatarUrl(ImageFormat.Png, 256) ?? member.GetDefaultAvatarUrl();
        var avatarBytes = await GetAvatarBytesAsync(avatarUrl, ct);

        return await Task.Run(() =>
        {
            const int w = 1024;
            const int h = 360;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, profile.SelectedBackground);

            var cardRect = new SKRoundRect(new SKRect(36, 32, w - 36, h - 32), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(cardRect, panelPaint);
            canvas.DrawRoundRect(cardRect, borderPaint);

            DrawGlowingPillTab(canvas, 36f, (h - 44f) / 2f, 5f, 44f);
            DrawGlowingPillTab(canvas, w - 41f, (h - 44f) / 2f, 5f, 44f);

            DrawAvatarWithStatus(canvas, 130f, 130f, 62f, avatarBytes, member.Status);

            using var namePaint = new SKPaint { Typeface = FontBold, TextSize = 28f, Color = SKColors.White, IsAntialias = true };
            using var tagPaint = new SKPaint { Typeface = FontRegular, TextSize = 15f, Color = new SKColor(148, 163, 184), IsAntialias = true };
            canvas.DrawText(ClipText(member.DisplayName, namePaint, 380f), 220f, 105f, namePaint);
            canvas.DrawText($"@{member.Username} • Ранг #{rank} • {profile.Title}", 220f, 138f, tagPaint);

            using var lvlLabelPaint = new SKPaint { Typeface = FontBold, TextSize = 15f, Color = AccentCyan, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var lvlValPaint = new SKPaint { Typeface = FontBold, TextSize = 54f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText("УРОВЕНЬ", w - 140f, 96f, lvlLabelPaint);
            canvas.DrawText(profile.Level.ToString(), w - 140f, 155f, lvlValPaint);

            DrawXpProgressBar(canvas, profile, 220f, 175f, w - 380f, 26f);

            var pillY = 226f;
            var pillW = (w - 220f - 160f - 24f) / 2f;

            DrawStatPill(canvas, 220f, pillY, pillW, 46f);
            DrawChatBubbleIcon(canvas, 238f, pillY + 13f, 20f, AccentCyan);
            using var pillLabel = new SKPaint { Typeface = FontRegular, TextSize = 15f, Color = new SKColor(226, 232, 240), IsAntialias = true };
            using var pillVal = new SKPaint { Typeface = FontBold, TextSize = 16f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Right };
            canvas.DrawText("Сообщения", 268f, pillY + 29f, pillLabel);
            canvas.DrawText($"{profile.Messages:N0}", 220f + pillW - 18f, pillY + 29f, pillVal);

            DrawStatPill(canvas, 220f + pillW + 16f, pillY, pillW, 46f);
            DrawClockIcon(canvas, 238f + pillW + 16f, pillY + 13f, 20f, AccentCyan);
            canvas.DrawText("Онлайн", 268f + pillW + 16f, pillY + 29f, pillLabel);
            canvas.DrawText(TimeParser.FormatVoiceSeconds(profile.VoiceSeconds), 220f + 2 * pillW - 2f, pillY + 29f, pillVal);

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderBalanceCardAsync(
        SocketGuildUser member,
        UserProfileEntity profile,
        string currencyName,
        string currencySymbol,
        int balanceRank,
        CancellationToken ct = default)
    {
        var avatarUrl = member.GetAvatarUrl(ImageFormat.Png, 256) ?? member.GetDefaultAvatarUrl();
        var avatarBytes = await GetAvatarBytesAsync(avatarUrl, ct);

        return await Task.Run(() =>
        {
            const int w = 1024;
            const int h = 380;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, profile.SelectedBackground);

            var panelRect = new SKRoundRect(new SKRect(36, 32, w - 36, h - 32), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(panelRect, panelPaint);
            canvas.DrawRoundRect(panelRect, borderPaint);

            DrawGlowingPillTab(canvas, 36f, (h - 44f) / 2f, 5f, 44f);
            DrawGlowingPillTab(canvas, w - 41f, (h - 44f) / 2f, 5f, 44f);

            DrawAvatarWithStatus(canvas, 130f, 135f, 62f, avatarBytes, member.Status);

            using var headerPaint = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = AccentCyan, IsAntialias = true };
            using var balPaint = new SKPaint { Typeface = FontBold, TextSize = 44f, Color = SKColors.White, IsAntialias = true };
            using var namePaint = new SKPaint { Typeface = FontRegular, TextSize = 16f, Color = new SKColor(148, 163, 184), IsAntialias = true };

            canvas.DrawText($"ФИНАНСОВЫЙ БАЛАНС • {currencyName.ToUpperInvariant()}", 220f, 90f, headerPaint);
            var balText = $"{profile.Balance:N0} {currencySymbol}";
            canvas.DrawText(balText, 220f, 145f, balPaint);
            canvas.DrawText($"Владелец: {member.DisplayName} (@{member.Username})", 220f, 180f, namePaint);

            const float gap = 18f;
            var pillY = 230f;
            var pillW = (w - 112f - 2 * gap) / 3f;
            var pill1X = 56f;
            var pill2X = pill1X + pillW + gap;
            var pill3X = pill2X + pillW + gap;

            DrawStatPill(canvas, pill1X, pillY, pillW, 58f);
            DrawWalletIcon(canvas, pill1X + 18f, pillY + 18f, 22f, AccentCyan);
            using var pillTitle = new SKPaint { Typeface = FontRegular, TextSize = 13f, Color = new SKColor(148, 163, 184), IsAntialias = true };
            using var pillValue = new SKPaint { Typeface = FontBold, TextSize = 17f, Color = SKColors.White, IsAntialias = true };
            canvas.DrawText("На руках", pill1X + 52f, pillY + 28f, pillTitle);
            canvas.DrawText(ClipText($"{profile.Balance:N0} {currencySymbol}", pillValue, pillW - 60f), pill1X + 52f, pillY + 49f, pillValue);

            DrawStatPill(canvas, pill2X, pillY, pillW, 58f);
            DrawStarIcon(canvas, pill2X + 26f, pillY + 29f, 12f, AccentCyan);
            canvas.DrawText("Уровень", pill2X + 50f, pillY + 28f, pillTitle);
            canvas.DrawText(ClipText($"Ур. {profile.Level} ({profile.Xp:N0} XP)", pillValue, pillW - 58f), pill2X + 50f, pillY + 49f, pillValue);

            DrawStatPill(canvas, pill3X, pillY, pillW, 58f);
            DrawChartIcon(canvas, pill3X + 16f, pillY + 18f, 22f, AccentCyan);
            canvas.DrawText("Позиция в топе", pill3X + 50f, pillY + 28f, pillTitle);
            canvas.DrawText(ClipText($"#{balanceRank} в рейтинге", pillValue, pillW - 58f), pill3X + 50f, pillY + 49f, pillValue);

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderLeaderboardCardAsync(
        string title,
        string subtitle,
        IReadOnlyList<LeaderboardVisualRowDto> rows,
        int authorRank,
        CancellationToken ct = default)
    {
        var avatarMap = new Dictionary<ulong, byte[]?>();
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.AvatarUrl))
                avatarMap[r.UserId] = await GetAvatarBytesAsync(r.AvatarUrl, ct);
        }

        return await Task.Run(() =>
        {
            const int w = 1024;
            var count = Math.Max(1, rows.Count);
            var h = 160 + count * 56;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(14, 16, 25));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, "cyberpunk.png");

            var outerRect = new SKRoundRect(new SKRect(24, 24, w - 24, h - 24), 22, 22);
            using var outerPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(outerRect, outerPaint);
            canvas.DrawRoundRect(outerRect, borderPaint);

            DrawGlowingPillTab(canvas, 24f, (h - 44f) / 2f, 5f, 44f);
            DrawGlowingPillTab(canvas, w - 29f, (h - 44f) / 2f, 5f, 44f);

            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 25f, Color = SKColors.White, IsAntialias = true };
            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = new SKColor(148, 163, 184), IsAntialias = true };

            canvas.DrawText(title, 44f, 56f, titlePaint);
            canvas.DrawText($"{subtitle}  •  Ваше место: #{authorRank}", 44f, 82f, subPaint);

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var y = 108f + i * 54f;

                using var rowBg = new SKPaint
                {
                    Color = i % 2 == 0 ? new SKColor(255, 255, 255, 12) : new SKColor(255, 255, 255, 7),
                    IsAntialias = true
                };
                var rowRect = new SKRoundRect(new SKRect(44f, y, w - 44f, y + 46f), 12, 12);
                canvas.DrawRoundRect(rowRect, rowBg);

                var rankColor = row.Rank switch
                {
                    1 => new SKColor(245, 158, 11),
                    2 => new SKColor(148, 163, 184),
                    3 => new SKColor(217, 119, 6),
                    _ => AccentCyan
                };

                using var rankBg = new SKPaint { Color = rankColor.WithAlpha(40), IsAntialias = true };
                using var rankBorder = new SKPaint { Color = rankColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                using var rkPaint = new SKPaint { Typeface = FontBold, TextSize = 16f, Color = rankColor, IsAntialias = true, TextAlign = SKTextAlign.Center };
                canvas.DrawCircle(72f, y + 23f, 16f, rankBg);
                canvas.DrawCircle(72f, y + 23f, 16f, rankBorder);
                canvas.DrawText($"{row.Rank}", 72f, y + 29f, rkPaint);

                var hasAvatar = false;
                if (avatarMap.TryGetValue(row.UserId, out var avBytes) && avBytes is { Length: > 0 })
                {
                    using var bmp = SKBitmap.Decode(avBytes);
                    if (bmp is not null)
                    {
                        canvas.Save();
                        using var avClip = new SKPath();
                        avClip.AddCircle(118f, y + 23f, 16f);
                        canvas.ClipPath(avClip, SKClipOperation.Intersect, true);
                        canvas.DrawBitmap(bmp, new SKRect(102f, y + 7f, 134f, y + 39f));
                        canvas.Restore();

                        using var avRing = new SKPaint { Color = rankColor.WithAlpha(180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                        canvas.DrawCircle(118f, y + 23f, 16.5f, avRing);
                        hasAvatar = true;
                    }
                }

                if (!hasAvatar)
                {
                    using var avBg = new SKPaint { Color = new SKColor(24, 30, 48), IsAntialias = true };
                    canvas.DrawCircle(118f, y + 23f, 16f, avBg);
                    using var avRing = new SKPaint { Color = rankColor.WithAlpha(180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                    canvas.DrawCircle(118f, y + 23f, 16.5f, avRing);

                    var cleanName = row.DisplayName.StartsWith("ID ") ? row.DisplayName[3..] : row.DisplayName;
                    var initial = string.IsNullOrWhiteSpace(cleanName) ? "?" : cleanName[0].ToString().ToUpperInvariant();
                    using var initialPaint = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = new SKColor(203, 213, 225), IsAntialias = true, TextAlign = SKTextAlign.Center };
                    canvas.DrawText(initial, 118f, y + 28f, initialPaint);
                }

                using var userPaint = new SKPaint { Typeface = FontBold, TextSize = 16f, Color = SKColors.White, IsAntialias = true };
                canvas.DrawText(ClipText(row.DisplayName, userPaint, 480f), 150f, y + 29f, userPaint);

                using var valPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 16f,
                    Color = new SKColor(52, 211, 153),
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Right
                };
                canvas.DrawText(row.FormattedValue, w - 64f, y + 29f, valPaint);
            }

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderClanCardAsync(
        ClanEntity clan,
        string leaderName,
        int memberCount,
        string currencySymbol,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            const int w = 1024;
            const int h = 380;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, "default.png");

            var accent = SKColor.TryParse(clan.ColorHex, out var parsed) ? parsed : AccentCyan;
            var panelRect = new SKRoundRect(new SKRect(36, 32, w - 36, h - 32), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
            canvas.DrawRoundRect(panelRect, panelPaint);
            canvas.DrawRoundRect(panelRect, borderPaint);

            DrawGlowingPillTab(canvas, 36f, (h - 44f) / 2f, 5f, 44f, accent);
            DrawGlowingPillTab(canvas, w - 41f, (h - 44f) / 2f, 5f, 44f, accent);

            using var badgeBg = new SKPaint { Color = accent.WithAlpha(45), IsAntialias = true };
            canvas.DrawCircle(130f, 140f, 62f, badgeBg);
            canvas.DrawCircle(130f, 140f, 62f, borderPaint);

            using var tagPaint = new SKPaint { Typeface = FontBold, TextSize = 28f, Color = accent, IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText($"[{clan.Tag}]", 130f, 150f, tagPaint);

            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 32f, Color = SKColors.White, IsAntialias = true };
            using var descPaint = new SKPaint { Typeface = FontRegular, TextSize = 16f, Color = new SKColor(209, 213, 219), IsAntialias = true };

            canvas.DrawText(ClipText(clan.Name, titlePaint, 660f), 220f, 96f, titlePaint);
            canvas.DrawText(ClipText(clan.Description, descPaint, 680f), 220f, 130f, descPaint);

            const float gap = 18f;
            var pillY = 230f;
            var pillW = (w - 112f - 2 * gap) / 3f;
            var pill1X = 56f;
            var pill2X = pill1X + pillW + gap;
            var pill3X = pill2X + pillW + gap;

            using var pillTitle = new SKPaint { Typeface = FontRegular, TextSize = 13f, Color = new SKColor(148, 163, 184), IsAntialias = true };
            using var pillVal = new SKPaint { Typeface = FontBold, TextSize = 17f, Color = SKColors.White, IsAntialias = true };

            DrawStatPill(canvas, pill1X, pillY, pillW, 58f);
            canvas.DrawText("Лидер клана", pill1X + 18f, pillY + 28f, pillTitle);
            canvas.DrawText(ClipText(leaderName, pillVal, pillW - 36f), pill1X + 18f, pillY + 49f, pillVal);

            DrawStatPill(canvas, pill2X, pillY, pillW, 58f);
            canvas.DrawText("Участники", pill2X + 18f, pillY + 28f, pillTitle);
            canvas.DrawText($"{memberCount} / {clan.MemberLimit} чел.", pill2X + 18f, pillY + 49f, pillVal);

            DrawStatPill(canvas, pill3X, pillY, pillW, 58f);
            canvas.DrawText("Казна и Слава", pill3X + 18f, pillY + 28f, pillTitle);
            canvas.DrawText(ClipText($"{clan.Treasury:N0} {currencySymbol} • {clan.Points:N0} pts", pillVal, pillW - 36f), pill3X + 18f, pillY + 49f, pillVal);

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderFamilyCardAsync(
        MarriageEntity marriage,
        FamilyProfileEntity family,
        string partner1Name,
        string? partner1AvatarUrl,
        string partner2Name,
        string? partner2AvatarUrl,
        IReadOnlyList<string> childrenNames,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var av1 = !string.IsNullOrWhiteSpace(partner1AvatarUrl) ? await GetAvatarBytesAsync(partner1AvatarUrl, ct) : null;
        var av2 = !string.IsNullOrWhiteSpace(partner2AvatarUrl) ? await GetAvatarBytesAsync(partner2AvatarUrl, ct) : null;

        return await Task.Run(() =>
        {
            const int w = 1024;
            const int h = 420;
            var roseAccent = new SKColor(244, 114, 182);

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, "nebula.png");

            var panelRect = new SKRoundRect(new SKRect(32, 28, w - 32, h - 28), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = roseAccent.WithAlpha(110), Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
            canvas.DrawRoundRect(panelRect, panelPaint);
            canvas.DrawRoundRect(panelRect, borderPaint);

            DrawGlowingPillTab(canvas, 32f, (h - 48f) / 2f, 5f, 48f, roseAccent);
            DrawGlowingPillTab(canvas, w - 37f, (h - 48f) / 2f, 5f, 48f, roseAccent);

            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 26f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = new SKColor(203, 213, 225), IsAntialias = true, TextAlign = SKTextAlign.Center };

            var daysTogether = Math.Max(1, (int)(DateTimeOffset.UtcNow - marriage.MarriedAt).TotalDays + 1);
            canvas.DrawText(ClipText(family.FamilyName, titlePaint, 640f), w / 2f, 68f, titlePaint);
            canvas.DrawText($"Союз с {marriage.MarriedAt:dd.MM.yyyy} ({daysTogether} дн.)  •  Уровень любви: {family.LoveLevel} ({family.LoveXp:N0} XP)", w / 2f, 94f, subPaint);

            DrawCircularAvatarSimple(canvas, 290f, 175f, 54f, av1, roseAccent);
            DrawCircularAvatarSimple(canvas, 734f, 175f, 54f, av2, AccentCyan);

            using var heartBg = new SKPaint { Color = roseAccent.WithAlpha(40), IsAntialias = true };
            canvas.DrawCircle(w / 2f, 175f, 32f, heartBg);
            canvas.DrawCircle(w / 2f, 175f, 32f, borderPaint);
            DrawStarIcon(canvas, w / 2f, 175f, 15f, roseAccent);

            using var pNamePaint = new SKPaint { Typeface = FontBold, TextSize = 18f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText(ClipText(partner1Name, pNamePaint, 230f), 290f, 254f, pNamePaint);
            canvas.DrawText(ClipText(partner2Name, pNamePaint, 230f), 734f, 254f, pNamePaint);

            const float gap = 18f;
            var pillY = 290f;
            var pillW = (w - 112f - 2 * gap) / 3f;
            var pill1X = 56f;
            var pill2X = pill1X + pillW + gap;
            var pill3X = pill2X + pillW + gap;

            using var pillLbl = new SKPaint { Typeface = FontRegular, TextSize = 13f, Color = new SKColor(148, 163, 184), IsAntialias = true };
            using var pillVal = new SKPaint { Typeface = FontBold, TextSize = 17f, Color = SKColors.White, IsAntialias = true };

            DrawStatPill(canvas, pill1X, pillY, pillW, 64f);
            DrawWalletIcon(canvas, pill1X + 18f, pillY + 21f, 22f, roseAccent);
            canvas.DrawText("Семейный банк", pill1X + 52f, pillY + 28f, pillLbl);
            canvas.DrawText($"{family.FamilyBalance:N0} {currencySymbol}", pill1X + 52f, pillY + 51f, pillVal);

            DrawStatPill(canvas, pill2X, pillY, pillW, 64f);
            DrawStarIcon(canvas, pill2X + 28f, pillY + 32f, 12f, roseAccent);
            canvas.DrawText("Гармония пары", pill2X + 52f, pillY + 28f, pillLbl);
            canvas.DrawText($"Ур. {family.LoveLevel} ({family.LoveXp % 250}/250 XP)", pill2X + 52f, pillY + 51f, pillVal);

            DrawStatPill(canvas, pill3X, pillY, pillW, 64f);
            DrawTrophyIcon(canvas, pill3X + 28f, pillY + 32f, 20f, AccentCyan);
            canvas.DrawText($"Дети в семье ({childrenNames.Count})", pill3X + 52f, pillY + 28f, pillLbl);
            var kidsText = childrenNames.Count == 0 ? "Нет усыновлённых" : string.Join(", ", childrenNames);
            canvas.DrawText(ClipText(kidsText, pillVal, pillW - 64f), pill3X + 52f, pillY + 51f, pillVal);

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderDuelCardAsync(
        DuelSessionEntity duel,
        string challengerName,
        string? challengerAvatarUrl,
        string opponentName,
        string? opponentAvatarUrl,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var av1 = !string.IsNullOrWhiteSpace(challengerAvatarUrl) ? await GetAvatarBytesAsync(challengerAvatarUrl, ct) : null;
        var av2 = !string.IsNullOrWhiteSpace(opponentAvatarUrl) ? await GetAvatarBytesAsync(opponentAvatarUrl, ct) : null;

        return await Task.Run(() =>
        {
            const int w = 1024;
            const int h = 380;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(15, 17, 26));

            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(new SKRect(0, 0, w, h), 26, 26);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);

            DrawBackground(canvas, w, h, "crimson.png");

            var panelRect = new SKRoundRect(new SKRect(32, 26, w - 32, h - 26), 22, 22);
            using var panelPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
            using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(panelRect, panelPaint);
            canvas.DrawRoundRect(panelRect, borderPaint);

            DrawGlowingPillTab(canvas, 32f, (h - 44f) / 2f, 5f, 44f, AccentCyan);
            DrawGlowingPillTab(canvas, w - 37f, (h - 44f) / 2f, 5f, 44f, new SKColor(244, 63, 94));

            using var headerPaint = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = new SKColor(148, 163, 184), IsAntialias = true, TextAlign = SKTextAlign.Center };

            var potText = duel.Bet > 0 ? $"Банк: {duel.Bet * 2:N0} {currencySymbol}" : "Товарищеский бой";
            canvas.DrawText($"PVP АРЕНА KRLZ  •  РАУНД #{duel.RoundNum}", w / 2f, 64f, headerPaint);
            canvas.DrawText(potText, w / 2f, 88f, subPaint);

            DrawCircularAvatarSimple(canvas, 140f, 175f, 52f, av1, AccentCyan);
            DrawCircularAvatarSimple(canvas, w - 140f, 175f, 52f, av2, new SKColor(244, 63, 94));

            using var nameLeft = new SKPaint { Typeface = FontBold, TextSize = 20f, Color = SKColors.White, IsAntialias = true };
            using var nameRight = new SKPaint { Typeface = FontBold, TextSize = 20f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Right };
            canvas.DrawText(ClipText(challengerName, nameLeft, 240f), 215f, 145f, nameLeft);
            canvas.DrawText(ClipText(opponentName, nameRight, 240f), w - 215f, 145f, nameRight);

            DrawHpBar(canvas, 215f, 162f, 240f, 24f, duel.ChallengerHp, AccentCyan);
            DrawHpBar(canvas, w - 455f, 162f, 240f, 24f, duel.OpponentHp, new SKColor(244, 63, 94));

            using var vsPaint = new SKPaint { Typeface = FontBold, TextSize = 28f, Color = new SKColor(251, 191, 36), IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText("VS", w / 2f, 182f, vsPaint);

            DrawStatPill(canvas, 64f, 268f, w - 128f, 56f);
            using var logPaint = new SKPaint { Typeface = FontBold, TextSize = 15.5f, Color = new SKColor(226, 232, 240), IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText(ClipText(duel.LastLog, logPaint, w - 160f), w / 2f, 302f, logPaint);

            canvas.Restore();

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    private static void DrawHpBar(SKCanvas canvas, float x, float y, float w, float h, int hp, SKColor color)
    {
        var clamped = Math.Clamp(hp, 0, 100);
        var ratio = clamped / 100f;

        using var track = new SKPaint { Color = new SKColor(255, 255, 255, 18), IsAntialias = true };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x, y, x + w, y + h), 10, 10), track);

        if (clamped > 0)
        {
            var fillW = Math.Max(16f, w * ratio);
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(x, y, x + fillW, y + h), 10, 10), fill);
        }

        using var txt = new SKPaint { Typeface = FontBold, TextSize = 13f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
        canvas.DrawText($"{clamped} / 100 HP", x + w / 2f, y + 17f, txt);
    }

    private static void DrawCircularAvatarSimple(SKCanvas canvas, float cx, float cy, float r, byte[]? bytes, SKColor ringColor)
    {
        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(cx, cy, r);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            if (bytes is { Length: > 0 })
            {
                using var bmp = SKBitmap.Decode(bytes);
                if (bmp is not null)
                    canvas.DrawBitmap(bmp, new SKRect(cx - r, cy - r, cx + r, cy + r));
            }
            else
            {
                using var bg = new SKPaint { Color = new SKColor(30, 41, 59), IsAntialias = true };
                canvas.DrawCircle(cx, cy, r, bg);
            }
        }
        canvas.Restore();

        using var ring = new SKPaint { Color = ringColor, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r + 1.5f, ring);
    }

    private static SKColor ResolveAccentColor(string? customAccentHex, string? roleColorHex)
    {
        if (!string.IsNullOrWhiteSpace(customAccentHex) && SKColor.TryParse(customAccentHex, out var c1))
            return c1;
        if (!string.IsNullOrWhiteSpace(roleColorHex) && SKColor.TryParse(roleColorHex, out var c2))
            return c2;
        return AccentCyan;
    }

    private static void DrawBackground(SKCanvas canvas, int width, int height, string selectedBackground)
    {
        var safeFile = Path.GetFileName(string.IsNullOrWhiteSpace(selectedBackground) ? "default.png" : selectedBackground);
        var bgPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", safeFile);

        var drewBitmap = false;
        if (File.Exists(bgPath))
        {
            if (!BackgroundCache.TryGetValue(bgPath, out var bgBitmap))
            {
                var decoded = SKBitmap.Decode(bgPath);
                if (decoded is not null)
                {
                    decoded.SetImmutable();
                    bgBitmap = BackgroundCache.GetOrAdd(bgPath, decoded);
                }
            }

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
                    [new SKColor(16, 24, 40), new SKColor(10, 14, 26)],
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
                    new SKColor(10, 15, 26, 60),
                    new SKColor(10, 15, 26, 175),
                    new SKColor(10, 15, 26, 235)
                ],
                [0f, 0.35f, 0.65f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), vignettePaint);
    }

    private static void DrawCenterAvatarAndName(
        SKCanvas canvas,
        SocketGuildUser member,
        byte[]? avatarBytes,
        UserProfileEntity profile,
        CustomRoleEntity? customRole,
        UserPetEntity? pet,
        SKColor accent)
    {
        const float centerX = 512f;
        const float centerY = 110f;
        const float radius = 66f;

        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 8f,
            Color = accent.WithAlpha(90),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f)
        };
        canvas.DrawCircle(centerX, centerY, radius + 2f, glowPaint);

        canvas.Save();
        using (var clipPath = new SKPath())
        {
            clipPath.AddCircle(centerX, centerY, radius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            if (avatarBytes is { Length: > 0 })
            {
                using var avatarBmp = SKBitmap.Decode(avatarBytes);
                if (avatarBmp is not null)
                {
                    var dest = new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius);
                    canvas.DrawBitmap(avatarBmp, dest);
                }
            }
            else
            {
                using var fallbackPaint = new SKPaint { Color = new SKColor(55, 65, 81), IsAntialias = true };
                canvas.DrawCircle(centerX, centerY, radius, fallbackPaint);
            }
        }
        canvas.Restore();

        using var ringPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            Color = accent
        };
        canvas.DrawCircle(centerX, centerY, radius + 2f, ringPaint);

        var statusColor = member.Status switch
        {
            UserStatus.Online => new SKColor(35, 165, 90),
            UserStatus.Idle or UserStatus.AFK => new SKColor(240, 178, 50),
            UserStatus.DoNotDisturb => new SKColor(242, 63, 67),
            _ => new SKColor(128, 132, 142)
        };
        var statusX = centerX + radius * 0.72f;
        var statusY = centerY + radius * 0.72f;
        using var statusBg = new SKPaint { Color = new SKColor(15, 17, 26), IsAntialias = true };
        using var statusFg = new SKPaint { Color = statusColor, IsAntialias = true };
        canvas.DrawCircle(statusX, statusY, 15f, statusBg);
        canvas.DrawCircle(statusX, statusY, 10f, statusFg);

        using var namePaint = new SKPaint
        {
            Typeface = FontBold,
            TextSize = 25f,
            Color = SKColors.White,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        var displayName = ClipText(member.DisplayName, namePaint, 420f);
        var nameW = namePaint.MeasureText(displayName);
        const float nameY = 212f;
        canvas.DrawText(displayName, centerX, nameY, namePaint);

        DrawGlowingPillTab(canvas, centerX - nameW / 2f - 18f, nameY - 20f, 4f, 24f, accent);
        DrawGlowingPillTab(canvas, centerX + nameW / 2f + 14f, nameY - 20f, 4f, 24f, accent);

        var subParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Title))
            subParts.Add(profile.Title);
        if (customRole is not null && !string.IsNullOrWhiteSpace(customRole.Name))
            subParts.Add($"@{customRole.Name}");
        if (pet is not null)
            subParts.Add($"Питомец: {pet.Name} (Ур.{pet.Level})");
        if (!string.IsNullOrWhiteSpace(profile.Bio) && profile.Bio != "Участник сервера KRLZ")
            subParts.Add(profile.Bio);

        var subtitleLine = subParts.Count > 0 ? string.Join("  •  ", subParts) : profile.Bio;
        using var subPaint = new SKPaint
        {
            Typeface = FontRegular,
            TextSize = 13.5f,
            Color = new SKColor(148, 163, 184),
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText(ClipText(subtitleLine, subPaint, 840f), centerX, 238f, subPaint);
    }

    private static void DrawMainStatsPanel(SKCanvas canvas, UserProfileEntity profile, int rank, string currencySymbol, SKColor accent)
    {
        const float panelX = 48f;
        const float panelY = 258f;
        const float panelW = 928f;
        const float panelH = 152f;
        const float corner = 22f;

        var panelRect = new SKRoundRect(new SKRect(panelX, panelY, panelX + panelW, panelY + panelH), corner, corner);
        using var bgPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawRoundRect(panelRect, bgPaint);
        canvas.DrawRoundRect(panelRect, borderPaint);

        DrawGlowingPillTab(canvas, panelX, panelY + (panelH - 44f) / 2f, 5f, 44f, accent);
        DrawGlowingPillTab(canvas, panelX + panelW - 5f, panelY + (panelH - 44f) / 2f, 5f, 44f, accent);

        const float pillW = 356f;
        const float pillH = 48f;

        using var labelPaint = new SKPaint { Typeface = FontRegular, TextSize = 16f, Color = new SKColor(226, 232, 240), IsAntialias = true };
        using var valPaint = new SKPaint { Typeface = FontBold, TextSize = 17f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Right };

        var p1Y = panelY + 20f;
        DrawStatPill(canvas, 76f, p1Y, pillW, pillH);
        DrawWalletIcon(canvas, 94f, p1Y + 14f, 20f, accent);
        canvas.DrawText("Баланс", 124f, p1Y + 31f, labelPaint);
        canvas.DrawText($"{profile.Balance:N0} {currencySymbol}", 76f + pillW - 20f, p1Y + 31f, valPaint);

        var p2Y = panelY + 84f;
        DrawStatPill(canvas, 76f, p2Y, pillW, pillH);
        DrawChatBubbleIcon(canvas, 94f, p2Y + 14f, 20f, accent);
        canvas.DrawText("Сообщения", 124f, p2Y + 31f, labelPaint);
        canvas.DrawText($"{profile.Messages:N0}", 76f + pillW - 20f, p2Y + 31f, valPaint);

        using var lvlTitle = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = accent, IsAntialias = true, TextAlign = SKTextAlign.Center };
        using var lvlNum = new SKPaint { Typeface = FontBold, TextSize = 46f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
        canvas.DrawText("УРОВЕНЬ", 512f, panelY + 54f, lvlTitle);
        canvas.DrawText(profile.Level.ToString(), 512f, panelY + 112f, lvlNum);

        DrawStatPill(canvas, 592f, p1Y, pillW, pillH);
        DrawClockIcon(canvas, 610f, p1Y + 14f, 20f, accent);
        canvas.DrawText("Онлайн", 640f, p1Y + 31f, labelPaint);
        canvas.DrawText(TimeParser.FormatVoiceSeconds(profile.VoiceSeconds), 592f + pillW - 20f, p1Y + 31f, valPaint);

        DrawStatPill(canvas, 592f, p2Y, pillW, pillH);
        DrawChartIcon(canvas, 610f, p2Y + 14f, 20f, accent);
        canvas.DrawText("Ранг", 640f, p2Y + 31f, labelPaint);
        canvas.DrawText($"#{rank}", 592f + pillW - 20f, p2Y + 31f, valPaint);
    }

    private static void DrawBottomCards(
        SKCanvas canvas,
        string? partnerName,
        byte[]? partnerAvatarBytes,
        ClanEntity? clan,
        IReadOnlyList<AchievementEntity>? equippedBadges,
        long reputation,
        int unlockedAchievementsCount,
        int totalAchievementsCount,
        SKColor accent)
    {
        const float y = 434f;
        const float h = 100f;
        const float w = 296f;
        const float corner = 18f;

        using var bgPaint = new SKPaint { Color = GlassFill, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = GlassBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        using var lblPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = new SKColor(148, 163, 184), IsAntialias = true };
        using var valPaint = new SKPaint { Typeface = FontBold, TextSize = 18f, Color = SKColors.White, IsAntialias = true };

        var c1Rect = new SKRoundRect(new SKRect(48f, y, 48f + w, y + h), corner, corner);
        canvas.DrawRoundRect(c1Rect, bgPaint);
        canvas.DrawRoundRect(c1Rect, borderPaint);

        const float circleR = 24f;
        const float c1CenterY = y + h / 2f;
        const float c1CenterX = 48f + 42f;

        if (partnerAvatarBytes is { Length: > 0 })
        {
            canvas.Save();
            using var clip = new SKPath();
            clip.AddCircle(c1CenterX, c1CenterY, circleR);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            using var pBmp = SKBitmap.Decode(partnerAvatarBytes);
            if (pBmp is not null)
            {
                canvas.DrawBitmap(pBmp, new SKRect(c1CenterX - circleR, c1CenterY - circleR, c1CenterX + circleR, c1CenterY + circleR));
            }
            canvas.Restore();
        }
        else
        {
            using var circleBg = new SKPaint { Color = new SKColor(15, 23, 42), IsAntialias = true };
            canvas.DrawCircle(c1CenterX, c1CenterY, circleR, circleBg);
        }

        using var ringPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, IsAntialias = true };
        canvas.DrawCircle(c1CenterX, c1CenterY, circleR, ringPaint);

        canvas.DrawText("Пара:", 48f + 80f, y + 42f, lblPaint);
        var partnerDisplay = string.IsNullOrWhiteSpace(partnerName) ? "нет" : partnerName;
        canvas.DrawText(ClipText(partnerDisplay, valPaint, 195f), 48f + 80f, y + 68f, valPaint);

        const float c2X = 364f;
        var c2Rect = new SKRoundRect(new SKRect(c2X, y, c2X + w, y + h), corner, corner);
        canvas.DrawRoundRect(c2Rect, bgPaint);
        canvas.DrawRoundRect(c2Rect, borderPaint);

        const float c2CenterX = c2X + 42f;
        using var c2Bg = new SKPaint { Color = new SKColor(15, 23, 42), IsAntialias = true };
        canvas.DrawCircle(c2CenterX, c1CenterY, circleR, c2Bg);
        canvas.DrawCircle(c2CenterX, c1CenterY, circleR, ringPaint);
        DrawStarIcon(canvas, c2CenterX, c1CenterY, 11f, accent);

        var repText = reputation > 0 ? $"+{reputation}" : reputation.ToString();
        var clanLabel = clan is not null ? "Клан • Репутация:" : "Репутация:";
        var clanVal = clan is not null ? $"[{clan.Tag}] • {repText}" : repText;
        canvas.DrawText(clanLabel, c2X + 80f, y + 42f, lblPaint);
        canvas.DrawText(ClipText(clanVal, valPaint, 195f), c2X + 80f, y + 68f, valPaint);

        const float c3X = 680f;
        var c3Rect = new SKRoundRect(new SKRect(c3X, y, c3X + w, y + h), corner, corner);
        canvas.DrawRoundRect(c3Rect, bgPaint);
        canvas.DrawRoundRect(c3Rect, borderPaint);

        const float c3CenterX = c3X + 42f;
        canvas.DrawCircle(c3CenterX, c1CenterY, circleR, c2Bg);
        canvas.DrawCircle(c3CenterX, c1CenterY, circleR, ringPaint);
        DrawTrophyIcon(canvas, c3CenterX, c1CenterY, 20f, accent);

        var achText = totalAchievementsCount > 0
            ? $"{unlockedAchievementsCount} / {totalAchievementsCount}"
            : (equippedBadges?.Count ?? 0).ToString();

        canvas.DrawText("Достижения:", c3X + 80f, y + 42f, lblPaint);
        canvas.DrawText(achText, c3X + 80f, y + 68f, valPaint);
    }

    private static void DrawStatPill(SKCanvas canvas, float x, float y, float w, float h)
    {
        var rect = new SKRoundRect(new SKRect(x, y, x + w, y + h), 14, 14);
        using var paint = new SKPaint { Color = PillFill, IsAntialias = true };
        canvas.DrawRoundRect(rect, paint);
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

    private static void DrawWalletIcon(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        var body = new SKRoundRect(new SKRect(x, y + size * 0.15f, x + size, y + size * 0.85f), size * 0.18f, size * 0.18f);
        canvas.DrawRoundRect(body, paint);

        using var darkPaint = new SKPaint { Color = new SKColor(16, 22, 34), Style = SKPaintStyle.Fill, IsAntialias = true };
        var flap = new SKRoundRect(new SKRect(x + size * 0.52f, y + size * 0.32f, x + size, y + size * 0.68f), size * 0.12f, size * 0.12f);
        canvas.DrawRoundRect(flap, darkPaint);

        using var dotPaint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawCircle(x + size * 0.72f, y + size * 0.5f, size * 0.08f, dotPaint);
    }

    private static void DrawChatBubbleIcon(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        path.AddRoundRect(new SKRect(x, y + size * 0.1f, x + size, y + size * 0.78f), size * 0.2f, size * 0.2f);
        path.MoveTo(x + size * 0.25f, y + size * 0.75f);
        path.LineTo(x + size * 0.12f, y + size * 0.96f);
        path.LineTo(x + size * 0.45f, y + size * 0.75f);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawClockIcon(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        var cx = x + size / 2f;
        var cy = y + size / 2f;
        var r = size * 0.42f;

        using var stroke = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.14f,
            IsAntialias = true
        };
        canvas.DrawCircle(cx, cy, r, stroke);

        using var hand = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = size * 0.12f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawLine(cx, cy, cx, cy - r * 0.55f, hand);
        canvas.DrawLine(cx, cy, cx + r * 0.45f, cy, hand);
    }

    private static void DrawChartIcon(SKCanvas canvas, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        var barW = size * 0.22f;
        var spacing = size * 0.12f;
        var corner = size * 0.08f;

        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x, y + size * 0.55f, x + barW, y + size * 0.95f), corner, corner), paint);
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x + barW + spacing, y + size * 0.3f, x + 2 * barW + spacing, y + size * 0.95f), corner, corner), paint);
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(x + 2 * (barW + spacing), y + size * 0.05f, x + 3 * barW + 2 * spacing, y + size * 0.95f), corner, corner), paint);
    }

    private static void DrawStarIcon(SKCanvas canvas, float cx, float cy, float radius, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var path = new SKPath();
        var innerRadius = radius * 0.45f;
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? radius : innerRadius;
            var angle = i * MathF.PI / 5f - MathF.PI / 2f;
            var px = cx + MathF.Cos(angle) * r;
            var py = cy + MathF.Sin(angle) * r;
            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawTrophyIcon(SKCanvas canvas, float cx, float cy, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var stroke = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.09f, IsAntialias = true };

        using var cup = new SKPath();
        var topW = size * 0.36f;
        var botW = size * 0.18f;
        var topY = cy - size * 0.36f;
        var midY = cy + size * 0.05f;
        cup.MoveTo(cx - topW, topY);
        cup.LineTo(cx + topW, topY);
        cup.LineTo(cx + botW, midY);
        cup.LineTo(cx - botW, midY);
        cup.Close();
        canvas.DrawPath(cup, paint);

        canvas.DrawRect(new SKRect(cx - size * 0.08f, midY, cx + size * 0.08f, midY + size * 0.22f), paint);

        var baseW = size * 0.35f;
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(cx - baseW, midY + size * 0.2f, cx + baseW, midY + size * 0.35f), size * 0.05f, size * 0.05f), paint);

        canvas.DrawArc(new SKRect(cx - topW - size * 0.22f, topY - size * 0.02f, cx - topW + size * 0.08f, midY - size * 0.05f), 90f, 180f, false, stroke);
        canvas.DrawArc(new SKRect(cx + topW - size * 0.08f, topY - size * 0.02f, cx + topW + size * 0.22f, midY - size * 0.05f), -90f, 180f, false, stroke);
    }

    private static void DrawAvatarWithStatus(
        SKCanvas canvas,
        float centerX,
        float centerY,
        float radius,
        byte[]? avatarBytes,
        UserStatus status)
    {
        using var ringPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            Color = AccentCyan
        };

        canvas.Save();
        using (var clipPath = new SKPath())
        {
            clipPath.AddCircle(centerX, centerY, radius);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            if (avatarBytes is { Length: > 0 })
            {
                using var avatarBmp = SKBitmap.Decode(avatarBytes);
                if (avatarBmp is not null)
                {
                    var dest = new SKRect(centerX - radius, centerY - radius, centerX + radius, centerY + radius);
                    canvas.DrawBitmap(avatarBmp, dest);
                }
            }
            else
            {
                using var fallbackPaint = new SKPaint { Color = new SKColor(55, 65, 81), IsAntialias = true };
                canvas.DrawCircle(centerX, centerY, radius, fallbackPaint);
            }
        }
        canvas.Restore();

        canvas.DrawCircle(centerX, centerY, radius + 2f, ringPaint);

        var statusColor = status switch
        {
            UserStatus.Online => new SKColor(35, 165, 90),
            UserStatus.Idle or UserStatus.AFK => new SKColor(240, 178, 50),
            UserStatus.DoNotDisturb => new SKColor(242, 63, 67),
            _ => new SKColor(128, 132, 142)
        };

        var offset = radius * 0.7f;
        var statusX = centerX + offset;
        var statusY = centerY + offset;

        using var statusBg = new SKPaint { Color = new SKColor(15, 17, 26), IsAntialias = true };
        using var statusFg = new SKPaint { Color = statusColor, IsAntialias = true };
        canvas.DrawCircle(statusX, statusY, 15f, statusBg);
        canvas.DrawCircle(statusX, statusY, 10f, statusFg);
    }

    private static void DrawXpProgressBar(
        SKCanvas canvas,
        UserProfileEntity profile,
        float barLeft,
        float barTop,
        float barWidth,
        float barHeight)
    {
        var currentLevelXp = UserRepository.XpForLevel(profile.Level);
        var nextLevelXp = UserRepository.XpForLevel(profile.Level + 1);
        var span = Math.Max(1L, nextLevelXp - currentLevelXp);
        var progress = Math.Clamp((float)(profile.Xp - currentLevelXp) / span, 0f, 1f);

        using var trackPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 14),
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(barLeft, barTop, barLeft + barWidth, barTop + barHeight), 12, 12), trackPaint);

        var fillWidth = Math.Max(24f, barWidth * progress);
        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(barLeft, barTop),
                new SKPoint(barLeft + fillWidth, barTop),
                [AccentCyan, AccentBlue],
                (float[]?)null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(new SKRoundRect(new SKRect(barLeft, barTop, barLeft + fillWidth, barTop + barHeight), 12, 12), fillPaint);

        using var xpTextPaint = new SKPaint
        {
            Typeface = FontBold,
            TextSize = 13f,
            Color = SKColors.White,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        var xpStr = string.Create(CultureInfo.InvariantCulture, $"{profile.Xp:N0} / {nextLevelXp:N0} XP ({progress * 100:F0}%)");
        canvas.DrawText(xpStr, barLeft + barWidth / 2f, barTop + barHeight * 0.72f, xpTextPaint);
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

    public async Task<byte[]?> GetAvatarBytesAsync(string url, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_avatarCache.TryGetValue(url, out var cached) && cached.ExpiresAt > now)
            return cached.Bytes;

        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url, ct);
            _avatarCache[url] = (bytes, now.AddMinutes(15));
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
