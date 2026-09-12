using Bot.Infrastructure;
using Bot.Models;
using Dapper;

namespace Bot.Data;

public sealed class GuildSettingsRepository(DbConnectionFactory dbFactory, SettingsProvider settingsProvider)
{
    public async Task<GuildSettingsEntity> GetOrCreateAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<GuildSettingsEntity>(
            "SELECT * FROM guild_settings WHERE guild_id = @GuildId;",
            new { GuildId = guildId });

        if (existing is not null)
            return existing;

        var s = settingsProvider.Current;
        var entity = new GuildSettingsEntity(
            GuildId: guildId,
            EmbedColorHex: s.EmbedColorHex,
            CurrencyName: s.CurrencyName,
            CurrencySymbol: s.CurrencySymbol,
            CustomRolePrice: s.CustomRolePrice,
            CustomRoleAnchorId: s.CustomRoleAnchorRoleId,
            ModLogChannelId: s.Channels.ModLogChannelId,
            TicketLogChannelId: s.Channels.TicketLogChannelId,
            TicketPanelChannelId: s.Channels.TicketPanelChannelId,
            TicketCategoryId: s.Channels.TicketCategoryId,
            WelcomeChannelId: s.Channels.WelcomeChannelId,
            SecurityLogChannelId: s.Channels.SecurityLogChannelId,
            AdminRoleId: s.Roles.AdminRoleId,
            ModeratorRoleId: s.Roles.ModeratorRoleId,
            MutedRoleId: s.Roles.MutedRoleId,
            VerifiedRoleId: s.Roles.VerifiedRoleId,
            QuarantineRoleId: s.Roles.QuarantineRoleId,
            WarnThresholdMute: s.Moderation.WarnThresholdMute,
            WarnMuteMinutes: s.Moderation.WarnMuteDurationMinutes,
            WarnThresholdBan: s.Moderation.WarnThresholdBan,
            MaxOpenTickets: s.Moderation.MaxOpenTicketsPerUser,
            AntispamEnabled: s.Antispam.Enabled,
            AntispamMaxMessages: s.Antispam.MaxMessagesInWindow,
            AntispamWindowSeconds: s.Antispam.WindowSeconds,
            AntispamBlockInvites: s.Antispam.BlockDiscordInvites,
            AntispamBlockLinks: s.Antispam.BlockExternalLinks,
            AntinukeEnabled: s.Security.AntiNukeEnabled,
            AntiraidEnabled: s.Security.AntiRaidEnabled,
            CaptchaEnabled: s.Security.CaptchaEnabled,
            LockdownActive: false,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        await UpsertAsync(entity, ct);
        return entity;
    }

    public async Task UpsertAsync(GuildSettingsEntity entity, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.CreateOpenConnectionAsync(ct);
        await conn.ExecuteAsync(@"
            INSERT INTO guild_settings (
                guild_id, embed_color_hex, currency_name, currency_symbol, custom_role_price, custom_role_anchor_id,
                mod_log_channel_id, ticket_log_channel_id, ticket_panel_channel_id, ticket_category_id, welcome_channel_id, security_log_channel_id,
                admin_role_id, moderator_role_id, muted_role_id, verified_role_id, quarantine_role_id,
                warn_threshold_mute, warn_mute_minutes, warn_threshold_ban, max_open_tickets,
                antispam_enabled, antispam_max_messages, antispam_window_seconds, antispam_block_invites, antispam_block_links,
                antinuke_enabled, antiraid_enabled, captcha_enabled, lockdown_active, updated_at
            ) VALUES (
                @GuildId, @EmbedColorHex, @CurrencyName, @CurrencySymbol, @CustomRolePrice, @CustomRoleAnchorId,
                @ModLogChannelId, @TicketLogChannelId, @TicketPanelChannelId, @TicketCategoryId, @WelcomeChannelId, @SecurityLogChannelId,
                @AdminRoleId, @ModeratorRoleId, @MutedRoleId, @VerifiedRoleId, @QuarantineRoleId,
                @WarnThresholdMute, @WarnMuteMinutes, @WarnThresholdBan, @MaxOpenTickets,
                @AntispamEnabled, @AntispamMaxMessages, @AntispamWindowSeconds, @AntispamBlockInvites, @AntispamBlockLinks,
                @AntinukeEnabled, @AntiraidEnabled, @CaptchaEnabled, @LockdownActive, @UpdatedAt
            )
            ON CONFLICT(guild_id) DO UPDATE SET
                embed_color_hex = excluded.embed_color_hex,
                currency_name = excluded.currency_name,
                currency_symbol = excluded.currency_symbol,
                custom_role_price = excluded.custom_role_price,
                custom_role_anchor_id = excluded.custom_role_anchor_id,
                mod_log_channel_id = excluded.mod_log_channel_id,
                ticket_log_channel_id = excluded.ticket_log_channel_id,
                ticket_panel_channel_id = excluded.ticket_panel_channel_id,
                ticket_category_id = excluded.ticket_category_id,
                welcome_channel_id = excluded.welcome_channel_id,
                security_log_channel_id = excluded.security_log_channel_id,
                admin_role_id = excluded.admin_role_id,
                moderator_role_id = excluded.moderator_role_id,
                muted_role_id = excluded.muted_role_id,
                verified_role_id = excluded.verified_role_id,
                quarantine_role_id = excluded.quarantine_role_id,
                warn_threshold_mute = excluded.warn_threshold_mute,
                warn_mute_minutes = excluded.warn_mute_minutes,
                warn_threshold_ban = excluded.warn_threshold_ban,
                max_open_tickets = excluded.max_open_tickets,
                antispam_enabled = excluded.antispam_enabled,
                antispam_max_messages = excluded.antispam_max_messages,
                antispam_window_seconds = excluded.antispam_window_seconds,
                antispam_block_invites = excluded.antispam_block_invites,
                antispam_block_links = excluded.antispam_block_links,
                antinuke_enabled = excluded.antinuke_enabled,
                antiraid_enabled = excluded.antiraid_enabled,
                captcha_enabled = excluded.captcha_enabled,
                lockdown_active = excluded.lockdown_active,
                updated_at = excluded.updated_at;",
            entity);
    }
}
