using Bot.Data;
using Bot.Infrastructure;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class SchedulerService(
    DiscordSocketClient client,
    ModerationRepository modRepo,
    SocialRepository socialRepo,
    SocialExpansionRepository socialExpRepo,
    PartyGameRepository partyRepo,
    PartyGameService partyService,
    GuildSettingsRepository settingsRepo,
    LevelingService levelingService,
    PrivateVoiceService privateVoiceService,
    ClanService clanService,
    AuctionService auctionService,
    CasinoService casinoService,
    CommunityEventsService communityEventsService,
    ServerLoggingService serverLoggingService,
    DatabaseBackupService dbBackupService,
    BackupService backupService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private const ulong RoleBanId = 1553561451137073152UL;
    private DateTimeOffset _lastAutoBackup = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastLogPurge = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SafeExecuteAsync(ProcessExpiredPunishmentsAsync, "Punishments", stoppingToken);
                await SafeExecuteAsync(ProcessRemindersAsync, "Reminders", stoppingToken);
                await SafeExecuteAsync(t => levelingService.FlushActiveVoiceSessionsAsync(client, t), "VoiceSessions", stoppingToken);
                await SafeExecuteAsync(t => privateVoiceService.CleanupExpiredEmptyRoomsAsync(client, t), "PrivateVoice", stoppingToken);
                await SafeExecuteAsync(t => clanService.ProcessDueClanWarsAsync(client, t), "ClanWars", stoppingToken);
                await SafeExecuteAsync(t => auctionService.ProcessExpiredAuctionsAsync(client, t), "Auctions", stoppingToken);
                await SafeExecuteAsync(t => casinoService.ProcessAbandonedBlackjackSessionsAsync(client, t), "Blackjack", stoppingToken);
                await SafeExecuteAsync(socialExpRepo.RefundExpiredDuelsAsync, "Duels", stoppingToken);
                await SafeExecuteAsync(ProcessExpiredPartyGamesAsync, "PartyGames", stoppingToken);
                await SafeExecuteAsync(t => socialExpRepo.ProcessDueBankInterestAsync(
                    settingsProvider.Current.Bank.DailyInterestPercent,
                    settingsProvider.Current.Bank.MaxInterestCapPerDay,
                    t), "BankInterest", stoppingToken);
                await SafeExecuteAsync(t => communityEventsService.ProcessDuePollsAndGiveawaysAsync(client, t), "PollsAndGiveaways", stoppingToken);
                await SafeExecuteAsync(t => dbBackupService.ProcessScheduledDatabaseBackupAsync(client, t), "DbBackup", stoppingToken);
                await SafeExecuteAsync(ProcessAutoBackupAsync, "AutoBackup", stoppingToken);
                await SafeExecuteAsync(ProcessLogPurgeAsync, "LogPurge", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка фонового планировщика задач");
            }
        }
    }

    private async Task SafeExecuteAsync(Func<CancellationToken, Task> action, string jobName, CancellationToken ct)
    {
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка в фоновой задаче {JobName}", jobName);
        }
    }

    private async Task ProcessExpiredPartyGamesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await partyRepo.GetExpiredSessionsAsync(now, ct);
        foreach (var s in expired)
        {
            var players = await partyRepo.GetPlayersAsync(s.Id, ct);
            var guild = client.GetGuild(s.GuildId);
            await partyService.UnmuteSessionUsersAsync(guild, s.VoiceChannelId, s.MutedUserIds, players.Select(x => x.UserId));
            await partyRepo.CancelAndRefundSessionAsync(s.Id, "refunded", ct);
        }
    }

    private async Task ProcessExpiredPunishmentsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await modRepo.GetExpiredActivePunishmentsAsync(now, ct);

        foreach (var item in expired)
        {
            await modRepo.DeactivatePunishmentByIdAsync(item.Id, ct);
            var guild = client.GetGuild(item.GuildId);
            if (guild is null)
                continue;

            try
            {
                var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
                if (item.Type == "ban")
                {
                    // Снимаем как серверный бан Discord, так и ролевой бан от ИИ-агентов (ROLE_BAN_ID)
                    try
                    {
                        await guild.RemoveBanAsync(item.UserId);
                    }
                    catch
                    {
                    }

                    var member = await guild.GetGuildUserSafeAsync(item.UserId);
                    if (member is not null)
                    {
                        if (guild.GetRole(RoleBanId) is { } banRole && member.RoleIds.Contains(banRole.Id))
                        {
                            try { await member.RemoveRoleAsync(banRole.Id); } catch { }
                        }

                        var verRoleId = cfg.VerifiedRoleId != 0 ? cfg.VerifiedRoleId : 1548721744871821476UL;
                        if (guild.GetRole(verRoleId) is { } verRole && !member.RoleIds.Contains(verRole.Id))
                        {
                            try { await member.AddRoleAsync(verRole.Id); } catch { }
                        }
                    }
                }
                else if (item.Type == "mute")
                {
                    var member = await guild.GetGuildUserSafeAsync(item.UserId);
                    if (member is not null)
                    {
                        try { await member.RemoveTimeOutAsync(); } catch { }
                        if (cfg.MutedRoleId != 0 && guild.GetRole(cfg.MutedRoleId) is { } muteRole && member.RoleIds.Contains(muteRole.Id))
                        {
                            try { await member.RemoveRoleAsync(muteRole.Id); } catch { }
                        }
                    }
                }
                else if (item.Type == "vban")
                {
                    await modRepo.RemoveVoiceBanAsync(guild.Id, item.UserId, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Не удалось автоматически снять наказание #{Id} типа {Type}", item.Id, item.Type);
            }
        }
    }

    private async Task ProcessRemindersAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await socialRepo.GetDueRemindersAsync(now, ct);

        foreach (var rem in due)
        {
            await socialRepo.MarkReminderCompletedAsync(rem.Id, ct);
            var guild = client.GetGuild(rem.GuildId);
            var channel = guild?.GetTextChannel(rem.ChannelId);

            if (channel is not null)
            {
                try
                {
                    var embed = embeds.Info("Напоминание", rem.Message);
                    await channel.SendMessageAsync(text: $"<@{rem.UserId}>", embed: embed);
                }
                catch
                {
                }
            }
        }
    }

    private async Task ProcessAutoBackupAsync(CancellationToken ct)
    {
        var hours = Math.Max(1, settingsProvider.Current.Security.AutoBackupHours);
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastAutoBackup).TotalHours < hours)
            return;

        _lastAutoBackup = now;
        foreach (var guild in client.Guilds)
        {
            try
            {
                await backupService.CreateBackupAsync(
                    guild,
                    guild.CurrentUser.Id,
                    $"auto-{now:yyyyMMdd-HHmm}",
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось выполнить автобэкап гильдии {GuildId}", guild.Id);
            }
        }
    }

    private async Task ProcessLogPurgeAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastLogPurge).TotalHours < 6)
            return;

        _lastLogPurge = now;
        await serverLoggingService.PurgeExpiredMessageLogsAsync(ct);
    }
}
