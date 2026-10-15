using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class AchievementService(
    AchievementRepository achievementRepo,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds,
    ILogger<AchievementService> logger)
{
    public async Task TrackEventAsync(
        SocketGuild guild,
        ulong userId,
        string eventType,
        long value,
        bool isAbsoluteValue = false,
        ISocketMessageChannel? notifyChannel = null,
        CancellationToken ct = default)
    {
        try
        {
            var unlocked = await achievementRepo.ProcessProgressAsync(guild.Id, userId, eventType, value, isAbsoluteValue, ct);
            if (unlocked.Count == 0)
                return;

            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);

            foreach (var ach in unlocked)
            {
                if (ach.RewardCoins > 0)
                {
                    await userRepo.TryModifyBalanceAsync(
                        guild.Id,
                        userId,
                        ach.RewardCoins,
                        "achievement_reward",
                        $"Награда за достижение: {ach.Name}",
                        ct);
                }

                if (ach.RewardXp > 0)
                {
                    await userRepo.AddMessageProgressAsync(guild.Id, userId, ach.RewardXp, 0, ct);
                }

                var lines = new[]
                {
                    $"Участник <@{userId}> открыл новое достижение!",
                    $"**Название:** {ach.Name} (`{ach.Code}`)",
                    $"**Описание:** {ach.Description}",
                    $"**Редкость:** {ItemService.FormatRarityLabel(ach.Rarity)} • Значок: `{ach.BadgeIcon}`",
                    $"**Награда:** +{ach.RewardCoins:N0} {cfg.CurrencySymbol} и +{ach.RewardXp:N0} XP"
                };

                var embed = embeds.Success("Достижение разблокировано!", string.Join(Environment.NewLine, lines));

                if (notifyChannel is not null)
                {
                    await notifyChannel.SendMessageAsync(embed: embed);
                }
                else
                {
                    var member = guild.GetUser(userId);
                    if (member is not null)
                    {
                        try { await member.SendMessageAsync(embed: embed); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка обработки достижения {EventType} для пользователя {UserId}", eventType, userId);
        }
    }
}
