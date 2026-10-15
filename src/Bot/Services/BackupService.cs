using System.Text.Json;
using Bot.Data;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class BackupService(
    SecurityRepository securityRepo,
    ILogger<BackupService> logger)
{
    public async Task<(long BackupId, BackupSnapshotDto Snapshot)> CreateBackupAsync(
        SocketGuild guild,
        ulong actorId,
        string snapshotName,
        CancellationToken ct = default)
    {
        var roles = guild.Roles
            .Where(r => !r.IsEveryone && !r.IsManaged)
            .OrderByDescending(r => r.Position)
            .Select(r => new BackupRoleDto(
                OriginalId: r.Id,
                Name: r.Name,
                ColorRaw: r.Color.RawValue,
                IsHoisted: r.IsHoisted,
                IsMentionable: r.IsMentionable,
                PermissionsRaw: r.Permissions.RawValue,
                Position: r.Position))
            .ToList();

        var channels = new List<BackupChannelDto>();
        foreach (var ch in guild.Channels.Where(c => c is not SocketThreadChannel).OrderBy(c => c.Position))
        {
            List<BackupPermissionOverwriteDto> overwrites;
            try
            {
                overwrites = ch.PermissionOverwrites
                    .Select(o => new BackupPermissionOverwriteDto(
                        o.TargetId,
                        (int)o.TargetType,
                        o.Permissions.AllowValue,
                        o.Permissions.DenyValue))
                    .ToList();
            }
            catch
            {
                overwrites = [];
            }

            if (ch is SocketCategoryChannel cat)
            {
                channels.Add(new BackupChannelDto(
                    cat.Id, cat.Name, "category", cat.Position, null, null, null, null, null, overwrites));
            }
            else if (ch is SocketTextChannel txt && txt is not SocketNewsChannel)
            {
                channels.Add(new BackupChannelDto(
                    txt.Id, txt.Name, "text", txt.Position, txt.Category?.Name, txt.Topic, txt.SlowModeInterval, null, null, overwrites));
            }
            else if (ch is SocketVoiceChannel vc)
            {
                channels.Add(new BackupChannelDto(
                    vc.Id, vc.Name, "voice", vc.Position, vc.Category?.Name, null, null, vc.Bitrate, vc.UserLimit, overwrites));
            }
        }

        var snapshot = new BackupSnapshotDto(
            GuildId: guild.Id,
            GuildName: guild.Name,
            CreatedAt: DateTimeOffset.UtcNow,
            Roles: roles,
            Channels: channels);

        var json = JsonSerializer.Serialize(snapshot);
        var id = await securityRepo.SaveBackupAsync(guild.Id, snapshotName, actorId, json, ct);
        logger.LogInformation("Создана резервная копия #{BackupId} для гильдии {GuildId}", id, guild.Id);
        return (id, snapshot);
    }

    public async Task<(int RestoredRoles, int RestoredChannels)> RestoreBackupAsync(
        SocketGuild guild,
        GuildBackupEntity backupEntity,
        CancellationToken ct = default)
    {
        var snapshot = JsonSerializer.Deserialize<BackupSnapshotDto>(backupEntity.PayloadJson)
            ?? throw new InvalidOperationException("Не удалось десериализовать снимок бэкапа.");

        var restoredRoles = 0;
        var restoredChannels = 0;

        foreach (var roleDto in snapshot.Roles.OrderBy(r => r.Position))
        {
            ct.ThrowIfCancellationRequested();
            if (guild.Roles.Any(r => r.Name.Equals(roleDto.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            await guild.CreateRoleAsync(
                name: roleDto.Name,
                permissions: new GuildPermissions(roleDto.PermissionsRaw),
                color: new Color(roleDto.ColorRaw),
                isHoisted: roleDto.IsHoisted,
                isMentionable: roleDto.IsMentionable);
            restoredRoles++;
        }

        var categoryMap = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingCat in guild.CategoryChannels)
            categoryMap[existingCat.Name] = existingCat.Id;

        foreach (var catDto in snapshot.Channels.Where(c => c.ChannelType == "category").OrderBy(c => c.Position))
        {
            ct.ThrowIfCancellationRequested();
            if (categoryMap.ContainsKey(catDto.Name))
                continue;

            var createdCat = await guild.CreateCategoryChannelAsync(catDto.Name, p => p.Position = catDto.Position);
            categoryMap[catDto.Name] = createdCat.Id;
            restoredChannels++;
        }

        foreach (var chDto in snapshot.Channels.Where(c => c.ChannelType != "category").OrderBy(c => c.Position))
        {
            ct.ThrowIfCancellationRequested();
            if (chDto.ChannelType == "text")
            {
                if (guild.TextChannels.Any(c => c is not SocketThreadChannel && c.Name.Equals(chDto.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                await guild.CreateTextChannelAsync(chDto.Name, p =>
                {
                    p.Position = chDto.Position;
                    p.Topic = chDto.Topic;
                    p.SlowModeInterval = chDto.SlowModeInterval ?? 0;
                    if (!string.IsNullOrWhiteSpace(chDto.CategoryName) && categoryMap.TryGetValue(chDto.CategoryName, out var catId))
                        p.CategoryId = catId;
                });
                restoredChannels++;
            }
            else if (chDto.ChannelType == "voice")
            {
                if (guild.VoiceChannels.Any(c => c.Name.Equals(chDto.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                await guild.CreateVoiceChannelAsync(chDto.Name, p =>
                {
                    p.Position = chDto.Position;
                    if (chDto.Bitrate.HasValue)
                        p.Bitrate = chDto.Bitrate.Value;
                    p.UserLimit = chDto.UserLimit;
                    if (!string.IsNullOrWhiteSpace(chDto.CategoryName) && categoryMap.TryGetValue(chDto.CategoryName, out var catId))
                        p.CategoryId = catId;
                });
                restoredChannels++;
            }
        }

        return (restoredRoles, restoredChannels);
    }
}
