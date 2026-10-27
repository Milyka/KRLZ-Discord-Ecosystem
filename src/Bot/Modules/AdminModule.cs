using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class AdminModule(
    GuildSettingsRepository settingsRepo,
    PrivateVoiceRepository privateVoiceRepo,
    UserRepository userRepo,
    SettingsProvider settingsProvider,
    InteractionService interactions,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("setup", "Быстрая интерактивная настройка основных каналов, ролей и голосового хаба сервера KRLZ")]
    [RequireAdmin]
    public async Task SetupCommandAsync(
        [Summary("mod_log", "Канал логов модерации")] SocketTextChannel? modLog = null,
        [Summary("ticket_log", "Канал логов тикетов")] SocketTextChannel? ticketLog = null,
        [Summary("security_log", "Канал логов безопасности")] SocketTextChannel? securityLog = null,
        [Summary("welcome", "Канал приветствий")] SocketTextChannel? welcome = null,
        [Summary("app_review_channel", "Закрытый канал для проверки анкет модераторами")] SocketTextChannel? appReviewChannel = null,
        [Summary("ticket_category", "Категория для создания тикетов")] SocketCategoryChannel? ticketCategory = null,
        [Summary("admin_role", "Роль администратора бота")] SocketRole? adminRole = null,
        [Summary("mod_role", "Роль модератора бота")] SocketRole? modRole = null,
        [Summary("muted_role", "Роль мута")] SocketRole? mutedRole = null,
        [Summary("verified_role", "Роль верифицированного участника")] SocketRole? verifiedRole = null,
        [Summary("quarantine_role", "Роль карантина до капчи")] SocketRole? quarantineRole = null,
        [Summary("custom_role_anchor", "Роль, под которой размещаются кастомные роли")] SocketRole? customRoleAnchor = null,
        [Summary("voice_hub_channel", "Голосовой хаб-канал для создания приватных комнат")] SocketVoiceChannel? voiceHubChannel = null,
        [Summary("private_voice_category", "Категория для приватных голосовых комнат")] SocketCategoryChannel? privateVoiceCategory = null,
        [Summary("voice_control_channel", "Текстовый канал для панелей управления приватными войсами")] SocketTextChannel? voiceControlChannel = null,
        [Summary("voice_name_template", "Шаблон названия приватной комнаты (например: Комната {user})")] string? voiceNameTemplate = null)
    {
        await DeferAsync(ephemeral: true);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var updated = cfg with
        {
            ModLogChannelId = modLog?.Id ?? cfg.ModLogChannelId,
            TicketLogChannelId = ticketLog?.Id ?? cfg.TicketLogChannelId,
            SecurityLogChannelId = securityLog?.Id ?? cfg.SecurityLogChannelId,
            WelcomeChannelId = welcome?.Id ?? cfg.WelcomeChannelId,
            TicketCategoryId = ticketCategory?.Id ?? cfg.TicketCategoryId,
            AdminRoleId = adminRole?.Id ?? cfg.AdminRoleId,
            ModeratorRoleId = modRole?.Id ?? cfg.ModeratorRoleId,
            MutedRoleId = mutedRole?.Id ?? cfg.MutedRoleId,
            VerifiedRoleId = verifiedRole?.Id ?? cfg.VerifiedRoleId,
            QuarantineRoleId = quarantineRole?.Id ?? cfg.QuarantineRoleId,
            CustomRoleAnchorId = customRoleAnchor?.Id ?? cfg.CustomRoleAnchorId,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await settingsRepo.UpsertAsync(updated);

        var hubCfg = await privateVoiceRepo.GetOrCreateHubConfigAsync(Context.Guild.Id);
        var updatedHub = hubCfg with
        {
            HubChannelId = voiceHubChannel?.Id ?? hubCfg.HubChannelId,
            CategoryId = privateVoiceCategory?.Id ?? hubCfg.CategoryId,
            ControlChannelId = voiceControlChannel?.Id ?? hubCfg.ControlChannelId,
            NameTemplate = string.IsNullOrWhiteSpace(voiceNameTemplate) ? hubCfg.NameTemplate : voiceNameTemplate.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await privateVoiceRepo.UpsertHubConfigAsync(updatedHub);

        await settingsProvider.UpdateAsync(s =>
        {
            s.GuildId = Context.Guild.Id;
            s.Channels.ModLogChannelId = updated.ModLogChannelId;
            s.Channels.TicketLogChannelId = updated.TicketLogChannelId;
            s.Channels.SecurityLogChannelId = updated.SecurityLogChannelId;
            s.Channels.WelcomeChannelId = updated.WelcomeChannelId;
            s.Channels.TicketCategoryId = updated.TicketCategoryId;
            if (appReviewChannel is not null)
                s.Verification.ApplicationReviewChannelId = appReviewChannel.Id;
            s.Roles.AdminRoleId = updated.AdminRoleId;
            s.Roles.ModeratorRoleId = updated.ModeratorRoleId;
            s.Roles.MutedRoleId = updated.MutedRoleId;
            s.Roles.VerifiedRoleId = updated.VerifiedRoleId;
            s.Roles.QuarantineRoleId = updated.QuarantineRoleId;
            s.CustomRoleAnchorRoleId = updated.CustomRoleAnchorId;
            s.PrivateVoice.HubVoiceChannelId = updatedHub.HubChannelId;
            s.PrivateVoice.PrivateRoomsCategoryId = updatedHub.CategoryId;
            s.PrivateVoice.ControlTextChannelId = updatedHub.ControlChannelId;
            s.PrivateVoice.RoomNameTemplate = updatedHub.NameTemplate;
        });

        await FollowupAsync(embed: BuildConfigSummaryEmbed(updated, updatedHub), ephemeral: true);
    }

    [Group("config", "Просмотр и тонкая настройка параметров сервера")]
    [RequireAdmin]
    public sealed class ConfigSubgroup(
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("view", "Показать текущую конфигурацию сервера KRLZ")]
        public async Task ViewConfigCommandAsync()
        {
            await DeferAsync(ephemeral: true);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var s = settingsProvider.Current;

            var channelsInfo = string.Join(Environment.NewLine,
                $"Мод-лог: {FormatChannel(cfg.ModLogChannelId)}",
                $"Тикет-лог: {FormatChannel(cfg.TicketLogChannelId)}",
                $"Security-лог: {FormatChannel(cfg.SecurityLogChannelId)}",
                $"Приветствия: {FormatChannel(cfg.WelcomeChannelId)}",
                $"Лог сообщений: {FormatChannel(s.Logging.MessageLogChannelId)}",
                $"Лог войса: {FormatChannel(s.Logging.VoiceLogChannelId)}",
                $"Лог участников: {FormatChannel(s.Logging.MemberLogChannelId)}",
                $"Проверка анкет: {FormatChannel(s.Verification.ApplicationReviewChannelId)}");

            var rolesInfo = string.Join(Environment.NewLine,
                $"Админ: {FormatRole(cfg.AdminRoleId)}",
                $"Модератор: {FormatRole(cfg.ModeratorRoleId)}",
                $"Мут: {FormatRole(cfg.MutedRoleId)}",
                $"Верификация: {FormatRole(cfg.VerifiedRoleId)}",
                $"Карантин: {FormatRole(cfg.QuarantineRoleId)}");

            var expansionInfo = string.Join(Environment.NewLine,
                $"Создание клана: `{s.Clans.CreationPrice:N0} {cfg.CurrencySymbol}` | Комиссия аукциона: `{s.Auction.CommissionPercent}%`",
                $"Лимит ставок казино: `{s.Casino.MinBet:N0}` – `{s.Casino.MaxBet:N0}` | Дневной стоп-лосс: `{s.Casino.DailyLossLimit:N0}` | House Edge Краша: `{s.Casino.CrashHouseEdge:P1}`",
                $"Премиум Батлпасс: `{s.BattlePass.PremiumPrice:N0} {cfg.CurrencySymbol}` | Режим верификации: `{s.Verification.Mode}`");

            var embed = embeds.CreateBase("Текущая конфигурация сервера KRLZ")
                .AddField("Валюта", $"{cfg.CurrencyName} (`{cfg.CurrencySymbol}`)", true)
                .AddField("Цена кастомной роли", $"{cfg.CustomRolePrice:N0} {cfg.CurrencySymbol}", true)
                .AddField("Цвет Embed", $"`{cfg.EmbedColorHex}`", true)
                .AddField("Каналы логов", channelsInfo, true)
                .AddField("Ключевые роли", rolesInfo, true)
                .AddField("Модули расширения", expansionInfo, false)
                .Build();

            await FollowupAsync(embed: embed, ephemeral: true);
        }

        [SlashCommand("general", "Изменить базовые параметры темы, валюты и порогов предупреждений")]
        public async Task GeneralConfigCommandAsync(
            [Summary("embed_color", "HEX-цвет всех сообщений бота")] string? embedColor = null,
            [Summary("currency_name", "Название валюты")] string? currencyName = null,
            [Summary("currency_symbol", "Символ валюты")] string? currencySymbol = null,
            [Summary("role_price", "Цена создания кастомной роли")] long? rolePrice = null,
            [Summary("warn_mute", "Количество варнов для авто-мута")] int? warnMute = null,
            [Summary("warn_ban", "Количество варнов для авто-бана")] int? warnBan = null)
        {
            await DeferAsync(ephemeral: true);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            if (!string.IsNullOrWhiteSpace(embedColor) && !EmbedFactory.TryParseColor(embedColor, out _))
            {
                await FollowupAsync(embed: embeds.Error("Некорректный HEX-цвет."), ephemeral: true);
                return;
            }

            var updated = cfg with
            {
                EmbedColorHex = string.IsNullOrWhiteSpace(embedColor) ? cfg.EmbedColorHex : embedColor.Trim(),
                CurrencyName = string.IsNullOrWhiteSpace(currencyName) ? cfg.CurrencyName : currencyName.Trim(),
                CurrencySymbol = string.IsNullOrWhiteSpace(currencySymbol) ? cfg.CurrencySymbol : currencySymbol.Trim(),
                CustomRolePrice = rolePrice ?? cfg.CustomRolePrice,
                WarnThresholdMute = warnMute ?? cfg.WarnThresholdMute,
                WarnThresholdBan = warnBan ?? cfg.WarnThresholdBan,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await settingsRepo.UpsertAsync(updated);
            await settingsProvider.UpdateAsync(s =>
            {
                s.EmbedColorHex = updated.EmbedColorHex;
                s.CurrencyName = updated.CurrencyName;
                s.CurrencySymbol = updated.CurrencySymbol;
                s.CustomRolePrice = updated.CustomRolePrice;
                s.Moderation.WarnThresholdMute = updated.WarnThresholdMute;
                s.Moderation.WarnThresholdBan = updated.WarnThresholdBan;
            });

            await FollowupAsync(embed: embeds.Success("Конфигурация сохранена", "Общие параметры бота успешно обновлены в БД и `settings.json`."), ephemeral: true);
        }

        [SlashCommand("expansion", "Настроить цены и лимиты модулей Кланов, Аукциона, Казино, Логирования и Батлпасса")]
        public async Task ExpansionConfigCommandAsync(
            [Summary("clan_price", "Цена создания клана")] [MinValue(0)] long? clanPrice = null,
            [Summary("auction_fee", "Комиссия аукциона в процентах (0-50)")] [MinValue(0), MaxValue(50)] int? auctionFee = null,
            [Summary("casino_min_bet", "Минимальная ставка в казино")] [MinValue(1)] long? casinoMinBet = null,
            [Summary("casino_max_bet", "Максимальная ставка в казино")] [MinValue(10)] long? casinoMaxBet = null,
            [Summary("casino_loss_limit", "Дневной лимит проигрыша в казино")] [MinValue(100)] long? casinoLossLimit = null,
            [Summary("crash_house_edge", "Преимущество казино в Краше (0.01 - 0.20)")] double? crashHouseEdge = null,
            [Summary("battlepass_price", "Стоимость премиум-ветки Батлпасса")] [MinValue(0)] long? battlepassPrice = null,
            [Summary("verification_mode", "Режим верификации"), Choice("Капча-картинка", "captcha"), Choice("Анкета модераторам", "application")] string? verificationMode = null,
            [Summary("app_review_channel", "Закрытый канал для проверки анкет верификации")] SocketTextChannel? appReviewChannel = null,
            [Summary("message_log_channel", "Канал логов удалённых/изменённых сообщений")] SocketTextChannel? messageLogChannel = null,
            [Summary("voice_log_channel", "Канал логов входов/выходов войса")] SocketTextChannel? voiceLogChannel = null,
            [Summary("member_log_channel", "Канал логов смены ников и ролей")] SocketTextChannel? memberLogChannel = null,
            [Summary("db_backup_channel", "Приватный канал для отправки копий SQLite")] SocketTextChannel? dbBackupChannel = null)
        {
            await DeferAsync(ephemeral: true);
            await settingsProvider.UpdateAsync(s =>
            {
                if (clanPrice.HasValue) s.Clans.CreationPrice = clanPrice.Value;
                if (auctionFee.HasValue) s.Auction.CommissionPercent = auctionFee.Value;
                if (casinoMinBet.HasValue) s.Casino.MinBet = casinoMinBet.Value;
                if (casinoMaxBet.HasValue) s.Casino.MaxBet = casinoMaxBet.Value;
                if (casinoLossLimit.HasValue) s.Casino.DailyLossLimit = casinoLossLimit.Value;
                if (crashHouseEdge.HasValue) s.Casino.CrashHouseEdge = Math.Clamp(crashHouseEdge.Value, 0.01, 0.20);
                if (battlepassPrice.HasValue) s.BattlePass.PremiumPrice = battlepassPrice.Value;
                if (!string.IsNullOrWhiteSpace(verificationMode)) s.Verification.Mode = verificationMode;
                if (appReviewChannel is not null) s.Verification.ApplicationReviewChannelId = appReviewChannel.Id;
                if (messageLogChannel is not null) s.Logging.MessageLogChannelId = messageLogChannel.Id;
                if (voiceLogChannel is not null) s.Logging.VoiceLogChannelId = voiceLogChannel.Id;
                if (memberLogChannel is not null) s.Logging.MemberLogChannelId = memberLogChannel.Id;
                if (dbBackupChannel is not null) s.DatabaseBackup.BackupChannelId = dbBackupChannel.Id;
            });

            var cur = settingsProvider.Current;
            var summary = string.Join(Environment.NewLine,
                $"• **Цена клана:** {cur.Clans.CreationPrice:N0} {cur.CurrencySymbol}",
                $"• **Комиссия аукциона:** {cur.Auction.CommissionPercent}%",
                $"• **Ставки казино:** {cur.Casino.MinBet:N0} – {cur.Casino.MaxBet:N0} (стоп-лосс: {cur.Casino.DailyLossLimit:N0}, House Edge: {cur.Casino.CrashHouseEdge:P1})",
                $"• **Премиум Батлпасс:** {cur.BattlePass.PremiumPrice:N0} {cur.CurrencySymbol}",
                $"• **Режим верификации:** `{cur.Verification.Mode}`");

            await FollowupAsync(embed: embeds.Success("Параметры модулей обновлены", summary), ephemeral: true);
        }

        private static string FormatChannel(ulong id) => id == 0 ? "`не задан`" : $"<#{id}>";
        private static string FormatRole(ulong id) => id == 0 ? "`не задана`" : $"<@&{id}>";
    }

    [Group("antispam", "Управление параметрами антиспам-фильтра")]
    [RequireAdmin]
    public sealed class AntispamConfigSubgroup(
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("config", "Настроить лимиты и правила антиспам-системы")]
        public async Task AntispamConfigCommandAsync(
            [Summary("enabled", "Включить или выключить антиспам")] bool enabled,
            [Summary("max_messages", "Макс. сообщений за окно времени")] [MinValue(2), MaxValue(30)] int maxMessages = 6,
            [Summary("window_seconds", "Длительность окна в секундах")] [MinValue(2), MaxValue(60)] int windowSeconds = 5,
            [Summary("block_invites", "Блокировать инвайты Discord")] bool blockInvites = true,
            [Summary("block_links", "Блокировать ссылки вне белого списка")] bool blockLinks = true)
        {
            await DeferAsync(ephemeral: true);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var updated = cfg with
            {
                AntispamEnabled = enabled,
                AntispamMaxMessages = maxMessages,
                AntispamWindowSeconds = windowSeconds,
                AntispamBlockInvites = blockInvites,
                AntispamBlockLinks = blockLinks,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await settingsRepo.UpsertAsync(updated);
            await settingsProvider.UpdateAsync(s =>
            {
                s.Antispam.Enabled = enabled;
                s.Antispam.MaxMessagesInWindow = maxMessages;
                s.Antispam.WindowSeconds = windowSeconds;
                s.Antispam.BlockDiscordInvites = blockInvites;
                s.Antispam.BlockExternalLinks = blockLinks;
            });

            await FollowupAsync(embed: embeds.Success("Антиспам обновлён",
                $"**Статус:** {(enabled ? "Включён" : "Выключен")}\n" +
                $"**Лимит:** {maxMessages} сообщ. за {windowSeconds} сек.\n" +
                $"**Блок инвайтов:** {(blockInvites ? "Да" : "Нет")} | **Блок ссылок:** {(blockLinks ? "Да" : "Нет")}"), ephemeral: true);
        }
    }

    [Group("economy", "Административное управление балансами участников")]
    [RequireGuildOwnerCustom]
    public sealed class EconomyAdminSubgroup(
        UserRepository userRepo,
        GuildSettingsRepository settingsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("give", "Начислить валюту участнику сервера")]
        public async Task GiveCommandAsync(
            [Summary("user", "Участник")] SocketGuildUser target,
            [Summary("amount", "Сумма начисления")] [MinValue(1)] long amount,
            [Summary("reason", "Причина")] string reason = "Начисление администратором")
        {
            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            await userRepo.TryModifyBalanceAsync(Context.Guild.Id, target.Id, amount, "admin_give", reason);
            await FollowupAsync(embed: embeds.Success("Средства начислены",
                $"Участнику <@{target.Id}> начислено **{amount:N0} {cfg.CurrencySymbol}**.\n**Причина:** {reason}"));
        }

        [SlashCommand("take", "Списать валюту с баланса участника")]
        public async Task TakeCommandAsync(
            [Summary("user", "Участник")] SocketGuildUser target,
            [Summary("amount", "Сумма списания")] [MinValue(1)] long amount,
            [Summary("reason", "Причина")] string reason = "Списание администратором")
        {
            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var ok = await userRepo.TryModifyBalanceAsync(Context.Guild.Id, target.Id, -amount, "admin_take", reason);
            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error("У участника недостаточно средств для списания указанной суммы."), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Средства списаны",
                $"С баланса <@{target.Id}> списано **{amount:N0} {cfg.CurrencySymbol}**.\n**Причина:** {reason}"));
        }

        [SlashCommand("reset", "Обнулить баланс участника")]
        public async Task ResetCommandAsync([Summary("user", "Участник")] SocketGuildUser target)
        {
            await DeferAsync();
            await userRepo.ResetBalanceAsync(Context.Guild.Id, target.Id);
            await FollowupAsync(embed: embeds.Warning("Баланс обнулён", $"Баланс участника <@{target.Id}> сброшен до **0**."));
        }
    }

    [SlashCommand("sync", "Принудительно синхронизировать слэш-команды с текущей гильдией")]
    [RequireAdmin]
    public async Task SyncCommandsAsync()
    {
        await DeferAsync(ephemeral: true);
        await interactions.RegisterCommandsToGuildAsync(Context.Guild.Id, true);
        await FollowupAsync(embed: embeds.Success("Синхронизация выполнена", "Все слэш-команды зарегистрированы на сервере."), ephemeral: true);
    }

    private Embed BuildConfigSummaryEmbed(Bot.Models.GuildSettingsEntity cfg, Bot.Models.VoiceHubConfigEntity hub) =>
        embeds.Success("Настройки сервера KRLZ сохранены", string.Join(Environment.NewLine,
            $"**Мод-лог:** {(cfg.ModLogChannelId != 0 ? $"<#{cfg.ModLogChannelId}>" : "не задан")}",
            $"**Тикет-лог:** {(cfg.TicketLogChannelId != 0 ? $"<#{cfg.TicketLogChannelId}>" : "не задан")}",
            $"**Security-лог:** {(cfg.SecurityLogChannelId != 0 ? $"<#{cfg.SecurityLogChannelId}>" : "не задан")}",
            $"**Приветствия:** {(cfg.WelcomeChannelId != 0 ? $"<#{cfg.WelcomeChannelId}>" : "не задан")}",
            $"**Проверка анкет:** {(settingsProvider.Current.Verification.ApplicationReviewChannelId != 0 ? $"<#{settingsProvider.Current.Verification.ApplicationReviewChannelId}>" : "не задан")}",
            $"**Роль Админа:** {(cfg.AdminRoleId != 0 ? $"<@&{cfg.AdminRoleId}>" : "не задана")}",
            $"**Роль Модератора:** {(cfg.ModeratorRoleId != 0 ? $"<@&{cfg.ModeratorRoleId}>" : "не задана")}",
            $"**Хаб приватных войсов:** {(hub.HubChannelId != 0 ? $"<#{hub.HubChannelId}> (`{hub.NameTemplate}`)" : "не задан")}"));
}
