using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using SkiaSharp;

namespace Bot.Services;

public sealed class ProgressionService(
    ProgressionRepository progRepo,
    UserRepository userRepo,
    InventoryService inventoryService,
    SettingsProvider settingsProvider)
{
    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);
    private static readonly SKTypeface FontRegular = LoadTypeface("Inter-Regular.ttf", SKFontStyle.Normal);

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, style);
    }

    public async Task TrackActivityAsync(
        ulong guildId,
        ulong userId,
        string objectiveType,
        long objectiveDelta,
        int battlePassXpDelta,
        CancellationToken ct = default)
    {
        if (objectiveDelta > 0 && !string.IsNullOrWhiteSpace(objectiveType))
        {
            await progRepo.IncrementQuestObjectiveAsync(guildId, userId, objectiveType, objectiveDelta, ct);
        }

        if (battlePassXpDelta > 0)
        {
            var bpCfg = settingsProvider.Current.BattlePass;
            var season = await progRepo.GetOrCreateActiveSeasonAsync(
                guildId, bpCfg.DefaultSeasonDays, bpCfg.MaxLevel, bpCfg.XpPerLevel, bpCfg.PremiumPrice, ct);

            await progRepo.AddBattlePassXpAsync(
                guildId, season.Id, userId, battlePassXpDelta, season.MaxLevel, season.XpPerLevel, ct);
        }
    }

    public async Task<(int Count, long TotalCoins, long TotalXp, int TotalBpXp)> ClaimCompletedQuestsAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        var claimed = await progRepo.ClaimReadyQuestsAsync(guildId, userId, ct);
        if (claimed.Count == 0)
            return (0, 0, 0, 0);

        var totalCoins = claimed.Sum(x => x.RewardCoins);
        var totalXp = claimed.Sum(x => x.RewardXp);
        var totalBpXp = claimed.Sum(x => x.RewardBpXp);

        if (totalCoins > 0)
        {
            await userRepo.TryModifyBalanceAsync(
                guildId, userId, totalCoins, "quest_reward", $"Награда за выполнение квестов ({claimed.Count} шт.)", ct);
        }

        if (totalXp > 0)
        {
            await userRepo.AddMessageProgressAsync(guildId, userId, totalXp, 0, ct);
        }

        if (totalBpXp > 0)
        {
            await TrackActivityAsync(guildId, userId, string.Empty, 0, totalBpXp, ct);
        }

        return (claimed.Count, totalCoins, totalXp, totalBpXp);
    }

    public async Task<(int ClaimedNodesCount, List<string> RewardSummaries)> ClaimBattlePassRewardsAsync(
        ulong guildId,
        ulong userId,
        CancellationToken ct = default)
    {
        var bpCfg = settingsProvider.Current.BattlePass;
        var season = await progRepo.GetOrCreateActiveSeasonAsync(
            guildId, bpCfg.DefaultSeasonDays, bpCfg.MaxLevel, bpCfg.XpPerLevel, bpCfg.PremiumPrice, ct);
        var prog = await progRepo.GetOrCreateUserProgressAsync(guildId, season.Id, userId, ct);

        var nodes = JsonSerializer.Deserialize<List<BattlePassRewardNodeDto>>(season.RewardsJson) ?? [];
        var claimedFree = ParseLevelSet(prog.ClaimedFreeLevels);
        var claimedPrem = ParseLevelSet(prog.ClaimedPremiumLevels);

        var summaries = new List<string>();
        long totalCoins = 0;

        foreach (var node in nodes.Where(n => n.Level <= prog.Level).OrderBy(n => n.Level))
        {
            if (node.Track == "free" && !claimedFree.Contains(node.Level))
            {
                claimedFree.Add(node.Level);
                totalCoins += await GrantNodeRewardAsync(guildId, userId, node, summaries, ct);
            }
            else if (node.Track == "premium" && prog.IsPremium && !claimedPrem.Contains(node.Level))
            {
                claimedPrem.Add(node.Level);
                totalCoins += await GrantNodeRewardAsync(guildId, userId, node, summaries, ct);
            }
        }

        if (summaries.Count == 0)
            return (0, []);

        if (totalCoins > 0)
        {
            await userRepo.TryModifyBalanceAsync(
                guildId, userId, totalCoins, "battlepass_reward", $"Монеты из наград Батлпасса ({season.Name})", ct);
        }

        await progRepo.SaveClaimedLevelsAsync(
            guildId,
            season.Id,
            userId,
            string.Join(",", claimedFree.OrderBy(x => x)),
            string.Join(",", claimedPrem.OrderBy(x => x)),
            ct);

        return (summaries.Count, summaries);
    }

    public async Task<byte[]> RenderBattlePassTrackImageAsync(
        BattlePassSeasonEntity season,
        BattlePassProgressEntity progress,
        string username,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            const int width = 940;
            const int height = 340;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(13, 15, 24));

            using var bgPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(width, height),
                    [new SKColor(17, 20, 34), new SKColor(32, 24, 56)],
                    (float[]?)null,
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(new SKRect(0, 0, width, height), bgPaint);

            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 24f, Color = SKColors.White, IsAntialias = true };
            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 15f, Color = new SKColor(165, 180, 252), IsAntialias = true };

            canvas.DrawText(season.Name, 28f, 42f, titlePaint);
            var statusTag = progress.IsPremium ? "ПРЕМИУМ АКТИВЕН" : "БЕСПЛАТНАЯ ВЕТКА";
            canvas.DrawText($"Игрок: @{username}  •  Уровень: {progress.Level}/{season.MaxLevel}  •  Опыт: {progress.Xp:N0} XP  •  [{statusTag}]", 28f, 68f, subPaint);

            var nodes = JsonSerializer.Deserialize<List<BattlePassRewardNodeDto>>(season.RewardsJson) ?? [];
            var claimedFree = ParseLevelSet(progress.ClaimedFreeLevels);
            var claimedPrem = ParseLevelSet(progress.ClaimedPremiumLevels);

            var startLvl = Math.Clamp(((progress.Level - 1) / 5) * 5 + 1, 1, Math.Max(1, season.MaxLevel - 4));
            const float startX = 140f;
            const float colW = 154f;

            using var rowLabelPaint = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = new SKColor(209, 213, 219), IsAntialias = true };
            using var premLabelPaint = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = new SKColor(250, 204, 21), IsAntialias = true };
            canvas.DrawText("FREE", 32f, 150f, rowLabelPaint);
            canvas.DrawText("PREMIUM", 32f, 255f, premLabelPaint);

            for (var i = 0; i < 5; i++)
            {
                var lvl = startLvl + i;
                var x = startX + i * colW;

                var unlocked = progress.Level >= lvl;
                using var headerPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 15f,
                    Color = unlocked ? new SKColor(129, 140, 248) : new SKColor(107, 114, 128),
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText($"УР. {lvl}", x + 65f, 98f, headerPaint);

                var freeNode = nodes.FirstOrDefault(n => n.Level == lvl && n.Track == "free");
                var premNode = nodes.FirstOrDefault(n => n.Level == lvl && n.Track == "premium");

                DrawTrackCell(canvas, x, 110f, 136f, 76f, freeNode?.Label ?? "-", unlocked, claimedFree.Contains(lvl), false);
                DrawTrackCell(canvas, x, 208f, 136f, 76f, premNode?.Label ?? "-", unlocked && progress.IsPremium, claimedPrem.Contains(lvl), true);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    private static void DrawTrackCell(
        SKCanvas canvas,
        float x,
        float y,
        float w,
        float h,
        string label,
        bool unlocked,
        bool claimed,
        bool isPremiumTrack)
    {
        var borderColor = claimed
            ? new SKColor(16, 185, 129)
            : unlocked
                ? isPremiumTrack ? new SKColor(250, 204, 21) : new SKColor(99, 102, 241)
                : new SKColor(55, 65, 81);

        using var fillPaint = new SKPaint { Color = new SKColor(24, 28, 44, 225), IsAntialias = true };
        using var strokePaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };

        var rect = new SKRoundRect(new SKRect(x, y, x + w, y + h), 12, 12);
        canvas.DrawRoundRect(rect, fillPaint);
        canvas.DrawRoundRect(rect, strokePaint);

        using var textPaint = new SKPaint
        {
            Typeface = FontBold,
            TextSize = 12f,
            Color = SKColors.White,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        var shortLabel = label.Length > 18 ? label[..17] + "…" : label;
        canvas.DrawText(shortLabel, x + w / 2f, y + 34f, textPaint);

        var statusStr = claimed ? "ПОЛУЧЕНО" : unlocked ? "ДОСТУПНО" : "ЗАКРЫТО";
        using var statusPaint = new SKPaint
        {
            Typeface = FontRegular,
            TextSize = 11f,
            Color = borderColor,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText(statusStr, x + w / 2f, y + 58f, statusPaint);
    }

    private async Task<long> GrantNodeRewardAsync(
        ulong guildId,
        ulong userId,
        BattlePassRewardNodeDto node,
        List<string> summaries,
        CancellationToken ct)
    {
        var trackTag = node.Track == "premium" ? "Премиум" : "Бесплатно";
        summaries.Add($"• [Ур. {node.Level} • {trackTag}] **{node.Label}**");

        if (node.RewardType == "coins")
            return node.CoinsAmount;

        if (node.RewardType == "background" && !string.IsNullOrWhiteSpace(node.Payload))
        {
            await userRepo.UpdateBackgroundAsync(guildId, userId, node.Payload, ct);
        }
        else if (node.RewardType == "title" && !string.IsNullOrWhiteSpace(node.Payload))
        {
            await userRepo.UpdateTitleAsync(guildId, userId, node.Payload, ct);
        }
        else if (node.RewardType == "item" && !string.IsNullOrWhiteSpace(node.Payload))
        {
            await inventoryService.GiveItemByCodeAsync(guildId, userId, node.Payload, Math.Max(1, node.ItemQuantity), ct);
        }

        return 0;
    }

    private static HashSet<int> ParseLevelSet(string raw)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var lvl))
                set.Add(lvl);
        }

        return set;
    }
}
