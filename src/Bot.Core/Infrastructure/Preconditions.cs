using System.Collections.Concurrent;
using Bot.Data;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace Bot.Infrastructure;

public sealed class RequireGuildOwnerCustomAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        if (context.Guild is null)
            return Task.FromResult(PreconditionResult.FromError("Команда доступна только на сервере."));

        if (context.User.Id == context.Guild.OwnerId)
            return Task.FromResult(PreconditionResult.FromSuccess());

        return Task.FromResult(PreconditionResult.FromError("Доступно исключительно владельцу сервера."));
    }
}

public sealed class RequireAdminAttribute : PreconditionAttribute
{
    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        if (context.Guild is null || context.User is not SocketGuildUser member)
            return PreconditionResult.FromError("Команда доступна только на сервере.");

        if (member.Id == context.Guild.OwnerId)
            return PreconditionResult.FromSuccess();

        var settingsRepo = services.GetRequiredService<GuildSettingsRepository>();
        var cfg = await settingsRepo.GetOrCreateAsync(context.Guild.Id);

        if (cfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == cfg.AdminRoleId))
            return PreconditionResult.FromSuccess();

        return PreconditionResult.FromError("Требуется роль администратора сервера или права владельца.");
    }
}

public sealed class RequireModeratorAttribute : PreconditionAttribute
{
    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        if (context.Guild is null || context.User is not SocketGuildUser member)
            return PreconditionResult.FromError("Команда доступна только на сервере.");

        if (member.Id == context.Guild.OwnerId || member.GuildPermissions.Administrator)
            return PreconditionResult.FromSuccess();

        var settingsRepo = services.GetRequiredService<GuildSettingsRepository>();
        var cfg = await settingsRepo.GetOrCreateAsync(context.Guild.Id);

        var hasAdmin = cfg.AdminRoleId != 0 && member.Roles.Any(r => r.Id == cfg.AdminRoleId);
        var hasMod = cfg.ModeratorRoleId != 0 && member.Roles.Any(r => r.Id == cfg.ModeratorRoleId);

        if (hasAdmin || hasMod || member.GuildPermissions.ModerateMembers || member.GuildPermissions.KickMembers)
            return PreconditionResult.FromSuccess();

        return PreconditionResult.FromError("Требуется роль модератора или администратора сервера.");
    }
}

public sealed class RateLimitAttribute(int seconds) : PreconditionAttribute
{
    private static readonly ConcurrentDictionary<(ulong UserId, string CommandName), DateTimeOffset> Cooldowns = new();

    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context,
        ICommandInfo commandInfo,
        IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (context.User.Id, commandInfo.Name);

        if (Cooldowns.TryGetValue(key, out var expiresAt) && expiresAt > now)
        {
            var remaining = Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalSeconds));
            return Task.FromResult(PreconditionResult.FromError($"Подождите {remaining} сек. перед повторным использованием команды."));
        }

        Cooldowns[key] = now.AddSeconds(seconds);
        return Task.FromResult(PreconditionResult.FromSuccess());
    }
}

public static class HierarchyGuard
{
    public static bool CanModerate(SocketGuild guild, SocketGuildUser actor, IGuildUser target, out string error)
    {
        error = string.Empty;

        if (actor.Id == target.Id)
        {
            error = "Вы не можете применить это действие к себе.";
            return false;
        }

        if (target.Id == guild.OwnerId)
        {
            error = "Невозможно применить модерацию к владельцу сервера.";
            return false;
        }

        if (actor.Id != guild.OwnerId && actor.Hierarchy <= target.Hierarchy)
        {
            error = "Роль выбранного участника равна или выше вашей в иерархии.";
            return false;
        }

        if (guild.CurrentUser.Hierarchy <= target.Hierarchy)
        {
            error = "Роль выбранного участника равна или выше роли бота.";
            return false;
        }

        return true;
    }
}
