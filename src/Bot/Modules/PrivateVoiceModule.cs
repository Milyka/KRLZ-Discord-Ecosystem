using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class PrivateVoiceModule(
    PrivateVoiceRepository voiceRepo,
    PrivateVoiceService voiceService,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class RoomRenameModal : IModal
    {
        public string Title => "Переименование комнаты";

        [InputLabel("Новое название комнаты")]
        [ModalTextInput("name", TextInputStyle.Short, "Чилл-зона", minLength: 2, maxLength: 60)]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RoomLimitModal : IModal
    {
        public string Title => "Лимит участников";

        [InputLabel("Лимит мест (0 = без лимита, максимум 99)")]
        [ModalTextInput("limit", TextInputStyle.Short, "0", minLength: 1, maxLength: 2)]
        public string Limit { get; set; } = string.Empty;
    }

    public sealed class RoomUserModal : IModal
    {
        public string Title => "Выбор участника";

        [InputLabel("ID или @упоминание пользователя")]
        [ModalTextInput("user_input", TextInputStyle.Short, "123456789012345678", minLength: 2, maxLength: 35)]
        public string UserInput { get; set; } = string.Empty;
    }

    public static bool TryParseUserId(string? input, out ulong userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim().Trim('<', '>', '@', '!', '&');
        return ulong.TryParse(trimmed, out userId);
    }

    [SlashCommand("voice-panel", "Открыть панель управления вашей текущей приватной голосовой комнатой")]
    public async Task VoicePanelCommandAsync()
    {
        var (room, _, error) = await ResolveTargetRoomAsync(requireOwnership: true);
        if (error is not null || room is null)
        {
            await RespondAsync(embed: embeds.Error("У вас сейчас нет активной приватной голосовой комнаты. Зайдите в хаб создания комнат!"), ephemeral: true);
            return;
        }

        var (embed, comp) = voiceService.BuildControlPanel(room);
        await RespondAsync(embed: embed, components: comp, ephemeral: true);
    }

    [SlashCommand("voice-setup-panel", "Опубликовать общую панель управления приватными войсами")]
    [RequireAdmin]
    public async Task VoiceSetupPanelCommandAsync([Summary("channel", "Канал для публикации панели")] SocketTextChannel? targetChannel = null)
    {
        await DeferAsync(ephemeral: true);
        var channel = targetChannel ?? (SocketTextChannel)Context.Channel;
        var (embed, comp) = voiceService.BuildStaticHubPanel();

        try
        {
            var oldMessages = await channel.GetMessagesAsync(20).FlattenAsync();
            foreach (var oldMsg in oldMessages.Where(m => m.Author.Id == Context.Client.CurrentUser.Id && m.Embeds.Any(e => e.Title == "Управление приватной комнатой")))
            {
                await oldMsg.DeleteAsync();
            }
        }
        catch { }

        await channel.SendMessageAsync(embed: embed, components: comp);
        await FollowupAsync(embed: embeds.Success("Панель опубликована", $"Панель управления приватными комнатами размещена в <#{channel.Id}>."), ephemeral: true);
    }

    [ComponentInteraction("pvoice_hub:*", true)]
    public async Task HandleHubButtonAsync(string action)
    {
        var caller = (SocketGuildUser)Context.User;
        var requireOwnership = action is not ("claim" or "info");
        var (room, vc, error) = await ResolveTargetRoomAsync(explicitChannelId: 0, requireOwnership: requireOwnership);

        if (error is not null || room is null || vc is null)
        {
            await RespondAsync(embed: embeds.Error(error ?? "Комната не найдена."), ephemeral: true);
            return;
        }

        await DispatchRoomActionAsync(action, caller, room, vc);
    }

    [ComponentInteraction("pvoice:*:*", true)]
    public async Task HandleRoomButtonAsync(string action, string channelIdStr)
    {
        if (!ulong.TryParse(channelIdStr, out var channelId))
            return;

        var caller = (SocketGuildUser)Context.User;
        var requireOwnership = action is not ("claim" or "info");
        var (room, vc, error) = await ResolveTargetRoomAsync(explicitChannelId: channelId, requireOwnership: requireOwnership);

        if (error is not null || room is null || vc is null)
        {
            await RespondAsync(embed: embeds.Error(error ?? "Комната не найдена."), ephemeral: true);
            return;
        }

        await DispatchRoomActionAsync(action, caller, room, vc);
    }

    private async Task DispatchRoomActionAsync(string action, SocketGuildUser caller, PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        switch (action)
        {
            case "lock":
                await ExecuteLockAsync(room, vc);
                break;
            case "hide":
                await ExecuteHideAsync(room, vc);
                break;
            case "invite":
                await RespondWithModalAsync<RoomUserModal>($"pvoice_modal:invite:{room.ChannelId}");
                break;
            case "kick":
                await RespondWithModalAsync<RoomUserModal>($"pvoice_modal:kick:{room.ChannelId}");
                break;
            case "mute":
                await ExecuteMuteAsync(room, vc);
                break;
            case "revoke":
                await RespondWithModalAsync<RoomUserModal>($"pvoice_modal:revoke:{room.ChannelId}");
                break;
            case "info":
                await ExecuteInfoAsync(room, vc);
                break;
            case "rename":
                await RespondWithModalAsync<RoomRenameModal>($"pvoice_modal:rename:{room.ChannelId}");
                break;
            case "limit":
                await RespondWithModalAsync<RoomLimitModal>($"pvoice_modal:limit:{room.ChannelId}");
                break;
            case "transfer":
                await RespondWithModalAsync<RoomUserModal>($"pvoice_modal:transfer:{room.ChannelId}");
                break;
            case "claim":
                await ExecuteClaimAsync(caller, room, vc);
                break;
            case "save_preset":
                await ExecuteSavePresetAsync(caller, room);
                break;
            case "load_preset":
                await ExecuteLoadPresetAsync(caller, room, vc);
                break;
            default:
                await RespondAsync(embed: embeds.Error("Неизвестное действие."), ephemeral: true);
                break;
        }
    }

    [ModalInteraction("pvoice_modal:rename:*", true)]
    public async Task HandleRenameModalAsync(string channelIdStr, RoomRenameModal modal)
    {
        if (!ulong.TryParse(channelIdStr, out var channelId))
            return;

        var (room, vc, error) = await ResolveTargetRoomAsync(explicitChannelId: channelId, requireOwnership: true);
        if (error is not null || room is null || vc is null)
        {
            await RespondAsync(embed: embeds.Error(error ?? "Комната не найдена."), ephemeral: true);
            return;
        }

        if (!voiceService.CanModifyChannelName(channelId, out var waitSec))
        {
            await RespondAsync(embed: embeds.Error($"Защита от лимитов Discord API: переименовать комнату можно будет через **{waitSec} сек.**"), ephemeral: true);
            return;
        }

        var cleanName = modal.Name.Trim();
        await vc.ModifyAsync(p => p.Name = cleanName);
        room = room with { RoomName = cleanName };
        await voiceRepo.UpdateRoomAsync(room);

        await RespondAsync(embed: embeds.Success("Комната переименована", $"✏️ Новое название комнаты: **{cleanName}**."), ephemeral: true);
    }

    [ModalInteraction("pvoice_modal:limit:*", true)]
    public async Task HandleLimitModalAsync(string channelIdStr, RoomLimitModal modal)
    {
        if (!ulong.TryParse(channelIdStr, out var channelId))
            return;

        if (!int.TryParse(modal.Limit.Trim(), out var limit) || limit is < 0 or > 99)
        {
            await RespondAsync(embed: embeds.Error("Укажите число от 0 до 99 (0 — без лимита)."), ephemeral: true);
            return;
        }

        var (room, vc, error) = await ResolveTargetRoomAsync(explicitChannelId: channelId, requireOwnership: true);
        if (error is not null || room is null || vc is null)
        {
            await RespondAsync(embed: embeds.Error(error ?? "Комната не найдена."), ephemeral: true);
            return;
        }

        await vc.ModifyAsync(p => p.UserLimit = limit == 0 ? null : limit);
        room = room with { UserLimit = limit };
        await voiceRepo.UpdateRoomAsync(room);

        var limitStr = limit == 0 ? "без лимита" : $"{limit} чел.";
        await RespondAsync(embed: embeds.Success("Лимит обновлён", $"👥 Лимит участников комнаты: **{limitStr}**."), ephemeral: true);
    }

    [ModalInteraction("pvoice_modal:*:*", true)]
    public async Task HandleTargetUserModalAsync(string action, string channelIdStr, RoomUserModal modal)
    {
        if (action is "rename" or "limit")
            return;

        if (!ulong.TryParse(channelIdStr, out var channelId))
            return;

        if (!TryParseUserId(modal.UserInput, out var targetId))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректный Discord ID или @упоминание пользователя."), ephemeral: true);
            return;
        }

        var (room, vc, error) = await ResolveTargetRoomAsync(explicitChannelId: channelId, requireOwnership: true);
        if (error is not null || room is null || vc is null)
        {
            await RespondAsync(embed: embeds.Error(error ?? "Комната не найдена."), ephemeral: true);
            return;
        }

        if (targetId == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Нельзя применить это действие к самому себе."), ephemeral: true);
            return;
        }

        var targetMember = await Context.Guild.GetGuildUserSafeAsync(targetId);
        if (targetMember is null)
        {
            await RespondAsync(embed: embeds.Error("Участник с указанным ID не найден на сервере."), ephemeral: true);
            return;
        }

        switch (action)
        {
            case "invite":
            {
                var banned = PrivateVoiceService.ParseUserIds(room.BannedUserIds);
                banned.Remove(targetId);
                room = room with { BannedUserIds = string.Join(",", banned) };
                await voiceRepo.UpdateRoomAsync(room);

                await vc.AddPermissionOverwriteAsync(targetMember, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow,
                    speak: PermValue.Allow));

                await RespondAsync(embed: embeds.Success("Доступ выдан", $"➕ Пользователю <@{targetId}> выдан доступ к комнате <#{channelId}>."), ephemeral: true);
                break;
            }

            case "kick":
            {
                if (targetMember.VoiceChannel?.Id == channelId)
                {
                    try { await targetMember.ModifyAsync(p => p.Channel = null); } catch { }
                    await RespondAsync(embed: embeds.Success("Пользователь выгнан", $"✂️ Участник <@{targetId}> отключён из вашей комнаты."), ephemeral: true);
                }
                else
                {
                    await RespondAsync(embed: embeds.Warning("Участник не в комнате", $"Пользователь <@{targetId}> сейчас не находится в вашей комнате."), ephemeral: true);
                }
                break;
            }

            case "transfer":
            {
                if (targetMember.IsBot)
                {
                    await RespondAsync(embed: embeds.Error("Нельзя передать владение комнатой боту."), ephemeral: true);
                    return;
                }

                await vc.AddPermissionOverwriteAsync(targetMember, new OverwritePermissions(
                    viewChannel: PermValue.Allow,
                    connect: PermValue.Allow,
                    speak: PermValue.Allow,
                    stream: PermValue.Allow));

                room = room with { OwnerId = targetId };
                await voiceRepo.UpdateRoomAsync(room);

                await RespondAsync(embed: embeds.Success("Владение передано", $"👑 Новым владельцем комнаты <#{channelId}> назначен <@{targetId}>."), ephemeral: true);
                break;
            }

            case "revoke":
            {
                var banned = PrivateVoiceService.ParseUserIds(room.BannedUserIds);
                banned.Add(targetId);
                room = room with { BannedUserIds = string.Join(",", banned) };
                await voiceRepo.UpdateRoomAsync(room);

                await vc.AddPermissionOverwriteAsync(targetMember, new OverwritePermissions(
                    connect: PermValue.Deny,
                    viewChannel: PermValue.Deny));

                if (targetMember.VoiceChannel?.Id == channelId)
                {
                    try { await targetMember.ModifyAsync(p => p.Channel = null); } catch { }
                }

                await RespondAsync(embed: embeds.Warning("Доступ отозван", $"👤 У пользователя <@{targetId}> отозван доступ к комнате <#{channelId}>."), ephemeral: true);
                break;
            }
        }
    }

    private async Task<(PrivateVoiceRoomEntity? Room, SocketVoiceChannel? Channel, string? Error)> ResolveTargetRoomAsync(
        ulong explicitChannelId = 0,
        bool requireOwnership = true)
    {
        var member = (SocketGuildUser)Context.User;
        PrivateVoiceRoomEntity? room = null;
        SocketVoiceChannel? vc = null;

        if (explicitChannelId != 0)
        {
            room = await voiceRepo.GetRoomByChannelAsync(explicitChannelId);
            vc = Context.Guild.GetVoiceChannel(explicitChannelId);
        }
        else
        {
            if (member.VoiceChannel is not null)
            {
                var currentRoom = await voiceRepo.GetRoomByChannelAsync(member.VoiceChannel.Id);
                if (currentRoom is not null)
                {
                    if (!requireOwnership || currentRoom.OwnerId == member.Id)
                    {
                        room = currentRoom;
                        vc = member.VoiceChannel;
                    }
                }
            }

            if (room is null)
            {
                room = await voiceRepo.GetRoomByOwnerAsync(Context.Guild.Id, member.Id);
                if (room is not null)
                {
                    vc = Context.Guild.GetVoiceChannel(room.ChannelId);
                }
            }
        }

        if (room is null || vc is null)
        {
            return (null, null, "У вас нет активной приватной голосовой комнаты. Создайте комнату, зайдя в хаб-канал!");
        }

        if (requireOwnership && room.OwnerId != member.Id)
        {
            return (null, null, "Управлять этой комнатой может только её владелец.");
        }

        return (room, vc, null);
    }

    private async Task ExecuteLockAsync(PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        var nextLocked = !room.IsLocked;
        var currentPerms = vc.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        await vc.AddPermissionOverwriteAsync(
            Context.Guild.EveryoneRole,
            currentPerms.Modify(connect: nextLocked ? PermValue.Deny : PermValue.Inherit));

        room = room with { IsLocked = nextLocked };
        await voiceRepo.UpdateRoomAsync(room);

        var msg = nextLocked
            ? "🔒 Вход в комнату **закрыт** для всех (кроме приглашённых)."
            : "🔓 Вход в комнату **открыт** для всех участников.";

        await RespondAsync(embed: embeds.Success("Доступ к комнате", msg), ephemeral: true);
    }

    private async Task ExecuteHideAsync(PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        var nextHidden = !room.IsHidden;
        var currentPerms = vc.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        await vc.AddPermissionOverwriteAsync(
            Context.Guild.EveryoneRole,
            currentPerms.Modify(viewChannel: nextHidden ? PermValue.Deny : PermValue.Inherit));

        room = room with { IsHidden = nextHidden };
        await voiceRepo.UpdateRoomAsync(room);

        var msg = nextHidden
            ? "🙈 Комната **скрыта** из общего списка каналов."
            : "👁️ Комната снова **видна** всем участникам сервера.";

        await RespondAsync(embed: embeds.Success("Видимость комнаты", msg), ephemeral: true);
    }

    private async Task ExecuteSavePresetAsync(SocketGuildUser caller, PrivateVoiceRoomEntity room)
    {
        var preset = new PrivateVoicePresetEntity(
            GuildId: Context.Guild.Id,
            UserId: caller.Id,
            RoomName: room.RoomName,
            UserLimit: room.UserLimit,
            IsLocked: room.IsLocked,
            IsHidden: room.IsHidden,
            TrustedUserIds: string.Empty,
            BannedUserIds: room.BannedUserIds,
            UpdatedAt: DateTimeOffset.UtcNow);

        await voiceRepo.UpsertPresetAsync(preset);
        await RespondAsync(embed: embeds.Success("Пресет сохранён",
            $"💾 Настройки комнаты (**{room.RoomName}**, лимит: `{(room.UserLimit == 0 ? "без лимита" : room.UserLimit)}`) сохранены! Они будут автоматически применяться при создании вашей новой комнаты."), ephemeral: true);
    }

    private async Task ExecuteLoadPresetAsync(SocketGuildUser caller, PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        var preset = await voiceRepo.GetPresetAsync(Context.Guild.Id, caller.Id);
        if (preset is null)
        {
            await RespondAsync(embed: embeds.Error("У вас ещё нет сохранённого пресета комнаты. Сначала нажмите кнопку 💾!"), ephemeral: true);
            return;
        }

        await vc.ModifyAsync(p =>
        {
            if (voiceService.CanModifyChannelName(vc.Id, out _))
                p.Name = preset.RoomName;
            p.UserLimit = preset.UserLimit == 0 ? null : preset.UserLimit;
        });

        var currentPerms = vc.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        await vc.AddPermissionOverwriteAsync(
            Context.Guild.EveryoneRole,
            currentPerms.Modify(
                connect: preset.IsLocked ? PermValue.Deny : PermValue.Inherit,
                viewChannel: preset.IsHidden ? PermValue.Deny : PermValue.Inherit));

        room = room with
        {
            RoomName = preset.RoomName,
            UserLimit = preset.UserLimit,
            IsLocked = preset.IsLocked,
            IsHidden = preset.IsHidden,
            BannedUserIds = preset.BannedUserIds
        };
        await voiceRepo.UpdateRoomAsync(room);

        await RespondAsync(embed: embeds.Success("Пресет применён",
            $"📂 Загружен ваш пресет **{preset.RoomName}** (лимит: `{(preset.UserLimit == 0 ? "без лимита" : preset.UserLimit)}`)."), ephemeral: true);
    }

    private async Task ExecuteMuteAsync(PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        var currentPerms = vc.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        var willMute = currentPerms.Speak != PermValue.Deny;

        await vc.AddPermissionOverwriteAsync(
            Context.Guild.EveryoneRole,
            currentPerms.Modify(speak: willMute ? PermValue.Deny : PermValue.Inherit));

        var msg = willMute
            ? "🎙️ Голосовой чат **замьючен**. Говорить может только владелец комнаты."
            : "🎙️ Голосовой чат **размьючен**. Все участники снова могут говорить.";

        await RespondAsync(embed: embeds.Success("Управление микрофонами", msg), ephemeral: true);
    }

    private async Task ExecuteInfoAsync(PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        var everyonePerms = vc.GetPermissionOverwrite(Context.Guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
        var isLocked = room.IsLocked || everyonePerms.Connect == PermValue.Deny;
        var isHidden = room.IsHidden || everyonePerms.ViewChannel == PermValue.Deny;
        var isMuted = everyonePerms.Speak == PermValue.Deny;
        var limitText = room.UserLimit == 0 ? "Без лимита" : $"{room.UserLimit} чел.";

        var members = vc.ConnectedUsers.Where(u => !u.IsBot).ToList();
        var membersList = members.Count > 0
            ? string.Join(", ", members.Select(u => $"<@{u.Id}>"))
            : "В комнате никого нет";

        var infoDesc = string.Join(Environment.NewLine,
            $"**Комната:** <#{room.ChannelId}> (`{vc.Name}`)",
            $"**Владелец:** <@{room.OwnerId}>",
            $"**Участники:** `{members.Count}` / `{limitText}`",
            $"**Статус:** `{(isLocked ? "🔒 Закрыта" : "🔓 Открыта")}` • `{(isHidden ? "🙈 Скрыта" : "👁️ Видна")}` • `{(isMuted ? "🎙️ Замьючена" : "🎙️ Голос разрешён")}`",
            string.Empty,
            $"**Сейчас в комнате ({members.Count}):**",
            membersList);

        var infoEmbed = new EmbedBuilder()
            .WithTitle("💡 Информация о приватной комнате")
            .WithColor(PrivateVoiceService.PanelColor)
            .WithDescription(infoDesc)
            .Build();

        await RespondAsync(embed: infoEmbed, ephemeral: true);
    }

    private async Task ExecuteClaimAsync(SocketGuildUser caller, PrivateVoiceRoomEntity room, SocketVoiceChannel vc)
    {
        if (caller.VoiceChannel?.Id != vc.Id)
        {
            await RespondAsync(embed: embeds.Error("Вы должны находиться внутри комнаты, чтобы стать её владельцем."), ephemeral: true);
            return;
        }

        if (room.OwnerId == caller.Id)
        {
            await RespondAsync(embed: embeds.Warning("Вы уже владелец", "Вы уже являетесь владельцем этой комнаты."), ephemeral: true);
            return;
        }

        var isOwnerPresent = vc.ConnectedUsers.Any(u => u.Id == room.OwnerId);
        if (isOwnerPresent)
        {
            await RespondAsync(embed: embeds.Warning("Владелец в комнате", $"Владелец комнаты <@{room.OwnerId}> всё ещё находится в ней. Перехватить комнату нельзя."), ephemeral: true);
            return;
        }

        await vc.AddPermissionOverwriteAsync(caller, new OverwritePermissions(
            viewChannel: PermValue.Allow,
            connect: PermValue.Allow,
            speak: PermValue.Allow,
            stream: PermValue.Allow));

        room = room with { OwnerId = caller.Id };
        await voiceRepo.UpdateRoomAsync(room);

        await RespondAsync(embed: embeds.Success("Смена владельца", $"🔄 Вы успешно стали новым владельцем комнаты <#{vc.Id}>!"), ephemeral: true);
    }
}
