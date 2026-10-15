using System.Collections.Concurrent;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class PrivateVoiceService(
    PrivateVoiceRepository voiceRepo,
    ILogger<PrivateVoiceService> logger)
{
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _renameRateLimits = new();

    public bool CanModifyChannelName(ulong channelId, out int waitSeconds)
    {
        waitSeconds = 0;
        var now = DateTimeOffset.UtcNow;
        if (_renameRateLimits.TryGetValue(channelId, out var allowedAt) && allowedAt > now)
        {
            waitSeconds = Math.Max(1, (int)Math.Ceiling((allowedAt - now).TotalSeconds));
            return false;
        }

        _renameRateLimits[channelId] = now.AddMinutes(5);
        return true;
    }

    public async Task HandleVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after,
        CancellationToken ct = default)
    {
        if (user.IsBot || user is not SocketGuildUser member)
            return;

        var guild = member.Guild;
        var hubCfg = await voiceRepo.GetOrCreateHubConfigAsync(guild.Id, ct);

        if (before.VoiceChannel is not null && before.VoiceChannel.Id != after.VoiceChannel?.Id)
        {
            var leftRoom = await voiceRepo.GetRoomByChannelAsync(before.VoiceChannel.Id, ct);
            if (leftRoom is not null)
            {
                var nonBotCount = before.VoiceChannel.ConnectedUsers.Count(u => !u.IsBot && u.Id != member.Id);
                if (nonBotCount == 0)
                {
                    await voiceRepo.SetEmptySinceAsync(leftRoom.ChannelId, DateTimeOffset.UtcNow, ct);
                }
            }
        }

        if (after.VoiceChannel is not null)
        {
            var enteredRoom = await voiceRepo.GetRoomByChannelAsync(after.VoiceChannel.Id, ct);
            if (enteredRoom is not null)
            {
                var bannedSet = ParseUserIds(enteredRoom.BannedUserIds);
                if (bannedSet.Contains(member.Id) && member.Id != enteredRoom.OwnerId)
                {
                    try { await member.ModifyAsync(p => p.Channel = null); } catch { }
                    return;
                }

                if (enteredRoom.EmptySince.HasValue)
                {
                    await voiceRepo.SetEmptySinceAsync(enteredRoom.ChannelId, null, ct);
                }
                return;
            }

            if (hubCfg.HubChannelId != 0 && after.VoiceChannel.Id == hubCfg.HubChannelId)
            {
                await CreateOrMoveToPersonalRoomAsync(guild, member, hubCfg, after.VoiceChannel, ct);
            }
        }
    }

    public async Task ReconcileRoomsOnStartupAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var rooms = await voiceRepo.GetAllRoomsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var room in rooms)
        {
            var guild = client.GetGuild(room.GuildId);
            var voiceChannel = guild?.GetVoiceChannel(room.ChannelId);

            if (voiceChannel is null)
            {
                await voiceRepo.DeleteRoomAsync(room.ChannelId, ct);
                continue;
            }

            var activeMembers = voiceChannel.ConnectedUsers.Count(u => !u.IsBot);
            if (activeMembers == 0 && !room.EmptySince.HasValue)
            {
                await voiceRepo.SetEmptySinceAsync(room.ChannelId, now, ct);
            }
            else if (activeMembers > 0 && room.EmptySince.HasValue)
            {
                await voiceRepo.SetEmptySinceAsync(room.ChannelId, null, ct);
            }
        }
    }

    public async Task CleanupExpiredEmptyRoomsAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var rooms = await voiceRepo.GetAllRoomsAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var room in rooms.Where(r => r.EmptySince.HasValue))
        {
            var guild = client.GetGuild(room.GuildId);
            if (guild is null)
            {
                await voiceRepo.DeleteRoomAsync(room.ChannelId, ct);
                continue;
            }

            var hubCfg = await voiceRepo.GetOrCreateHubConfigAsync(guild.Id, ct);
            var timeoutSec = Math.Max(10, hubCfg.EmptyTimeoutSeconds);

            if ((now - room.EmptySince!.Value).TotalSeconds < timeoutSec)
                continue;

            var voiceChannel = guild.GetVoiceChannel(room.ChannelId);
            if (voiceChannel is not null && voiceChannel.ConnectedUsers.Any(u => !u.IsBot))
            {
                await voiceRepo.SetEmptySinceAsync(room.ChannelId, null, ct);
                continue;
            }

            await voiceRepo.DeleteRoomAsync(room.ChannelId, ct);
            _renameRateLimits.TryRemove(room.ChannelId, out _);

            if (voiceChannel is not null)
            {
                try
                {
                    await voiceChannel.DeleteAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Не удалось удалить пустую приватную комнату {ChannelId}", room.ChannelId);
                }
            }
        }
    }

    public static readonly Color PanelColor = new(43, 45, 49);

    public (Embed Embed, MessageComponent Components) BuildStaticHubPanel()
    {
        var embed = new EmbedBuilder()
            .WithTitle("Управление приватной комнатой")
            .WithColor(PanelColor)
            .AddField("\u200B",
                "🔒 — закрыть / открыть\n" +
                "➕ — пригласить / дать доступ\n" +
                "✂️ — выгнать пользователя\n" +
                "🎙️ — замьютить / размьютить\n" +
                "👤 — забрать доступ\n" +
                "🙈 — скрыть / показать канал",
                inline: true)
            .AddField("\u200B",
                "💡 — информация о комнате\n" +
                "✏️ — переименовать комнату\n" +
                "👥 — лимит комнаты\n" +
                "👑 — назначить владельца\n" +
                "🔄 — стать владельцем\n" +
                "💾 / 📂 — сохранить / загрузить пресет",
                inline: true)
            .Build();

        var comp = new ComponentBuilder()
            .WithButton(customId: "pvoice_hub:lock", style: ButtonStyle.Secondary, emote: new Emoji("🔒"), row: 0)
            .WithButton(customId: "pvoice_hub:invite", style: ButtonStyle.Secondary, emote: new Emoji("➕"), row: 0)
            .WithButton(customId: "pvoice_hub:kick", style: ButtonStyle.Secondary, emote: new Emoji("✂️"), row: 0)
            .WithButton(customId: "pvoice_hub:mute", style: ButtonStyle.Secondary, emote: new Emoji("🎙️"), row: 0)
            .WithButton(customId: "pvoice_hub:revoke", style: ButtonStyle.Secondary, emote: new Emoji("👤"), row: 0)
            .WithButton(customId: "pvoice_hub:info", style: ButtonStyle.Secondary, emote: new Emoji("💡"), row: 1)
            .WithButton(customId: "pvoice_hub:rename", style: ButtonStyle.Secondary, emote: new Emoji("✏️"), row: 1)
            .WithButton(customId: "pvoice_hub:limit", style: ButtonStyle.Secondary, emote: new Emoji("👥"), row: 1)
            .WithButton(customId: "pvoice_hub:transfer", style: ButtonStyle.Secondary, emote: new Emoji("👑"), row: 1)
            .WithButton(customId: "pvoice_hub:claim", style: ButtonStyle.Secondary, emote: new Emoji("🔄"), row: 1)
            .WithButton(customId: "pvoice_hub:hide", style: ButtonStyle.Secondary, emote: new Emoji("🙈"), row: 2)
            .WithButton(customId: "pvoice_hub:save_preset", style: ButtonStyle.Secondary, emote: new Emoji("💾"), row: 2)
            .WithButton(customId: "pvoice_hub:load_preset", style: ButtonStyle.Secondary, emote: new Emoji("📂"), row: 2)
            .Build();

        return (embed, comp);
    }

    public (Embed Embed, MessageComponent Components) BuildControlPanel(PrivateVoiceRoomEntity room)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Управление приватной комнатой")
            .WithColor(PanelColor)
            .AddField("\u200B",
                "🔒 — закрыть / открыть\n" +
                "➕ — пригласить / дать доступ\n" +
                "✂️ — выгнать пользователя\n" +
                "🎙️ — замьютить / размьютить\n" +
                "👤 — забрать доступ\n" +
                "🙈 — скрыть / показать канал",
                inline: true)
            .AddField("\u200B",
                "💡 — информация о комнате\n" +
                "✏️ — переименовать комнату\n" +
                "👥 — лимит комнаты\n" +
                "👑 — назначить владельца\n" +
                "🔄 — стать владельцем\n" +
                "💾 / 📂 — сохранить / загрузить пресет",
                inline: true)
            .Build();

        var comp = new ComponentBuilder()
            .WithButton(customId: $"pvoice:lock:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("🔒"), row: 0)
            .WithButton(customId: $"pvoice:invite:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("➕"), row: 0)
            .WithButton(customId: $"pvoice:kick:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("✂️"), row: 0)
            .WithButton(customId: $"pvoice:mute:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("🎙️"), row: 0)
            .WithButton(customId: $"pvoice:revoke:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("👤"), row: 0)
            .WithButton(customId: $"pvoice:info:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("💡"), row: 1)
            .WithButton(customId: $"pvoice:rename:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("✏️"), row: 1)
            .WithButton(customId: $"pvoice:limit:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("👥"), row: 1)
            .WithButton(customId: $"pvoice:transfer:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("👑"), row: 1)
            .WithButton(customId: $"pvoice:claim:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("🔄"), row: 1)
            .WithButton(customId: $"pvoice:hide:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("🙈"), row: 2)
            .WithButton(customId: $"pvoice:save_preset:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("💾"), row: 2)
            .WithButton(customId: $"pvoice:load_preset:{room.ChannelId}", style: ButtonStyle.Secondary, emote: new Emoji("📂"), row: 2)
            .Build();

        return (embed, comp);
    }

    public static HashSet<ulong> ParseUserIds(string raw)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part, out var id))
                set.Add(id);
        }

        return set;
    }

    private async Task CreateOrMoveToPersonalRoomAsync(
        SocketGuild guild,
        SocketGuildUser member,
        VoiceHubConfigEntity hubCfg,
        SocketVoiceChannel hubChannel,
        CancellationToken ct)
    {
        try
        {
            var existing = await voiceRepo.GetRoomByOwnerAsync(guild.Id, member.Id, ct);
            if (existing is not null && guild.GetVoiceChannel(existing.ChannelId) is { } existingVc)
            {
                await member.ModifyAsync(p => p.Channel = existingVc);
                return;
            }

            var preset = await voiceRepo.GetPresetAsync(guild.Id, member.Id, ct);
            var roomName = preset is not null && !string.IsNullOrWhiteSpace(preset.RoomName)
                ? preset.RoomName
                : hubCfg.NameTemplate.Replace("{user}", member.DisplayName, StringComparison.OrdinalIgnoreCase).Trim();

            if (roomName.Length > 90)
                roomName = roomName[..90];

            var userLimit = preset?.UserLimit ?? hubCfg.DefaultLimit;
            var isLocked = preset?.IsLocked ?? false;
            var isHidden = preset?.IsHidden ?? false;
            var bannedRaw = preset?.BannedUserIds ?? string.Empty;

            var categoryId = hubCfg.CategoryId != 0
                ? hubCfg.CategoryId
                : hubChannel.CategoryId;

            var overwrites = new List<Overwrite>
            {
                new(guild.CurrentUser.Id, PermissionTarget.User, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow,
                    manageChannel: PermValue.Allow,
                    moveMembers: PermValue.Allow)),
                new(member.Id, PermissionTarget.User, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow,
                    speak: PermValue.Allow,
                    stream: PermValue.Allow))
            };

            if (isLocked || isHidden)
            {
                overwrites.Add(new Overwrite(
                    guild.EveryoneRole.Id,
                    PermissionTarget.Role,
                    new OverwritePermissions(
                        viewChannel: isHidden ? PermValue.Deny : PermValue.Inherit,
                        connect: isLocked ? PermValue.Deny : PermValue.Inherit)));
            }

            var createdVc = await guild.CreateVoiceChannelAsync(roomName, p =>
            {
                p.CategoryId = categoryId;
                p.PermissionOverwrites = overwrites;
                if (userLimit > 0)
                    p.UserLimit = userLimit;
            });

            var entity = new PrivateVoiceRoomEntity(
                ChannelId: createdVc.Id,
                GuildId: guild.Id,
                OwnerId: member.Id,
                RoomName: roomName,
                UserLimit: userLimit,
                IsLocked: isLocked,
                IsHidden: isHidden,
                BannedUserIds: bannedRaw,
                EmptySince: null,
                CreatedAt: DateTimeOffset.UtcNow);

            await voiceRepo.CreateRoomAsync(entity, ct);
            await member.ModifyAsync(p => p.Channel = createdVc);

            var (panelEmbed, panelComp) = BuildControlPanel(entity);

            try
            {
                await createdVc.SendMessageAsync(embed: panelEmbed, components: panelComp);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Не удалось отправить панель в чат голосового канала {ChannelId}", createdVc.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка создания приватной голосовой комнаты для {UserId}", member.Id);
        }
    }
}
