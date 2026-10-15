using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Bot.Services;

public sealed class AuctionService(
    AuctionRepository auctionRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<AuctionService> logger)
{
    public async Task TransferDiscordRoleOwnershipAsync(
        SocketGuild guild,
        ulong roleId,
        ulong oldOwnerId,
        ulong newOwnerId)
    {
        try
        {
            var role = guild.GetRole(roleId);
            if (role is null || guild.CurrentUser.Hierarchy <= role.Position)
                return;

            var oldMember = await guild.GetGuildUserSafeAsync(oldOwnerId);
            if (oldMember is not null && oldMember.RoleIds.Contains(roleId))
            {
                await oldMember.RemoveRoleAsync(roleId);
            }

            var newMember = await guild.GetGuildUserSafeAsync(newOwnerId);
            if (newMember is not null && !newMember.RoleIds.Contains(roleId))
            {
                await newMember.AddRoleAsync(roleId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось переназначить роль {RoleId} в Discord при продаже на аукционе", roleId);
        }
    }

    public async Task ProcessExpiredAuctionsAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await auctionRepo.GetExpiredActiveAuctionsAsync(now, ct);
        if (expired.Count == 0)
            return;

        var feePct = settingsProvider.Current.Auction.CommissionPercent;

        foreach (var lot in expired)
        {
            try
            {
                var (sold, sellerPayout) = await auctionRepo.FinalizeExpiredAuctionAsync(lot, feePct, ct);
                var guild = client.GetGuild(lot.GuildId);
                if (guild is null)
                    continue;

                var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);

                if (sold && lot.HighestBidderId.HasValue)
                {
                    if (lot.LotType == "role" && lot.CustomRoleId.HasValue)
                    {
                        await TransferDiscordRoleOwnershipAsync(guild, lot.CustomRoleId.Value, lot.SellerId, lot.HighestBidderId.Value);
                    }

                    await TryNotifyUserAsync(guild, lot.SellerId,
                        "Лот продан на аукционе!",
                        $"Ваш лот `#{lot.Id}` (**{lot.LotTitle}**) куплен участником <@{lot.HighestBidderId.Value}> за **{lot.CurrentBid:N0} {cfg.CurrencySymbol}**.\n" +
                        $"Зачислено с учётом комиссии ({feePct}%): **{sellerPayout:N0} {cfg.CurrencySymbol}**.");

                    await TryNotifyUserAsync(guild, lot.HighestBidderId.Value,
                        "Победа на аукционе KRLZ!",
                        $"Вы выиграли торги по лоту `#{lot.Id}` (**{lot.LotTitle}**) со ставкой **{lot.CurrentBid:N0} {cfg.CurrencySymbol}**!");
                }
                else
                {
                    await TryNotifyUserAsync(guild, lot.SellerId,
                        "Срок торгов по лоту истёк",
                        $"По вашему лоту `#{lot.Id}` (**{lot.LotTitle}**) не поступило ставок. Предмет/роль возвращены вам.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка завершения аукционного лота #{LotId}", lot.Id);
            }
        }
    }

    public async Task NotifyOutbidUserAsync(
        SocketGuild guild,
        ulong outbidUserId,
        AuctionEntity lot,
        long newBidAmount,
        long refundedAmount,
        CancellationToken ct = default)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(guild.Id, ct);
        await TryNotifyUserAsync(guild, outbidUserId,
            "Ваша ставка перебита!",
            $"Ваша ставка по лоту `#{lot.Id}` (**{lot.LotTitle}**) была перебита новой ставкой **{newBidAmount:N0} {cfg.CurrencySymbol}**.\n" +
            $"Сумма **{refundedAmount:N0} {cfg.CurrencySymbol}** мгновенно возвращена на ваш баланс.");
    }

    private async Task TryNotifyUserAsync(SocketGuild guild, ulong userId, string title, string text)
    {
        try
        {
            var user = await guild.GetGuildUserSafeAsync(userId);
            if (user is not null)
                await user.SendMessageAsync(embed: embeds.Info(title, text));
        }
        catch
        {
        }
    }
}
