using Discord;
using Discord.WebSocket;

namespace Bot.Infrastructure;

public static class DiscordGuildExtensions
{
    public static async Task<IGuildUser?> GetGuildUserSafeAsync(this SocketGuild guild, ulong userId)
    {
        if (guild.GetUser(userId) is { } cached)
            return cached;

        try
        {
            return await ((IGuild)guild).GetUserAsync(userId);
        }
        catch
        {
            return null;
        }
    }
}
