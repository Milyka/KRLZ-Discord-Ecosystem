using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class ClanService(
    ClanRepository clanRepo,
    GuildSettingsRepository settingsRepo,
    AchievementService achievementService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<ClanService> logger)
{
    private readonly ConcurrentDictionary<(ulong GuildId, ulong TargetUserId), (long ClanId, DateTimeOffset ExpiresAt)> _pendingInvites = new();

    public async Task<(bool Success, string Message, ClanEntity? Clan)> CreateClanAsync(
        SocketGuild guild,
        SocketGuildUser leader,
        string name,
        string tag,
        string description,
        string colorHex,
        CancellationToken ct = default)
    {
        var cleanName = name.Trim();
        var cleanTag = tag.Trim().ToUpperInvariant();

        if (cleanName.Length is < 3 or > 32)
            return (false, "Название клана должно содержать от 3 до 32 символов.", null);

        if (cleanTag.Length is < 2 or > 5 || !cleanTag.All(char.IsLetterOrDigit))
            return (false, "Тег клана должен состоять из 2–5 букв или цифр.", null);

        if (!EmbedFactory.TryParseColor(colorHex, out var discordColor))
            return (false, "Укажите корректный HEX-цвет роли клана в формате `#RRGGBB`.", null);

        var clanCfg = settingsProvider.Current.Clans;
        var guildCfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);

        var createdRole = await guild.CreateRoleAsync(
            name: $"[{cleanTag}] {cleanName}",
            permissions: GuildPermissions.None,
            color: discordColor,
            isHoisted: false,
            isMentionable: true);

        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new(createdRole.Id, PermissionTarget.Role, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                attachFiles: PermValue.Allow)),
            new(guild.CurrentUser.Id, PermissionTarget.User, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                manageChannel: PermValue.Allow))
        };

        var channelSlug = $"clan-{cleanTag.ToLowerInvariant()}";
        var createdChannel = await guild.CreateTextChannelAsync(channelSlug, props =>
        {
            props.Topic = $"Приватный штаб клана [{cleanTag}] {cleanName}";
            props.PermissionOverwrites = overwrites;
            if (clanCfg.ClanCategoryId != 0 && guild.GetCategoryChannel(clanCfg.ClanCategoryId) is not null)
                props.CategoryId = clanCfg.ClanCategoryId;
        });

        var formattedColor = "#" + discordColor.RawValue.ToString("X6");
        var (ok, err, entity) = await clanRepo.CreateClanWithPaymentAsync(
            guild.Id,
            leader.Id,
            cleanName,
            cleanTag,
            description.Trim(),
            createdRole.Id,
            createdChannel.Id,
            formattedColor,
            clanCfg.BaseMemberLimit,
            clanCfg.BaseOfficerSlots,
            clanCfg.CreationPrice,
            ct);

        if (!ok || entity is null)
        {
            try { await createdChannel.DeleteAsync(); } catch { }
            try { await createdRole.DeleteAsync(); } catch { }
            return (false, $"{err} Стоимость создания клана: **{clanCfg.CreationPrice:N0} {guildCfg.CurrencySymbol}**.", null);
        }

        await leader.AddRoleAsync(createdRole);
        await achievementService.TrackEventAsync(guild, leader.Id, "clan_member", 1, isAbsoluteValue: true, null, ct);

        var welcomeEmbed = embeds.Success($"Штаб клана [{cleanTag}] {cleanName}",
            $"Клан успешно основан лидером <@{leader.Id}>! Используйте `/clan info`, `/clan invite` и `/clan upgrade` для развития.");
        await createdChannel.SendMessageAsync(embed: welcomeEmbed);

        return (true, $"Клан **[{cleanTag}] {cleanName}** основан! Канал штаба: <#{createdChannel.Id}>.", entity);
    }

    public void RegisterInvite(ulong guildId, long clanId, ulong targetUserId)
    {
        _pendingInvites[(guildId, targetUserId)] = (clanId, DateTimeOffset.UtcNow.AddMinutes(15));
    }

    public bool TryConsumeInvite(ulong guildId, ulong targetUserId, long expectedClanId)
    {
        if (!_pendingInvites.TryGetValue((guildId, targetUserId), out var invite))
            return false;

        if (invite.ClanId != expectedClanId || invite.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        _pendingInvites.TryRemove((guildId, targetUserId), out _);
        return true;
    }

    public async Task<int> GetUserEarnBonusPercentAsync(ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var userClan = await clanRepo.GetUserClanAsync(guildId, userId, ct);
        return userClan?.Clan.EarnBonusPercent ?? 0;
    }

    public async Task ProcessDueClanWarsAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dueWars = await clanRepo.GetDueActiveWarsAsync(now, ct);
        if (dueWars.Count == 0)
            return;

        var winPoints = settingsProvider.Current.Clans.PointsPerWarWin;

        foreach (var war in dueWars)
        {
            try
            {
                var c1 = await clanRepo.GetByIdAsync(war.GuildId, war.ChallengerClanId, ct);
                var c2 = await clanRepo.GetByIdAsync(war.GuildId, war.DefenderClanId, ct);
                var (winnerId, stolen) = await clanRepo.FinalizeWarTransactionAsync(war, winPoints, ct);

                var guild = client.GetGuild(war.GuildId);
                if (guild is null || c1 is null || c2 is null)
                    continue;

                var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
                string outcomeText;
                if (winnerId == c1.Id)
                {
                    outcomeText = $"Победил клан **[{c1.Tag}] {c1.Name}**! Захвачено из казны соперника: **{stolen:N0} {cfg.CurrencySymbol}** и начислено **+{winPoints} очков**.";
                }
                else if (winnerId == c2.Id)
                {
                    outcomeText = $"Победил клан **[{c2.Tag}] {c2.Name}**! Захвачено из казны соперника: **{stolen:N0} {cfg.CurrencySymbol}** и начислено **+{winPoints} очков**.";
                }
                else
                {
                    outcomeText = "Война завершилась вничью! Казна обоих кланов осталась неприкосновенной.";
                }

                var summary = string.Join(Environment.NewLine,
                    $"**[{c1.Tag}] {c1.Name}:** {war.ChallengerScore:N0} очков",
                    $"**[{c2.Tag}] {c2.Name}:** {war.DefenderScore:N0} очков",
                    string.Empty,
                    outcomeText);

                var embed = embeds.Info($"Итоги клановой войны #{war.Id}", summary);

                if (c1.ChannelId != 0 && guild.GetTextChannel(c1.ChannelId) is { } ch1)
                    await ch1.SendMessageAsync(embed: embed);
                if (c2.ChannelId != 0 && guild.GetTextChannel(c2.ChannelId) is { } ch2)
                    await ch2.SendMessageAsync(embed: embed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка завершения клановой войны #{WarId}", war.Id);
            }
        }
    }
}
