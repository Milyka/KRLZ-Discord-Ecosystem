using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
[Group("clan", "Клановая система сервера KRLZ: штабы, казна, ранги и войны")]
public sealed class ClanModule(
    ClanService clanService,
    ClanRepository clanRepo,
    ProfileCardService profileCardService,
    GuildSettingsRepository settingsRepo,
    AchievementService achievementService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class ClanColorUpgradeModal : IModal
    {
        public string Title => "Смена цвета клана";

        [InputLabel("Новый HEX-код цвета (например #FF0055)")]
        [ModalTextInput("hex", TextInputStyle.Short, "#FF0055", minLength: 6, maxLength: 7)]
        public string Hex { get; set; } = string.Empty;
    }

    [SlashCommand("create", "Основать собственный клан с ролью и приватным каналом")]
    public async Task CreateClanCommandAsync(
        [Summary("name", "Название клана (3-32 символа)")] string name,
        [Summary("tag", "Короткий тег клана (2-5 символов)")] string tag,
        [Summary("color", "HEX-цвет клана (например #5865F2)")] string color = "#5865F2",
        [Summary("description", "Девиз или описание клана")] string description = "Клан сервера KRLZ")
    {
        await DeferAsync();
        var member = (SocketGuildUser)Context.User;
        var (ok, msg, _) = await clanService.CreateClanAsync(Context.Guild, member, name, tag, description, color);

        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Клан основан!", msg));
    }

    [SlashCommand("info", "Графическая карточка и подробная информация о клане")]
    public async Task ClanInfoCommandAsync([Summary("query", "Название или тег клана (пусто = ваш клан)")] string? query = null)
    {
        await DeferAsync();
        ClanEntity? clan;
        if (string.IsNullOrWhiteSpace(query))
        {
            var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
            if (userClan is null)
            {
                await FollowupAsync(embed: embeds.Error("Вы не состоите в клане. Укажите название или тег клана в параметре `query`."), ephemeral: true);
                return;
            }
            clan = userClan.Value.Clan;
        }
        else
        {
            clan = await clanRepo.GetByNameOrTagAsync(Context.Guild.Id, query);
            if (clan is null)
            {
                await FollowupAsync(embed: embeds.Error($"Клан `{query}` не найден."), ephemeral: true);
                return;
            }
        }

        var members = await clanRepo.GetMembersAsync(clan.Id);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var leaderName = Context.Guild.GetUser(clan.LeaderId)?.DisplayName ?? $"ID {clan.LeaderId}";

        var cardBytes = await profileCardService.RenderClanCardAsync(
            clan, leaderName, members.Count, cfg.CurrencySymbol);

        var officers = members.Where(m => m.Rank == "officer").Select(m => $"<@{m.UserId}>").ToList();
        var customColor = EmbedFactory.TryParseColor(clan.ColorHex, out var c) ? c : (Color?)null;

        var embed = embeds.CreateBase($"Клан [{clan.Tag}] {clan.Name}", clan.Description, customColor)
            .AddField("Лидер", $"<@{clan.LeaderId}>", true)
            .AddField("Участники", $"{members.Count} / {clan.MemberLimit}", true)
            .AddField("Роль и штаб", $"<@&{clan.RoleId}> • <#{clan.ChannelId}>", true)
            .AddField($"Офицеры ({officers.Count}/{clan.OfficerSlots})", officers.Count > 0 ? string.Join(", ", officers) : "Не назначены", false)
            .WithImageUrl("attachment://clan-card.png")
            .Build();

        await using var stream = new MemoryStream(cardBytes);
        await FollowupWithFileAsync(stream, "clan-card.png", embed: embed);
    }

    [SlashCommand("invite", "Отправить приглашение в клан участнику сервера")]
    public async Task InviteCommandAsync([Summary("user", "Приглашаемый участник")] SocketGuildUser target)
    {
        if (target.IsBot || target.Id == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Нельзя пригласить бота или самого себя."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var actorClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (actorClan is null || actorClan.Value.Member.Rank is not ("leader" or "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Приглашать новых участников могут только лидер и офицеры клана."), ephemeral: true);
            return;
        }

        var targetClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, target.Id);
        if (targetClan is not null)
        {
            await FollowupAsync(embed: embeds.Error("Этот участник уже состоит в другом клане."), ephemeral: true);
            return;
        }

        var clan = actorClan.Value.Clan;
        clanService.RegisterInvite(Context.Guild.Id, clan.Id, target.Id);

        var embed = embeds.CreateBase($"Приглашение в клан [{clan.Tag}] {clan.Name}")
            .WithDescription($"<@{Context.User.Id}> приглашает <@{target.Id}> вступить в ряды клана **[{clan.Tag}] {clan.Name}**!\nПриглашение действует 15 минут.")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Принять приглашение", $"clan:join:{clan.Id}:{target.Id}", ButtonStyle.Success)
            .WithButton("Отклонить", $"clan:decline:{clan.Id}:{target.Id}", ButtonStyle.Secondary)
            .Build();

        await FollowupAsync(text: $"<@{target.Id}>", embed: embed, components: comp);
    }

    [SlashCommand("join", "Вступить в клан по активному приглашению")]
    public async Task JoinClanCommandAsync([Summary("clan_name_or_tag", "Название или тег клана")] string clanQuery)
    {
        await DeferAsync();
        var clan = await clanRepo.GetByNameOrTagAsync(Context.Guild.Id, clanQuery);
        if (clan is null)
        {
            await FollowupAsync(embed: embeds.Error("Клан не найден."), ephemeral: true);
            return;
        }

        if (!clanService.TryConsumeInvite(Context.Guild.Id, Context.User.Id, clan.Id))
        {
            await FollowupAsync(embed: embeds.Error("У вас нет активного приглашения в этот клан или его срок истёк."), ephemeral: true);
            return;
        }

        var (ok, err) = await clanRepo.AddMemberAsync(Context.Guild.Id, clan.Id, Context.User.Id);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        if (clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role && Context.Guild.CurrentUser.Hierarchy > role.Position)
            await member.AddRoleAsync(role);

        await achievementService.TrackEventAsync(Context.Guild, member.Id, "clan_member", 1, isAbsoluteValue: true);
        await FollowupAsync(embed: embeds.Success("Добро пожаловать в клан!", $"Вы вступили в клан **[{clan.Tag}] {clan.Name}**."));
    }

    [ComponentInteraction("clan:join:*:*", true)]
    public async Task HandleJoinButtonAsync(string clanIdStr, string targetIdStr)
    {
        if (!long.TryParse(clanIdStr, out var clanId) || !ulong.TryParse(targetIdStr, out var targetId))
            return;

        if (Context.User.Id != targetId)
        {
            await RespondAsync(embed: embeds.Error("Это приглашение адресовано другому участнику."), ephemeral: true);
            return;
        }

        await DeferAsync();

        if (!clanService.TryConsumeInvite(Context.Guild.Id, targetId, clanId))
        {
            await FollowupAsync(embed: embeds.Error("Срок действия приглашения истёк."), ephemeral: true);
            return;
        }

        var clan = await clanRepo.GetByIdAsync(Context.Guild.Id, clanId);
        if (clan is null)
        {
            await FollowupAsync(embed: embeds.Error("Клан не найден."), ephemeral: true);
            return;
        }

        var (ok, err) = await clanRepo.AddMemberAsync(Context.Guild.Id, clan.Id, targetId);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        if (clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role && Context.Guild.CurrentUser.Hierarchy > role.Position)
            await member.AddRoleAsync(role);

        await achievementService.TrackEventAsync(Context.Guild, member.Id, "clan_member", 1, isAbsoluteValue: true);

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Приглашение принято", $"<@{targetId}> присоединился к клану **[{clan.Tag}] {clan.Name}**!");
            m.Components = null;
        });
    }

    [ComponentInteraction("clan:decline:*:*", true)]
    public async Task HandleDeclineButtonAsync(string clanIdStr, string targetIdStr)
    {
        if (!ulong.TryParse(targetIdStr, out var targetId) || Context.User.Id != targetId)
        {
            await RespondAsync(embed: embeds.Error("Отклонить приглашение может только приглашённый участник."), ephemeral: true);
            return;
        }

        await DeferAsync();
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Warning("Приглашение отклонено", $"<@{targetId}> отклонил приглашение в клан.");
            m.Components = null;
        });
    }

    [SlashCommand("leave", "Покинуть текущий клан")]
    public async Task LeaveClanCommandAsync()
    {
        await DeferAsync();
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null)
        {
            await FollowupAsync(embed: embeds.Error("Вы не состоите в клане."), ephemeral: true);
            return;
        }

        var (clan, memberRecord) = userClan.Value;
        if (memberRecord.Rank == "leader")
        {
            await FollowupAsync(embed: embeds.Error("Лидер не может покинуть клан. Передайте лидерство через `/clan promote` или распустите клан через `/clan disband`."), ephemeral: true);
            return;
        }

        await clanRepo.RemoveMemberAsync(Context.Guild.Id, clan.Id, Context.User.Id);
        var member = (SocketGuildUser)Context.User;
        if (clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role && member.Roles.Any(r => r.Id == role.Id))
            await member.RemoveRoleAsync(role);

        await FollowupAsync(embed: embeds.Warning("Выход из клана", $"Вы покинули клан **[{clan.Tag}] {clan.Name}**."));
    }

    [SlashCommand("kick", "Исключить участника из клана")]
    public async Task KickMemberCommandAsync([Summary("user", "Участник клана")] SocketGuildUser target)
    {
        await DeferAsync();
        var actorClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        var targetClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, target.Id);

        if (actorClan is null || targetClan is null || actorClan.Value.Clan.Id != targetClan.Value.Clan.Id)
        {
            await FollowupAsync(embed: embeds.Error("Этот пользователь не состоит в вашем клане."), ephemeral: true);
            return;
        }

        var actorRank = actorClan.Value.Member.Rank;
        var targetRank = targetClan.Value.Member.Rank;

        if (actorRank == "member" || targetRank == "leader" || (actorRank == "officer" && targetRank == "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Недостаточно полномочий для исключения этого участника клана."), ephemeral: true);
            return;
        }

        var clan = actorClan.Value.Clan;
        await clanRepo.RemoveMemberAsync(Context.Guild.Id, clan.Id, target.Id);
        if (clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role && target.Roles.Any(r => r.Id == role.Id))
            await target.RemoveRoleAsync(role);

        await FollowupAsync(embed: embeds.Success("Участник исключён из клана", $"<@{target.Id}> исключён из клана **[{clan.Tag}] {clan.Name}**."));
    }

    [SlashCommand("promote", "Повысить участника клана до офицера или передать лидерство")]
    public async Task PromoteMemberCommandAsync([Summary("user", "Участник клана")] SocketGuildUser target)
    {
        await DeferAsync();
        var actorClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        var targetClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, target.Id);

        if (actorClan is null || actorClan.Value.Member.Rank != "leader")
        {
            await FollowupAsync(embed: embeds.Error("Повышать участников может только лидер клана."), ephemeral: true);
            return;
        }

        if (targetClan is null || targetClan.Value.Clan.Id != actorClan.Value.Clan.Id || target.Id == Context.User.Id)
        {
            await FollowupAsync(embed: embeds.Error("Участник должен состоять в вашем клане."), ephemeral: true);
            return;
        }

        var clan = actorClan.Value.Clan;
        if (targetClan.Value.Member.Rank == "member")
        {
            var allMembers = await clanRepo.GetMembersAsync(clan.Id);
            var currentOfficers = allMembers.Count(m => m.Rank == "officer");
            if (currentOfficers >= clan.OfficerSlots)
            {
                await FollowupAsync(embed: embeds.Error($"Все слоты офицеров заняты ({currentOfficers}/{clan.OfficerSlots}). Расширьте их через `/clan upgrade`."), ephemeral: true);
                return;
            }

            await clanRepo.UpdateMemberRankAsync(Context.Guild.Id, clan.Id, target.Id, "officer");
            await FollowupAsync(embed: embeds.Success("Повышение в клане", $"<@{target.Id}> назначен **офицером** клана **[{clan.Tag}] {clan.Name}**!"));
        }
        else if (targetClan.Value.Member.Rank == "officer")
        {
            await clanRepo.TransferLeadershipAsync(Context.Guild.Id, clan.Id, Context.User.Id, target.Id);
            await FollowupAsync(embed: embeds.Success("Передача лидерства", $"<@{target.Id}> стал новым **лидером** клана **[{clan.Tag}] {clan.Name}**!"));
        }
    }

    [SlashCommand("demote", "Понизить офицера клана до рядового участника")]
    public async Task DemoteMemberCommandAsync([Summary("user", "Офицер клана")] SocketGuildUser target)
    {
        await DeferAsync();
        var actorClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        var targetClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, target.Id);

        if (actorClan is null || actorClan.Value.Member.Rank != "leader")
        {
            await FollowupAsync(embed: embeds.Error("Понижать офицеров может только лидер клана."), ephemeral: true);
            return;
        }

        if (targetClan is null || targetClan.Value.Clan.Id != actorClan.Value.Clan.Id || targetClan.Value.Member.Rank != "officer")
        {
            await FollowupAsync(embed: embeds.Error("Выбранный пользователь не является офицером вашего клана."), ephemeral: true);
            return;
        }

        await clanRepo.UpdateMemberRankAsync(Context.Guild.Id, actorClan.Value.Clan.Id, target.Id, "member");
        await FollowupAsync(embed: embeds.Info("Понижение в должности", $"<@{target.Id}> переведён в рядовые участники клана."));
    }

    [SlashCommand("deposit", "Внести средства с личного баланса в казну клана")]
    public async Task DepositCommandAsync([Summary("amount", "Сумма взноса")] [MinValue(10)] long amount)
    {
        await DeferAsync();
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null)
        {
            await FollowupAsync(embed: embeds.Error("Вы не состоите в клане."), ephemeral: true);
            return;
        }

        var (ok, err) = await clanRepo.DepositToTreasuryAsync(Context.Guild.Id, userClan.Value.Clan.Id, Context.User.Id, amount);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await FollowupAsync(embed: embeds.Success("Казна пополнена",
            $"Вы внесли **{amount:N0} {cfg.CurrencySymbol}** в казну клана **[{userClan.Value.Clan.Tag}] {userClan.Value.Clan.Name}**."));
    }

    [SlashCommand("withdraw", "Вывести средства из казны клана (только для лидера)")]
    public async Task WithdrawCommandAsync([Summary("amount", "Сумма вывода")] [MinValue(10)] long amount)
    {
        await DeferAsync();
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Member.Rank != "leader")
        {
            await FollowupAsync(embed: embeds.Error("Выводить средства из казны может только лидер клана."), ephemeral: true);
            return;
        }

        var (ok, err) = await clanRepo.WithdrawFromTreasuryAsync(Context.Guild.Id, userClan.Value.Clan.Id, Context.User.Id, amount);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await FollowupAsync(embed: embeds.Success("Средства выведены",
            $"Лидер <@{Context.User.Id}> вывел **{amount:N0} {cfg.CurrencySymbol}** из казны клана."));
    }

    [SlashCommand("upgrade", "Открыть меню улучшений клана за счёт казны")]
    public async Task UpgradeClanCommandAsync()
    {
        await DeferAsync(ephemeral: true);
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Member.Rank is not ("leader" or "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Управлять улучшениями могут только лидер и офицеры клана."), ephemeral: true);
            return;
        }

        var clan = userClan.Value.Clan;
        var clanCfg = settingsProvider.Current.Clans;
        var guildCfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        var desc = string.Join(Environment.NewLine,
            $"**Текущая казна клана:** `{clan.Treasury:N0} {guildCfg.CurrencySymbol}`",
            string.Empty,
            $"• **Расширение штата (+5 мест)** — `{clanCfg.UpgradeMemberLimitPrice:N0} {guildCfg.CurrencySymbol}` (сейчас: {clan.MemberLimit})",
            $"• **Бонус к доходу (+5%)** — `{clanCfg.UpgradeEarnBonusPrice:N0} {guildCfg.CurrencySymbol}` (сейчас: +{clan.EarnBonusPercent}%)",
            $"• **Слот офицера (+1 слот)** — `{clanCfg.UpgradeOfficerSlotPrice:N0} {guildCfg.CurrencySymbol}` (сейчас: {clan.OfficerSlots})",
            $"• **Смена цвета роли клана** — `{clanCfg.UpgradeColorPrice:N0} {guildCfg.CurrencySymbol}` (сейчас: `{clan.ColorHex}`)");

        var select = new SelectMenuBuilder()
            .WithCustomId($"clan:upg_select:{clan.Id}")
            .WithPlaceholder("Выберите улучшение для покупки из казны...")
            .AddOption($"Лимит участников (+5) — {clanCfg.UpgradeMemberLimitPrice:N0}", "member_limit", "Увеличивает максимальное число участников на 5")
            .AddOption($"Бонус к заработку (+5%) — {clanCfg.UpgradeEarnBonusPrice:N0}", "earn_bonus", "Повышает доход участников клана за чат и войс")
            .AddOption($"Слот офицера (+1) — {clanCfg.UpgradeOfficerSlotPrice:N0}", "officer_slots", "Позволяет назначить ещё одного офицера")
            .AddOption($"Смена цвета клана — {clanCfg.UpgradeColorPrice:N0}", "color", "Изменяет HEX-цвет роли и карточки клана");

        var comp = new ComponentBuilder().WithSelectMenu(select).Build();
        await FollowupAsync(embed: embeds.Info($"Улучшения клана [{clan.Tag}] {clan.Name}", desc), components: comp, ephemeral: true);
    }

    [ComponentInteraction("clan:upg_select:*", true)]
    public async Task HandleUpgradeSelectAsync(string clanIdStr, string[] values)
    {
        if (!long.TryParse(clanIdStr, out var clanId))
            return;

        var choice = values.FirstOrDefault() ?? string.Empty;
        if (choice == "color")
        {
            await RespondWithModalAsync<ClanColorUpgradeModal>($"clan:upg_color:{clanId}");
            return;
        }

        await DeferAsync();
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Clan.Id != clanId || userClan.Value.Member.Rank is not ("leader" or "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Недостаточно прав для покупки улучшений."), ephemeral: true);
            return;
        }

        var clanCfg = settingsProvider.Current.Clans;
        var (price, delta, label) = choice switch
        {
            "member_limit" => (clanCfg.UpgradeMemberLimitPrice, 5, "Лимит участников увеличен на +5"),
            "earn_bonus" => (clanCfg.UpgradeEarnBonusPrice, 5, "Бонус к заработку увеличен на +5%"),
            "officer_slots" => (clanCfg.UpgradeOfficerSlotPrice, 1, "Добавлен +1 слот офицера"),
            _ => (0L, 0, string.Empty)
        };

        if (price <= 0)
            return;

        var (ok, err) = await clanRepo.PurchaseUpgradeFromTreasuryAsync(Context.Guild.Id, clanId, Context.User.Id, choice, price, delta);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Улучшение приобретено!", label);
            m.Components = null;
        });
    }

    [ModalInteraction("clan:upg_color:*", true)]
    public async Task HandleColorUpgradeModalAsync(string clanIdStr, ClanColorUpgradeModal modal)
    {
        if (!long.TryParse(clanIdStr, out var clanId))
            return;

        await DeferAsync(ephemeral: true);
        if (!EmbedFactory.TryParseColor(modal.Hex, out var discordColor))
        {
            await FollowupAsync(embed: embeds.Error("Некорректный формат HEX-цвета."), ephemeral: true);
            return;
        }

        var clanCfg = settingsProvider.Current.Clans;
        var hexFormatted = "#" + discordColor.RawValue.ToString("X6");
        var (ok, err) = await clanRepo.PurchaseUpgradeFromTreasuryAsync(
            Context.Guild.Id, clanId, Context.User.Id, "color", clanCfg.UpgradeColorPrice, 0, hexFormatted);

        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var clan = await clanRepo.GetByIdAsync(Context.Guild.Id, clanId);
        if (clan is not null && clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role)
        {
            await role.ModifyAsync(p => p.Color = discordColor);
        }

        await FollowupAsync(embed: embeds.Success("Цвет клана обновлён", $"Установлен новый цвет `{hexFormatted}`."), ephemeral: true);
    }

    [SlashCommand("disband", "Распустить клан с удалением роли и канала (только для лидера)")]
    public async Task DisbandClanCommandAsync()
    {
        await DeferAsync(ephemeral: true);
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Member.Rank != "leader")
        {
            await FollowupAsync(embed: embeds.Error("Распустить клан может только его основатель (лидер)."), ephemeral: true);
            return;
        }

        var clan = userClan.Value.Clan;
        var comp = new ComponentBuilder()
            .WithButton("Подтвердить роспуск", $"clan:disband_confirm:{clan.Id}", ButtonStyle.Danger)
            .WithButton("Отмена", "clan:disband_cancel", ButtonStyle.Secondary)
            .Build();

        await FollowupAsync(
            embed: embeds.Warning("Подтверждение роспуска клана",
                $"Вы уверены, что хотите безвозвратно распустить клан **[{clan.Tag}] {clan.Name}**? Казна, роль и канал будут удалены."),
            components: comp,
            ephemeral: true);
    }

    [ComponentInteraction("clan:disband_confirm:*", true)]
    public async Task HandleDisbandConfirmAsync(string clanIdStr)
    {
        if (!long.TryParse(clanIdStr, out var clanId))
            return;

        await DeferAsync();
        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Clan.Id != clanId || userClan.Value.Member.Rank != "leader")
            return;

        var clan = userClan.Value.Clan;
        await clanRepo.DisbandClanAsync(Context.Guild.Id, clan.Id);

        if (clan.ChannelId != 0 && Context.Guild.GetTextChannel(clan.ChannelId) is { } ch)
        {
            try { await ch.DeleteAsync(); } catch { }
        }

        if (clan.RoleId != 0 && Context.Guild.GetRole(clan.RoleId) is { } role)
        {
            try { await role.DeleteAsync(); } catch { }
        }

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Клан распущен", $"Клан **[{clan.Tag}] {clan.Name}** прекратил своё существование.");
            m.Components = null;
        });
    }

    [ComponentInteraction("clan:disband_cancel", true)]
    public async Task HandleDisbandCancelAsync()
    {
        await DeferAsync();
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Info("Отмена", "Роспуск клана отменён.");
            m.Components = null;
        });
    }

    [SlashCommand("top", "Рейтинг лучших кланов сервера по очкам или казне")]
    public async Task ClanTopCommandAsync(
        [Summary("sort", "Критерий сортировки"), Choice("По очкам славы и активности", "points"), Choice("По размеру казны", "treasury")] string sort = "points")
    {
        await DeferAsync();
        var clans = await clanRepo.GetTopClansAsync(Context.Guild.Id, sort, 10);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (clans.Count == 0)
        {
            await FollowupAsync(embed: embeds.Info("Рейтинг кланов", "На сервере пока не создано ни одного клана."));
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < clans.Count; i++)
        {
            var c = clans[i];
            sb.AppendLine($"**#{i + 1}** `[{c.Tag}]` **{c.Name}** — Очки: **{c.Points:N0}** • Казна: **{c.Treasury:N0} {cfg.CurrencySymbol}** (Лидер: <@{c.LeaderId}>)");
        }

        await FollowupAsync(embed: embeds.Info("Топ кланов сервера KRLZ", sb.ToString()));
    }

    [Group("war", "Дипломатия и клановые войны за казну и рейтинг")]
    public sealed class ClanWarSubgroup(
        ClanRepository clanRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("declare", "Объявить клановую войну другому клану")]
        public async Task DeclareWarCommandAsync(
            [Summary("target_clan", "Название или тег клана-соперника")] string targetQuery,
            [Summary("duration_days", "Длительность войны"), Choice("3 дня", 3), Choice("7 дней", 7)] int durationDays = 3)
        {
            await DeferAsync();
            var actorClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
            if (actorClan is null || actorClan.Value.Member.Rank is not ("leader" or "officer"))
            {
                await FollowupAsync(embed: embeds.Error("Объявлять войну могут только лидер и офицеры клана."), ephemeral: true);
                return;
            }

            var challenger = actorClan.Value.Clan;
            var defender = await clanRepo.GetByNameOrTagAsync(Context.Guild.Id, targetQuery);

            if (defender is null || defender.Id == challenger.Id)
            {
                await FollowupAsync(embed: embeds.Error("Укажите существующий клан соперника."), ephemeral: true);
                return;
            }

            var existing1 = await clanRepo.GetActiveOrPendingWarForClanAsync(Context.Guild.Id, challenger.Id);
            var existing2 = await clanRepo.GetActiveOrPendingWarForClanAsync(Context.Guild.Id, defender.Id);
            if (existing1 is not null || existing2 is not null)
            {
                await FollowupAsync(embed: embeds.Error("Один из кланов уже участвует в активной войне или ожидает ответа на вызов."), ephemeral: true);
                return;
            }

            var stealPercent = settingsProvider.Current.Clans.WarTreasuryStealPercent;
            var warId = await clanRepo.CreateWarDeclarationAsync(Context.Guild.Id, challenger.Id, defender.Id, durationDays, stealPercent);

            var desc = string.Join(Environment.NewLine,
                $"Клан **[{challenger.Tag}] {challenger.Name}** бросает вызов клану **[{defender.Tag}] {defender.Name}**!",
                $"• **Длительность:** {durationDays} дн.",
                $"• **Трофей победителя:** {stealPercent}% казны проигравшего клана + {settingsProvider.Current.Clans.PointsPerWarWin} очков славы.",
                string.Empty,
                $"Лидер <@{defender.LeaderId}> или офицеры клана **[{defender.Tag}]**, примите или отклоните вызов:");

            var comp = new ComponentBuilder()
                .WithButton("Принять вызов", $"clanwar:accept:{warId}", ButtonStyle.Danger)
                .WithButton("Отклонить", $"clanwar:decline:{warId}", ButtonStyle.Secondary)
                .Build();

            await FollowupAsync(text: $"<@{defender.LeaderId}>", embed: embeds.Warning("Объявление клановой войны!", desc), components: comp);
        }
    }

    [ComponentInteraction("clanwar:accept:*", true)]
    public async Task HandleWarAcceptAsync(string warIdStr)
    {
        if (!long.TryParse(warIdStr, out var warId))
            return;

        await DeferAsync();
        var war = await clanRepo.GetWarByIdAsync(warId);
        if (war is null || war.Status != "pending")
        {
            await FollowupAsync(embed: embeds.Error("Вызов на войну недействителен."), ephemeral: true);
            return;
        }

        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Clan.Id != war.DefenderClanId || userClan.Value.Member.Rank is not ("leader" or "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Принять вызов может только лидер или офицер обороняющегося клана."), ephemeral: true);
            return;
        }

        var activated = await clanRepo.ActivateWarAsync(war.Id, war.DurationDays);
        if (!activated)
            return;

        var endsAt = DateTimeOffset.UtcNow.AddDays(war.DurationDays);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Success("Клановая война началась!",
                $"Война `#{war.Id}` официально объявлена открытой!\n" +
                $"Завершение: {TimestampTag.FromDateTimeOffset(endsAt, TimestampTagStyles.Relative)}.\n" +
                "Очки начисляются за активность участников обоих кланов в текстовых и голосовых каналах.");
            m.Components = null;
        });
    }

    [ComponentInteraction("clanwar:decline:*", true)]
    public async Task HandleWarDeclineAsync(string warIdStr)
    {
        if (!long.TryParse(warIdStr, out var warId))
            return;

        await DeferAsync();
        var war = await clanRepo.GetWarByIdAsync(warId);
        if (war is null || war.Status != "pending")
            return;

        var userClan = await clanRepo.GetUserClanAsync(Context.Guild.Id, Context.User.Id);
        if (userClan is null || userClan.Value.Clan.Id != war.DefenderClanId || userClan.Value.Member.Rank is not ("leader" or "officer"))
        {
            await FollowupAsync(embed: embeds.Error("Отклонить вызов может только лидер или офицер обороняющегося клана."), ephemeral: true);
            return;
        }

        await clanRepo.CancelPendingWarAsync(war.Id);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embeds.Info("Вызов отклонён", "Обороняющийся клан отказался от проведения клановой войны.");
            m.Components = null;
        });
    }
}
