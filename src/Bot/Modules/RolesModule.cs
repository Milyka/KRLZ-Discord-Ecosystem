using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class RolesModule(
    CustomRoleRepository roleRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class RoleRenameModal : IModal
    {
        public string Title => "Изменение названия кастомной роли";

        [InputLabel("Новое название роли")]
        [ModalTextInput("name", TextInputStyle.Short, "VIP KRLZ", minLength: 2, maxLength: 48)]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class RoleColorModal : IModal
    {
        public string Title => "Изменение цвета роли (HEX)";

        [InputLabel("HEX-код цвета")]
        [ModalTextInput("hex", TextInputStyle.Short, "#FF007F", minLength: 6, maxLength: 7)]
        public string Hex { get; set; } = string.Empty;
    }

    public sealed class RoleIconModal : IModal
    {
        public string Title => "Установка эмодзи-иконки роли";

        [InputLabel("Unicode-эмодзи роли")]
        [ModalTextInput("emoji", TextInputStyle.Short, "🔥", minLength: 1, maxLength: 8)]
        public string Emoji { get; set; } = string.Empty;
    }

    public sealed class RoleMemberModal : IModal
    {
        public string Title => "Управление участником кастомной роли";

        [InputLabel("Discord ID участника сервера")]
        [ModalTextInput("user_id", TextInputStyle.Short, "123456789012345678", minLength: 15, maxLength: 22)]
        public string UserId { get; set; } = string.Empty;
    }

    [Group("create", "Создание пользовательских объектов за валюту")]
    public sealed class CreateRoleSubgroup(
        CustomRoleRepository roleRepo,
        GuildSettingsRepository settingsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("role", "Создать личную кастомную роль за валюту сервера")]
        public async Task CreateRoleCommandAsync(
            [Summary("name", "Название создаваемой роли")] string name,
            [Summary("color", "HEX-цвет роли (например #5865F2)")] string colorHex)
        {
            if (!EmbedFactory.TryParseColor(colorHex, out var discordColor))
            {
                await RespondAsync(embed: embeds.Error("Укажите корректный HEX-цвет в формате `#RRGGBB`."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var member = (SocketGuildUser)Context.User;

            var createdRole = await Context.Guild.CreateRoleAsync(
                name: name.Trim(),
                permissions: GuildPermissions.None,
                color: discordColor,
                isHoisted: true,
                isMentionable: false);

            if (cfg.CustomRoleAnchorId != 0 && Context.Guild.GetRole(cfg.CustomRoleAnchorId) is { } anchorRole)
            {
                var targetPos = Math.Max(1, anchorRole.Position - 1);
                if (targetPos < Context.Guild.CurrentUser.Hierarchy)
                {
                    await createdRole.ModifyAsync(p => p.Position = targetPos);
                }
            }

            var entity = await roleRepo.CreateWithPaymentAsync(
                Context.Guild.Id,
                member.Id,
                createdRole.Id,
                createdRole.Name,
                "#" + discordColor.RawValue.ToString("X6"),
                cfg.CustomRolePrice);

            if (entity is null)
            {
                await createdRole.DeleteAsync();
                await FollowupAsync(embed: embeds.Error(
                    $"Недостаточно средств. Создание кастомной роли стоит **{cfg.CustomRolePrice:N0} {cfg.CurrencySymbol}**."), ephemeral: true);
                return;
            }

            await member.AddRoleAsync(createdRole);
            await FollowupAsync(embed: embeds.Success("Кастомная роль создана",
                $"Вы создали личную роль <@&{createdRole.Id}> за **{cfg.CustomRolePrice:N0} {cfg.CurrencySymbol}**.\n" +
                "Используйте `/role manage` для управления цветом, названием и участниками роли."));
        }
    }

    [Group("role", "Управление личными кастомными ролями")]
    public sealed class RoleManageSubgroup(
        CustomRoleRepository roleRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("manage", "Открыть панель управления своими кастомными ролями")]
        public async Task ManageRolesCommandAsync()
        {
            var roles = await roleRepo.GetByOwnerAsync(Context.Guild.Id, Context.User.Id);
            if (roles.Count == 0)
            {
                await RespondAsync(embed: embeds.Error("У вас пока нет созданных кастомных ролей. Используйте `/create role`."), ephemeral: true);
                return;
            }

            var select = new SelectMenuBuilder()
                .WithCustomId("crole:select")
                .WithPlaceholder("Выберите вашу кастомную роль...");

            foreach (var r in roles.Take(25))
            {
                select.AddOption(r.Name, r.RoleId.ToString(), $"Цвет: {r.ColorHex} | ID: {r.RoleId}");
            }

            var first = roles[0];
            var embed = BuildRolePanelEmbed(first);
            var comp = BuildRoleActionComponents(select, first.RoleId);

            await RespondAsync(embed: embed, components: comp, ephemeral: true);
        }

        private Embed BuildRolePanelEmbed(Bot.Models.CustomRoleEntity role) =>
            embeds.CreateBase($"Управление кастомной ролью — {role.Name}")
                .AddField("Роль", $"<@&{role.RoleId}> (`{role.RoleId}`)", true)
                .AddField("Цвет", $"`{role.ColorHex}`", true)
                .AddField("Создана", TimestampTag.FromDateTimeOffset(role.CreatedAt, TimestampTagStyles.ShortDate), true)
                .Build();
    }

    public static MessageComponent BuildRoleActionComponents(SelectMenuBuilder? select, ulong roleId)
    {
        var builder = new ComponentBuilder();
        if (select is not null)
            builder.WithSelectMenu(select);

        builder
            .WithButton("Название", $"crole:name:{roleId}", ButtonStyle.Primary, row: 1)
            .WithButton("Цвет (HEX)", $"crole:color:{roleId}", ButtonStyle.Primary, row: 1)
            .WithButton("Иконка", $"crole:icon:{roleId}", ButtonStyle.Secondary, row: 1)
            .WithButton("Выдать участнику", $"crole:grant:{roleId}", ButtonStyle.Success, row: 2)
            .WithButton("Забрать у участника", $"crole:revoke:{roleId}", ButtonStyle.Secondary, row: 2)
            .WithButton("Удалить роль", $"crole:delete:{roleId}", ButtonStyle.Danger, row: 2);

        return builder.Build();
    }

    [ComponentInteraction("crole:select", true)]
    public async Task HandleRoleSelectAsync(string[] values)
    {
        if (!ulong.TryParse(values.FirstOrDefault(), out var roleId))
            return;

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        if (entity is null || entity.OwnerId != Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Роль не найдена или не принадлежит вам."), ephemeral: true);
            return;
        }

        var embed = embeds.CreateBase($"Управление кастомной ролью — {entity.Name}")
            .AddField("Роль", $"<@&{entity.RoleId}>", true)
            .AddField("Цвет", $"`{entity.ColorHex}`", true)
            .Build();

        await RespondAsync(embed: embed, components: BuildRoleActionComponents(null, entity.RoleId), ephemeral: true);
    }

    [ComponentInteraction("crole:*:*", true)]
    public async Task HandleRoleButtonAsync(string action, string roleIdStr)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId))
            return;

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        if (entity is null || entity.OwnerId != Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Эта роль вам не принадлежит."), ephemeral: true);
            return;
        }

        switch (action)
        {
            case "name":
                await RespondWithModalAsync<RoleRenameModal>($"crole_modal:name:{roleId}");
                break;
            case "color":
                await RespondWithModalAsync<RoleColorModal>($"crole_modal:color:{roleId}");
                break;
            case "icon":
                await RespondWithModalAsync<RoleIconModal>($"crole_modal:icon:{roleId}");
                break;
            case "grant":
                await RespondWithModalAsync<RoleMemberModal>($"crole_modal:grant:{roleId}");
                break;
            case "revoke":
                await RespondWithModalAsync<RoleMemberModal>($"crole_modal:revoke:{roleId}");
                break;
            case "delete":
                if (Context.Guild.GetRole(roleId) is { } socketRole)
                    await socketRole.DeleteAsync();
                await roleRepo.DeleteByRoleIdAsync(Context.Guild.Id, roleId);
                await RespondAsync(embed: embeds.Success("Роль удалена", $"Ваша кастомная роль **{entity.Name}** удалена."), ephemeral: true);
                break;
        }
    }

    [ModalInteraction("crole_modal:name:*", true)]
    public async Task HandleRoleRenameModalAsync(string roleIdStr, RoleRenameModal modal)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId))
            return;

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        if (entity is null || entity.OwnerId != Context.User.Id || Context.Guild.GetRole(roleId) is not { } role)
        {
            await RespondAsync(embed: embeds.Error("Роль не найдена."), ephemeral: true);
            return;
        }

        var cleanName = modal.Name.Trim();
        await role.ModifyAsync(p => p.Name = cleanName);
        await roleRepo.UpdateMetadataAsync(Context.Guild.Id, roleId, cleanName, entity.ColorHex);
        await RespondAsync(embed: embeds.Success("Название обновлено", $"Роль переименована в **{cleanName}**."), ephemeral: true);
    }

    [ModalInteraction("crole_modal:color:*", true)]
    public async Task HandleRoleColorModalAsync(string roleIdStr, RoleColorModal modal)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId))
            return;

        if (!EmbedFactory.TryParseColor(modal.Hex, out var color))
        {
            await RespondAsync(embed: embeds.Error("Неверный формат HEX-цвета. Пример: `#5865F2`."), ephemeral: true);
            return;
        }

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        if (entity is null || entity.OwnerId != Context.User.Id || Context.Guild.GetRole(roleId) is not { } role)
        {
            await RespondAsync(embed: embeds.Error("Роль не найдена."), ephemeral: true);
            return;
        }

        var hexFormatted = "#" + color.RawValue.ToString("X6");
        await role.ModifyAsync(p => p.Color = color);
        await roleRepo.UpdateMetadataAsync(Context.Guild.Id, roleId, entity.Name, hexFormatted);
        await RespondAsync(embed: embeds.Success("Цвет роли изменён", $"Установлен новый цвет `{hexFormatted}` для <@&{roleId}>."), ephemeral: true);
    }

    [ModalInteraction("crole_modal:icon:*", true)]
    public async Task HandleRoleIconModalAsync(string roleIdStr, RoleIconModal modal)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId) || Context.Guild.GetRole(roleId) is not { } role)
            return;

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        if (entity is null || entity.OwnerId != Context.User.Id)
            return;

        try
        {
            await role.ModifyAsync(p => p.Emoji = new Emoji(modal.Emoji.Trim()));
            await RespondAsync(embed: embeds.Success("Иконка установлена", $"Эмодзи-иконка роли <@&{roleId}> обновлена."), ephemeral: true);
        }
        catch
        {
            await RespondAsync(embed: embeds.Error("Сервер не поддерживает иконки ролей (требуется уровень буста 2+) или указан некорректный эмодзи."), ephemeral: true);
        }
    }

    [ModalInteraction("crole_modal:grant:*", true)]
    public async Task HandleRoleGrantModalAsync(string roleIdStr, RoleMemberModal modal)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId) || !ulong.TryParse(modal.UserId.Trim(), out var targetId))
        {
            await RespondAsync(embed: embeds.Error("Некорректный ID пользователя."), ephemeral: true);
            return;
        }

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        var targetMember = await Context.Guild.GetGuildUserSafeAsync(targetId);
        if (entity is null || entity.OwnerId != Context.User.Id ||
            Context.Guild.GetRole(roleId) is not { } role ||
            targetMember is null)
        {
            await RespondAsync(embed: embeds.Error("Роль или участник не найдены."), ephemeral: true);
            return;
        }

        await targetMember.AddRoleAsync(role);
        await RespondAsync(embed: embeds.Success("Роль выдана", $"Кастомная роль <@&{roleId}> выдана участнику <@{targetId}>."), ephemeral: true);
    }

    [ModalInteraction("crole_modal:revoke:*", true)]
    public async Task HandleRoleRevokeModalAsync(string roleIdStr, RoleMemberModal modal)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId) || !ulong.TryParse(modal.UserId.Trim(), out var targetId))
        {
            await RespondAsync(embed: embeds.Error("Некорректный ID пользователя."), ephemeral: true);
            return;
        }

        var entity = await roleRepo.GetByRoleIdAsync(Context.Guild.Id, roleId);
        var targetMember = await Context.Guild.GetGuildUserSafeAsync(targetId);
        if (entity is null || entity.OwnerId != Context.User.Id ||
            Context.Guild.GetRole(roleId) is not { } role ||
            targetMember is null)
        {
            await RespondAsync(embed: embeds.Error("Роль или участник не найдены."), ephemeral: true);
            return;
        }

        await targetMember.RemoveRoleAsync(role);
        await RespondAsync(embed: embeds.Success("Роль снята", $"Роль <@&{roleId}> забрана у участника <@{targetId}>."), ephemeral: true);
    }
}
