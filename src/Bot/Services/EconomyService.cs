using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;

namespace Bot.Services;

public sealed class EconomyService(
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider)
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _slotsCooldowns = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _msgRewardCooldowns = new();

    public async Task<UserProfileEntity> GetProfileAsync(ulong guildId, ulong userId, CancellationToken ct = default) =>
        await userRepo.GetOrCreateAsync(guildId, userId, ct);

    public async Task<(bool Success, string Message, long Amount)> ClaimDailyAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var profile = await userRepo.GetOrCreateAsync(guildId, userId, ct);
        var eco = settingsProvider.Current.Economy;
        var cfg = await settingsRepo.GetOrCreateAsync(guildId, ct);
        var now = DateTimeOffset.UtcNow;

        if (profile.LastDailyAt.HasValue)
        {
            var nextAvailable = profile.LastDailyAt.Value.AddSeconds(eco.DailyCooldownSeconds);
            if (nextAvailable > now)
            {
                var left = TimeParser.FormatDuration(nextAvailable - now);
                return (false, $"Вы уже получали ежедневную награду. Следующая будет доступна через **{left}**.", 0);
            }
        }

        await userRepo.UpdateCooldownTimestampAsync(guildId, userId, true, now, ct);
        await userRepo.TryModifyBalanceAsync(guildId, userId, eco.DailyAmount, "daily", "Ежедневная награда", ct);
        return (true, $"Вы получили ежедневную награду: **{eco.DailyAmount} {cfg.CurrencySymbol}**!", eco.DailyAmount);
    }

    public async Task<(bool Success, string Message, long Amount)> WorkAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var profile = await userRepo.GetOrCreateAsync(guildId, userId, ct);
        var eco = settingsProvider.Current.Economy;
        var cfg = await settingsRepo.GetOrCreateAsync(guildId, ct);
        var now = DateTimeOffset.UtcNow;

        if (profile.LastWorkAt.HasValue)
        {
            var nextAvailable = profile.LastWorkAt.Value.AddSeconds(eco.WorkCooldownSeconds);
            if (nextAvailable > now)
            {
                var left = TimeParser.FormatDuration(nextAvailable - now);
                return (false, $"Вы устали после смены. Следующая работа доступна через **{left}**.", 0);
            }
        }

        var reward = Random.Shared.NextInt64(eco.WorkMinAmount, eco.WorkMaxAmount + 1);
        var jobs = new[]
        {
            "настроили систему безопасности сервера KRLZ",
            "провели рефакторинг базы данных",
            "выполнили заказ на разработку микросервиса",
            "помогли провести турнир на сервере KRLZ",
            "оптимизировали сетевую инфраструктуру"
        };
        var jobText = jobs[Random.Shared.Next(jobs.Length)];

        await userRepo.UpdateCooldownTimestampAsync(guildId, userId, false, now, ct);
        await userRepo.TryModifyBalanceAsync(guildId, userId, reward, "work", $"Работа: {jobText}", ct);
        return (true, $"Вы успешно {jobText} и заработали **{reward} {cfg.CurrencySymbol}**!", reward);
    }

    public bool CheckSlotsCooldown(ulong guildId, ulong userId, out int remainingSeconds)
    {
        remainingSeconds = 0;
        var now = DateTimeOffset.UtcNow;
        var key = (guildId, userId);

        if (_slotsCooldowns.TryGetValue(key, out var until) && until > now)
        {
            remainingSeconds = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
            return false;
        }

        _slotsCooldowns[key] = now.AddSeconds(settingsProvider.Current.Economy.SlotsCooldownSeconds);
        return true;
    }

    public bool TryAcquireMessageRewardSlot(ulong guildId, ulong userId)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (guildId, userId);
        if (_msgRewardCooldowns.TryGetValue(key, out var until) && until > now)
            return false;

        _msgRewardCooldowns[key] = now.AddSeconds(settingsProvider.Current.Economy.MessageCooldownSeconds);
        return true;
    }
}
