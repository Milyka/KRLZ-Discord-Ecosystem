using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class LevelingService(
    UserRepository userRepo,
    SocialRepository socialRepo,
    SocialExpansionRepository socialExpRepo,
    ClanRepository clanRepo,
    ClanService clanService,
    EconomyService economyService,
    InventoryService inventoryService,
    AchievementService achievementService,
    ProgressionService progressionService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<LevelingService> logger)
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), (ulong ChannelId, DateTimeOffset JoinedAt)> _voiceStates = new();

    private static readonly IReadOnlyDictionary<int, string> LevelTitles = new Dictionary<int, string>
    {
        [5] = "Освоившийся",
        [10] = "Активист KRLZ",
        [20] = "Ветеран KRLZ",
        [35] = "Элита KRLZ",
        [50] = "Легенда KRLZ"
    };

    private async Task<double> GetPetBonusMultiplierAsync(ulong guildId, ulong userId, CancellationToken ct)
    {
        var pet = await socialExpRepo.GetUserPetAsync(guildId, userId, ct);
        if (pet is null || !pet.IsActive || pet.Satiety < 20)
            return 1.0;

        var basePct = pet.Rarity switch
        {
            "legendary" => 0.15,
            "epic" => 0.10,
            "rare" => 0.06,
            _ => 0.03
        };
        return 1.0 + basePct + Math.Min(0.15, (pet.Level - 1) * 0.01);
    }

    public async Task HandleMessageAsync(SocketUserMessage message, CancellationToken ct = default)
    {
        if (message.Author.IsBot || message.Author is not SocketGuildUser member || message.Channel is not SocketTextChannel channel)
            return;

        if (!economyService.TryAcquireMessageRewardSlot(member.Guild.Id, member.Id))
            return;

        var eco = settingsProvider.Current.Economy;
        var clanBonusPct = await clanService.GetUserEarnBonusPercentAsync(member.Guild.Id, member.Id, ct);
        var petMult = await GetPetBonusMultiplierAsync(member.Guild.Id, member.Id, ct);
        var xpMult = inventoryService.GetMultiplier(member.Guild.Id, member.Id, "xp_boost") * petMult;
        var coinMult = inventoryService.GetMultiplier(member.Guild.Id, member.Id, "coin_boost") * (1.0 + clanBonusPct / 100.0) * petMult;

        var xpGain = (long)Math.Round(eco.XpPerMessage * xpMult);
        var coinGain = (long)Math.Round(eco.MessageRewardAmount * coinMult);

        var (newLevel, leveledUp) = await userRepo.AddMessageProgressAsync(
            member.Guild.Id,
            member.Id,
            xpGain,
            coinGain,
            ct);

        var clanPts = settingsProvider.Current.Clans.PointsPerMessage;
        await clanRepo.AddActivityPointsAsync(member.Guild.Id, member.Id, clanPts, ct);

        await progressionService.TrackActivityAsync(
            member.Guild.Id,
            member.Id,
            "messages",
            1,
            settingsProvider.Current.BattlePass.BpXpPerMessage,
            ct);

        var profile = await userRepo.GetOrCreateAsync(member.Guild.Id, member.Id, ct);
        await achievementService.TrackEventAsync(member.Guild, member.Id, "messages", profile.Messages, isAbsoluteValue: true, channel, ct);
        await achievementService.TrackEventAsync(member.Guild, member.Id, "balance", profile.Balance, isAbsoluteValue: true, channel, ct);

        if (leveledUp)
            await HandleLevelUpAsync(member, channel, newLevel);
    }

    public async Task HandleVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after,
        CancellationToken ct = default)
    {
        if (user.IsBot || user is not SocketGuildUser member)
            return;

        var guildId = member.Guild.Id;
        var key = (guildId, member.Id);
        var now = DateTimeOffset.UtcNow;
        var isDeaf = after.IsSelfDeafened || after.IsDeafened;
        var wasDeaf = before.IsSelfDeafened || before.IsDeafened;

        if (before.VoiceChannel is not null && after.VoiceChannel is not null && before.VoiceChannel.Id == after.VoiceChannel.Id)
        {
            if (isDeaf && !wasDeaf)
            {
                if (_voiceStates.TryRemove(key, out var session))
                {
                    await FlushSingleSessionAsync(guildId, member, session, now, ct);
                }
            }
            else if (!isDeaf && wasDeaf)
            {
                _voiceStates[key] = (after.VoiceChannel.Id, now);
            }
            return;
        }

        if (before.VoiceChannel is not null && (after.VoiceChannel is null || after.VoiceChannel.Id != before.VoiceChannel.Id))
        {
            if (_voiceStates.TryRemove(key, out var session))
            {
                await FlushSingleSessionAsync(guildId, member, session, now, ct);
            }
        }

        if (after.VoiceChannel is not null && !isDeaf)
        {
            _voiceStates[key] = (after.VoiceChannel.Id, now);
        }
    }

    private async Task FlushSingleSessionAsync(
        ulong guildId,
        SocketGuildUser member,
        (ulong ChannelId, DateTimeOffset JoinedAt) session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var seconds = (long)Math.Max(0, (now - session.JoinedAt).TotalSeconds);
        if (seconds < 10)
            return;

        var minutes = seconds / 60;
        var eco = settingsProvider.Current.Economy;
        var clanBonusPct = await clanService.GetUserEarnBonusPercentAsync(guildId, member.Id, ct);
        var petMult = await GetPetBonusMultiplierAsync(guildId, member.Id, ct);
        var xpMult = inventoryService.GetMultiplier(guildId, member.Id, "xp_boost") * petMult;
        var coinMult = inventoryService.GetMultiplier(guildId, member.Id, "coin_boost") * (1.0 + clanBonusPct / 100.0) * petMult;

        await userRepo.AddVoiceProgressAsync(
            guildId,
            member.Id,
            seconds,
            (long)Math.Round(minutes * eco.XpPerVoiceMinute * xpMult),
            (long)Math.Round(minutes * eco.VoiceRewardPerMinute * coinMult),
            ct);

        if (minutes > 0)
        {
            await clanRepo.AddActivityPointsAsync(
                guildId,
                member.Id,
                (int)(minutes * settingsProvider.Current.Clans.PointsPerVoiceMinute),
                ct);

            await progressionService.TrackActivityAsync(
                guildId,
                member.Id,
                "voice_minutes",
                minutes,
                (int)(minutes * settingsProvider.Current.BattlePass.BpXpPerVoiceMinute),
                ct);

            await TryCreditMarriageVoiceXpAsync(guildId, member.Id, session.ChannelId, minutes, ct);
        }

        await socialRepo.RecordVoiceSessionAsync(guildId, member.Id, session.ChannelId, session.JoinedAt, now, seconds, ct);
        var profile = await userRepo.GetOrCreateAsync(guildId, member.Id, ct);
        await achievementService.TrackEventAsync(member.Guild, member.Id, "voice_minutes", profile.VoiceSeconds / 60, isAbsoluteValue: true, null, ct);
    }

    public async Task FlushActiveVoiceSessionsAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var eco = settingsProvider.Current.Economy;

        foreach (var kvp in _voiceStates.ToArray())
        {
            var (guildId, userId) = kvp.Key;
            var (channelId, joinedAt) = kvp.Value;
            var elapsed = (long)(now - joinedAt).TotalSeconds;
            if (elapsed < 60)
                continue;

            _voiceStates[kvp.Key] = (channelId, now);
            var minutes = elapsed / 60;
            var clanBonusPct = await clanService.GetUserEarnBonusPercentAsync(guildId, userId, ct);
            var petMult = await GetPetBonusMultiplierAsync(guildId, userId, ct);
            var xpMult = inventoryService.GetMultiplier(guildId, userId, "xp_boost") * petMult;
            var coinMult = inventoryService.GetMultiplier(guildId, userId, "coin_boost") * (1.0 + clanBonusPct / 100.0) * petMult;

            await userRepo.AddVoiceProgressAsync(
                guildId,
                userId,
                elapsed,
                (long)Math.Round(minutes * eco.XpPerVoiceMinute * xpMult),
                (long)Math.Round(minutes * eco.VoiceRewardPerMinute * coinMult),
                ct);
            await socialRepo.RecordVoiceSessionAsync(guildId, userId, channelId, joinedAt, now, elapsed, ct);
            await clanRepo.AddActivityPointsAsync(guildId, userId, (int)(minutes * settingsProvider.Current.Clans.PointsPerVoiceMinute), ct);

            await progressionService.TrackActivityAsync(
                guildId,
                userId,
                "voice_minutes",
                minutes,
                (int)(minutes * settingsProvider.Current.BattlePass.BpXpPerVoiceMinute),
                ct);

            await TryCreditMarriageVoiceXpAsync(guildId, userId, channelId, minutes, ct);

            var guild = client.GetGuild(guildId);
            if (guild is not null)
            {
                var profile = await userRepo.GetOrCreateAsync(guildId, userId, ct);
                await achievementService.TrackEventAsync(guild, userId, "voice_minutes", profile.VoiceSeconds / 60, isAbsoluteValue: true, null, ct);
            }
        }
    }

    private async Task TryCreditMarriageVoiceXpAsync(ulong guildId, ulong userId, ulong channelId, long minutes, CancellationToken ct)
    {
        var marriage = await socialRepo.GetMarriageAsync(guildId, userId, ct);
        if (marriage is null)
            return;

        var partnerId = marriage.User1Id == userId ? marriage.User2Id : marriage.User1Id;
        if (_voiceStates.TryGetValue((guildId, partnerId), out var partnerState) && partnerState.ChannelId == channelId)
        {
            var loveXp = minutes * Math.Max(1, settingsProvider.Current.Social.FamilyLoveXpPerVoiceMinute);
            await socialExpRepo.AddFamilyLoveXpAsync(marriage.Id, guildId, loveXp, ct);
        }
    }

    private async Task HandleLevelUpAsync(SocketGuildUser member, SocketTextChannel channel, int newLevel)
    {
        try
        {
            var rewardText = string.Empty;
            if (LevelTitles.TryGetValue(newLevel, out var titleName))
            {
                var role = member.Guild.Roles.FirstOrDefault(r => r.Name.Equals(titleName, StringComparison.OrdinalIgnoreCase));
                if (role is not null && member.Guild.CurrentUser.Hierarchy > role.Position)
                {
                    await member.AddRoleAsync(role);
                    rewardText = $"{Environment.NewLine}Вы получили ранговую роль <@&{role.Id}>!";
                }
            }

            var embed = embeds.Success("Повышение уровня!",
                $"Поздравляем, <@{member.Id}>! Вы достигли **{newLevel} уровня** активности на сервере KRLZ.{rewardText}");
            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Не удалось отправить уведомление о новом уровне для {UserId}", member.Id);
        }
    }
}
