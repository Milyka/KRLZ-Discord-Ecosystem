using System.Text;
using System.Text.RegularExpressions;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ProfileModule(
    ProfileCardService profileCardService,
    UserRepository userRepo,
    SocialRepository socialRepo,
    SocialExpansionRepository socialExpRepo,
    CustomRoleRepository customRoleRepo,
    ClanRepository clanRepo,
    AchievementRepository achievementRepo,
    ModerationRepository modRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly Regex HexColorRegex = new(@"^#?[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public sealed class BioModal : IModal
    {
        public string Title => "Изменение описания профиля";

        [InputLabel("Текст описания (Bio)")]
        [ModalTextInput("bio", TextInputStyle.Paragraph, "Расскажите коротко о себе...", minLength: 2, maxLength: 120)]
        public string Bio { get; set; } = string.Empty;
    }

    public sealed class AccentColorModal : IModal
    {
        public string Title => "Цвет неонового свечения карточки";

        [InputLabel("HEX-код цвета (например #38BDF8 или пусто)")]
        [ModalTextInput("color_hex", TextInputStyle.Short, "#38BDF8", minLength: 0, maxLength: 7)]
        public string ColorHex { get; set; } = string.Empty;
    }

    [SlashCommand("profile", "Открыть графическую карточку профиля участника KRLZ")]
    [RateLimit(4)]
    public async Task ProfileCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
    {
        await DeferAsync();
        var member = target ?? (SocketGuildUser)Context.User;
        var profile = await userRepo.GetOrCreateAsync(Context.Guild.Id, member.Id);
        var rank = await userRepo.GetUserRankByXpAsync(Context.Guild.Id, member.Id);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        var marriage = await socialRepo.GetMarriageAsync(Context.Guild.Id, member.Id);
        string? partnerName = null;
        string? partnerAvatarUrl = null;
        if (marriage is not null)
        {
            var partnerId = marriage.User1Id == member.Id ? marriage.User2Id : marriage.User1Id;
            var partnerUser = await Context.Guild.GetGuildUserSafeAsync(partnerId);
            partnerName = partnerUser?.DisplayName ?? $"ID {partnerId}";
            partnerAvatarUrl = partnerUser?.GetAvatarUrl(ImageFormat.Png, 128) ?? partnerUser?.GetDefaultAvatarUrl();
        }

        var customRoles = await customRoleRepo.GetByOwnerAsync(Context.Guild.Id, member.Id);
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, member.Id);
        var badges = await achievementRepo.GetEquippedBadgesAsync(Context.Guild.Id, member.Id);
        var allAchievements = await achievementRepo.GetUserAchievementsAsync(Context.Guild.Id, member.Id);
        var unlockedCount = allAchievements.Count(a => a.Progress.Unlocked);
        var rep = await socialExpRepo.GetOrCreateReputationAsync(Context.Guild.Id, member.Id);
        var pet = await socialExpRepo.GetUserPetAsync(Context.Guild.Id, member.Id);

        var pngBytes = await profileCardService.RenderProfileCardAsync(
            member,
            profile,
            rank,
            cfg.CurrencySymbol,
            partnerName,
            partnerAvatarUrl,
            customRoles.FirstOrDefault(),
            userClan?.Clan,
            badges,
            rep.Reputation,
            rep.AccentColorHex,
            unlockedCount,
            allAchievements.Count,
            pet);

        var comp = new ComponentBuilder()
            .WithButton("Сменить фон", $"prof:bg:{member.Id}", ButtonStyle.Primary)
            .WithButton("Цвет неона", $"prof:color:{member.Id}", ButtonStyle.Secondary)
            .WithButton("Изменить Bio", $"prof:bio:{member.Id}", ButtonStyle.Secondary)
            .WithButton("Мои варны", $"prof:warns:{member.Id}", ButtonStyle.Danger)
            .Build();

        await using var stream = new MemoryStream(pngBytes);
        await FollowupWithFileAsync(stream, $"profile-{member.Id}.png", components: comp);
    }

    [SlashCommand("rank", "Компактная графическая карточка вашего уровня и места в топе")]
    [RateLimit(3)]
    public async Task RankCommandAsync([Summary("user", "Участник сервера")] SocketGuildUser? target = null)
    {
        await DeferAsync();
        var member = target ?? (SocketGuildUser)Context.User;
        var profile = await userRepo.GetOrCreateAsync(Context.Guild.Id, member.Id);
        var rank = await userRepo.GetUserRankByXpAsync(Context.Guild.Id, member.Id);

        var png = await profileCardService.RenderRankCardAsync(member, profile, rank);
        await using var stream = new MemoryStream(png);
        await FollowupWithFileAsync(stream, $"rank-{member.Id}.png");
    }

    [SlashCommand("profile-color", "Установить персональный цвет неонового свечения вашей карточки профиля")]
    public async Task ProfileColorCommandAsync(
        [Summary("hex", "HEX-цвет (например #F43F5E) или reset для сброса")] string hex)
    {
        var raw = hex.Trim();
        if (raw.Equals("reset", StringComparison.OrdinalIgnoreCase) || raw.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            await socialExpRepo.SetAccentColorAsync(Context.Guild.Id, Context.User.Id, null);
            await RespondAsync(embed: embeds.Success("Цвет свечения сброшен", "Установлен стандартный неоновый цвет KRLZ (`#38BDF8`)."), ephemeral: true);
            return;
        }

        if (!HexColorRegex.IsMatch(raw))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректный HEX-цвет (например `#38BDF8`, `#F43F5E`, `#10B981`)."), ephemeral: true);
            return;
        }

        var normalized = raw.StartsWith('#') ? raw.ToUpperInvariant() : $"#{raw.ToUpperInvariant()}";
        var price = settingsProvider.Current.Social.CardColorChangePrice;
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (price > 0)
        {
            var paid = await userRepo.TryModifyBalanceAsync(Context.Guild.Id, Context.User.Id, -price, "profile_color", $"Смена цвета неона карточки на {normalized}");
            if (!paid)
            {
                await RespondAsync(embed: embeds.Error($"Недостаточно средств. Стоимость смены цвета неона: **{price:N0} {cfg.CurrencySymbol}**."), ephemeral: true);
                return;
            }
        }

        await socialExpRepo.SetAccentColorAsync(Context.Guild.Id, Context.User.Id, normalized);
        await RespondAsync(embed: embeds.Success("Цвет неона обновлён", $"Установлен цвет свечения `{normalized}`. Откройте `/profile`!"), ephemeral: true);
    }

    [SlashCommand("profile-badges", "Выбрать до 5 разблокированных значков достижений для карточки профиля")]
    public async Task ProfileBadgesCommandAsync(
        [Summary("codes", "Коды достижений через запятую (например: msg_100, voice_60) или пусто для сброса")] string? codesRaw = null)
    {
        var list = string.IsNullOrWhiteSpace(codesRaw)
            ? []
            : codesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var (ok, msg) = await achievementRepo.EquipBadgesAsync(Context.Guild.Id, Context.User.Id, list);
        if (!ok)
        {
            await RespondAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await RespondAsync(embed: embeds.Success("Значки профиля обновлены", msg), ephemeral: true);
    }

    [SlashCommand("profile-background", "Выбрать активный фон для вашей карточки профиля")]
    public async Task ProfileBackgroundCommandAsync(
        [Summary("background", "Фон карточки"),
         Choice("Стандартный тёмный (Default)", "default.png"),
         Choice("Киберпанк неон (Cyberpunk)", "cyberpunk.png"),
         Choice("Изумрудный (Emerald)", "emerald.png"),
         Choice("Багровый закат (Crimson)", "crimson.png"),
         Choice("Туманность (Nebula)", "nebula.png"),
         Choice("Солнечное Затмение (Solar)", "solar.png"),
         Choice("Золотая Бездна (Void Gold)", "void_gold.png")] string background)
    {
        await userRepo.UpdateBackgroundAsync(Context.Guild.Id, Context.User.Id, background);
        await RespondAsync(embed: embeds.Success("Фон профиля обновлён", $"Установлен фон `{background}`. Проверьте карточку через `/profile`!"), ephemeral: true);
    }

    [ComponentInteraction("prof:bg:*", true)]
    public async Task HandleChangeBgButtonAsync(string ownerIdStr)
    {
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || Context.User.Id != ownerId)
        {
            await RespondAsync(embed: embeds.Error("Вы можете менять оформление только собственного профиля."), ephemeral: true);
            return;
        }

        var select = new SelectMenuBuilder()
            .WithCustomId("prof:bg_select")
            .WithPlaceholder("Выберите тему оформления карточки...")
            .AddOption("Стандартный KRLZ", "default.png", "Классический тёмно-синий градиент")
            .AddOption("Киберпанк", "cyberpunk.png", "Неоновый пурпурный стиль")
            .AddOption("Изумруд", "emerald.png", "Глубокий изумрудный оттенок")
            .AddOption("Багровый", "crimson.png", "Тёмно-красный контрастный фон")
            .AddOption("Туманность", "nebula.png", "Космический фон из Батлпасса")
            .AddOption("Солнечное Затмение", "solar.png", "Огненный фон из Батлпасса")
            .AddOption("Золотая Бездна", "void_gold.png", "Легендарный фон 50 уровня Батлпасса");

        var comp = new ComponentBuilder().WithSelectMenu(select).Build();
        await RespondAsync(embed: embeds.Info("Выбор фона профиля", "Выберите желаемый фон из списка ниже:"), components: comp, ephemeral: true);
    }

    [ComponentInteraction("prof:bg_select", true)]
    public async Task HandleBgSelectMenuAsync(string[] values)
    {
        var bg = values.FirstOrDefault() ?? "default.png";
        await userRepo.UpdateBackgroundAsync(Context.Guild.Id, Context.User.Id, bg);
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Success("Фон изменён", $"Выбран фон `{bg}`. Вызовите `/profile`, чтобы увидеть обновлённую карточку.");
            m.Components = null;
        });
    }

    [ComponentInteraction("prof:color:*", true)]
    public async Task HandleColorButtonAsync(string ownerIdStr)
    {
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || Context.User.Id != ownerId)
        {
            await RespondAsync(embed: embeds.Error("Вы можете редактировать только свой профиль."), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<AccentColorModal>("prof:color_modal");
    }

    [ModalInteraction("prof:color_modal", true)]
    public async Task HandleColorModalSubmitAsync(AccentColorModal modal)
    {
        var raw = (modal.ColorHex ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            await socialExpRepo.SetAccentColorAsync(Context.Guild.Id, Context.User.Id, null);
            await RespondAsync(embed: embeds.Success("Цвет сброшен", "Восстановлен стандартный неоновый цвет `#38BDF8`."), ephemeral: true);
            return;
        }

        if (!HexColorRegex.IsMatch(raw))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректный HEX-цвет (например `#38BDF8` или `#F43F5E`)."), ephemeral: true);
            return;
        }

        var normalized = raw.StartsWith('#') ? raw.ToUpperInvariant() : $"#{raw.ToUpperInvariant()}";
        await socialExpRepo.SetAccentColorAsync(Context.Guild.Id, Context.User.Id, normalized);
        await RespondAsync(embed: embeds.Success("Цвет неона сохранён", $"Установлен цвет свечения `{normalized}`."), ephemeral: true);
    }

    [ComponentInteraction("prof:bio:*", true)]
    public async Task HandleBioButtonAsync(string ownerIdStr)
    {
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || Context.User.Id != ownerId)
        {
            await RespondAsync(embed: embeds.Error("Вы можете редактировать только свой профиль."), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<BioModal>("prof:bio_modal");
    }

    [ModalInteraction("prof:bio_modal", true)]
    public async Task HandleBioModalSubmitAsync(BioModal modal)
    {
        var clean = modal.Bio.Replace("\r", " ").Replace("\n", " ").Trim();
        await userRepo.UpdateBioAsync(Context.Guild.Id, Context.User.Id, clean);
        await RespondAsync(embed: embeds.Success("Описание обновлено", $"Ваше новое описание профиля: `{clean}`"), ephemeral: true);
    }

    [ComponentInteraction("prof:warns:*", true)]
    public async Task HandleShowWarnsButtonAsync(string targetIdStr)
    {
        if (!ulong.TryParse(targetIdStr, out var targetId))
            return;

        var warns = await modRepo.GetActiveWarnsAsync(Context.Guild.Id, targetId);
        if (warns.Count == 0)
        {
            await RespondAsync(embed: embeds.Success("Предупреждения отсутствуют", $"У участника <@{targetId}> нет активных предупреждений."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var w in warns)
        {
            sb.AppendLine($"• **#{w.Id}** ({TimestampTag.FromDateTimeOffset(w.CreatedAt, TimestampTagStyles.ShortDate)}): `{w.Reason}`");
        }

        await RespondAsync(embed: embeds.Warning($"Активные предупреждения ({warns.Count})", sb.ToString()), ephemeral: true);
    }
}
