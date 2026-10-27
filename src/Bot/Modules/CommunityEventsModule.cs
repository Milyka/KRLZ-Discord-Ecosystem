using System.Text;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class CommunityEventsModule(
    CommunityEventsRepository eventsRepo,
    CommunityEventsService eventsService,
    DatabaseBackupService dbBackupService,
    UserRepository userRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [Group("reactionroles", "Создание постоянных панелей автовыдачи ролей по кнопкам или меню")]
    [RequireAdmin]
    public sealed class ReactionRolesSubgroup(
        CommunityEventsRepository eventsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("create", "Опубликовать интерактивную панель выбора ролей")]
        public async Task CreateReactionRolePanelAsync(
            [Summary("title", "Заголовок панели ролей")] string title,
            [Summary("mode", "Режим выдачи"),
             Choice("Переключатель (Toggle)", "toggle"),
             Choice("Только одна роль из списка (Single)", "single"),
             Choice("Несколько ролей (Multiple)", "multi")] string mode,
            [Summary("component", "Тип интерфейса"),
             Choice("Кнопки", "buttons"),
             Choice("Выпадающее меню (Select)", "select")] string componentType,
            [Summary("role1", "Первая роль")] SocketRole role1,
            [Summary("label1", "Подпись первой роли")] string label1,
            [Summary("role2", "Вторая роль (необязательно)")] SocketRole? role2 = null,
            [Summary("label2", "Подпись второй роли")] string? label2 = null,
            [Summary("role3", "Третья роль (необязательно)")] SocketRole? role3 = null,
            [Summary("label3", "Подпись третьей роли")] string? label3 = null,
            [Summary("role4", "Четвёртая роль (необязательно)")] SocketRole? role4 = null,
            [Summary("label4", "Подпись четвёртой роли")] string? label4 = null)
        {
            var mappings = new List<ReactionRoleMappingDto>
            {
                new(role1.Id, label1.Trim(), $"Роль @{role1.Name}")
            };

            if (role2 is not null) mappings.Add(new(role2.Id, string.IsNullOrWhiteSpace(label2) ? role2.Name : label2.Trim(), $"Роль @{role2.Name}"));
            if (role3 is not null) mappings.Add(new(role3.Id, string.IsNullOrWhiteSpace(label3) ? role3.Name : label3.Trim(), $"Роль @{role3.Name}"));
            if (role4 is not null) mappings.Add(new(role4.Id, string.IsNullOrWhiteSpace(label4) ? role4.Name : label4.Trim(), $"Роль @{role4.Name}"));

            foreach (var m in mappings)
            {
                var r = Context.Guild.GetRole(m.RoleId);
                if (r is not null && r.Position >= Context.Guild.CurrentUser.Hierarchy)
                {
                    await RespondAsync(embed: embeds.Error($"Роль <@&{r.Id}> находится выше или на одном уровне с ролью бота."), ephemeral: true);
                    return;
                }
            }

            var panelId = await eventsRepo.CreateReactionRolePanelAsync(
                Context.Guild.Id,
                Context.Channel.Id,
                0,
                title.Trim(),
                mode,
                componentType,
                JsonSerializer.Serialize(mappings));

            var lines = mappings.Select(m => $"• **{m.Label}** — <@&{m.RoleId}>");
            var embed = embeds.Info(title.Trim(), string.Join(Environment.NewLine, lines));

            var compBuilder = new ComponentBuilder();
            if (componentType == "select")
            {
                var maxVals = mode == "single" ? 1 : mappings.Count;
                var select = new SelectMenuBuilder()
                    .WithCustomId($"rr:select:{panelId}")
                    .WithPlaceholder("Выберите роли...")
                    .WithMinValues(0)
                    .WithMaxValues(maxVals);

                foreach (var m in mappings)
                    select.AddOption(m.Label, m.RoleId.ToString(), m.Description);

                compBuilder.WithSelectMenu(select);
            }
            else
            {
                foreach (var m in mappings)
                    compBuilder.WithButton(m.Label, $"rr:btn:{panelId}:{m.RoleId}", ButtonStyle.Primary);
            }

            await RespondAsync(embed: embed, components: compBuilder.Build());
            var msg = await GetOriginalResponseAsync();
            await eventsRepo.UpdateReactionRoleMessageIdAsync(panelId, msg.Id);
        }
    }

    [ComponentInteraction("rr:btn:*:*", true)]
    public async Task HandleReactionRoleButtonAsync(string panelIdStr, string roleIdStr)
    {
        if (!long.TryParse(panelIdStr, out var panelId) || !ulong.TryParse(roleIdStr, out var roleId))
            return;

        var panel = await eventsRepo.GetReactionRoleByIdAsync(panelId);
        if (panel is null || Context.Guild.GetRole(roleId) is not { } targetRole)
        {
            await RespondAsync(embed: embeds.Error("Панель или роль не найдены."), ephemeral: true);
            return;
        }

        if (targetRole.Position >= Context.Guild.CurrentUser.Hierarchy)
        {
            await RespondAsync(embed: embeds.Error("Роль находится выше роли бота в иерархии."), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        var mappings = JsonSerializer.Deserialize<List<ReactionRoleMappingDto>>(panel.MappingsJson) ?? [];

        if (panel.Mode == "single")
        {
            var otherRoleIds = mappings.Select(x => x.RoleId).Where(id => id != roleId).ToHashSet();
            var toRemove = member.Roles.Where(r => otherRoleIds.Contains(r.Id) && r.Position < Context.Guild.CurrentUser.Hierarchy).ToList();
            if (toRemove.Count > 0)
                await member.RemoveRolesAsync(toRemove);
        }

        if (member.Roles.Any(r => r.Id == roleId))
        {
            await member.RemoveRoleAsync(targetRole);
            await RespondAsync(embed: embeds.Info("Роль снята", $"С вас снята роль <@&{roleId}>."), ephemeral: true);
        }
        else
        {
            await member.AddRoleAsync(targetRole);
            await RespondAsync(embed: embeds.Success("Роль выдана", $"Вам выдана роль <@&{roleId}>!"), ephemeral: true);
        }
    }

    [ComponentInteraction("role:toggle:*", true)]
    public async Task HandleRoleToggleAsync(string roleIdStr)
    {
        if (!ulong.TryParse(roleIdStr, out var roleId))
            return;

        var restrictedStaffRoles = new HashSet<ulong> {
            1553561065835470899, // ♂️ Мужчина
            1553561142004285461, // ♀️ Женщина
            1548720962386665683  // 18+
        };

        if (restrictedStaffRoles.Contains(roleId))
        {
            await RespondAsync(embed: embeds.Error("Эту роль может выдать только саппорт сервера."), ephemeral: true);
            return;
        }

        if (Context.Guild.GetRole(roleId) is not { } targetRole)
        {
            await RespondAsync(embed: embeds.Error("Роль не найдена."), ephemeral: true);
            return;
        }

        if (targetRole.Position >= Context.Guild.CurrentUser.Hierarchy)
        {
            await RespondAsync(embed: embeds.Error("Роль находится выше роли бота в иерархии."), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        var gameRoleIds = new HashSet<ulong> {
            1566147869214375946, 1566147885681086635, 1566147946905337997,
            1566147984004227134, 1566148011984158801, 1566148038563602467
        };
        const ulong parentGameRoleId = 1566153059459932190;

        // Gender mutually exclusive logic
        if (roleId == 1553561065835470899 && member.Roles.Any(r => r.Id == 1553561142004285461))
        {
            if (Context.Guild.GetRole(1553561142004285461) is { } fRole && fRole.Position < Context.Guild.CurrentUser.Hierarchy)
                await member.RemoveRoleAsync(fRole);
        }
        else if (roleId == 1553561142004285461 && member.Roles.Any(r => r.Id == 1553561065835470899))
        {
            if (Context.Guild.GetRole(1553561065835470899) is { } mRole && mRole.Position < Context.Guild.CurrentUser.Hierarchy)
                await member.RemoveRoleAsync(mRole);
        }

        if (member.Roles.Any(r => r.Id == roleId))
        {
            await member.RemoveRoleAsync(targetRole);

            // If removed game role and has no remaining games, remove @Game
            if (gameRoleIds.Contains(roleId))
            {
                var remainingGames = member.Roles.Count(r => gameRoleIds.Contains(r.Id) && r.Id != roleId);
                if (remainingGames == 0 && member.Roles.Any(r => r.Id == parentGameRoleId))
                {
                    if (Context.Guild.GetRole(parentGameRoleId) is { } pRole && pRole.Position < Context.Guild.CurrentUser.Hierarchy)
                        await member.RemoveRoleAsync(pRole);
                }
            }

            await RespondAsync(embed: embeds.Info("Роль снята", $"С вас снята роль <@&{roleId}>."), ephemeral: true);
        }
        else
        {
            await member.AddRoleAsync(targetRole);

            // If added game role, ensure @Game is present
            if (gameRoleIds.Contains(roleId) && !member.Roles.Any(r => r.Id == parentGameRoleId))
            {
                if (Context.Guild.GetRole(parentGameRoleId) is { } pRole && pRole.Position < Context.Guild.CurrentUser.Hierarchy)
                    await member.AddRoleAsync(pRole);
            }

            await RespondAsync(embed: embeds.Success("Роль выдана", $"Вам выдана роль <@&{roleId}>!"), ephemeral: true);
        }
    }

    [ComponentInteraction("role:select:*", true)]
    public async Task HandleDirectRoleSelectAsync(string category, string[] selectedRoleIds)
    {
        var chosenIds = selectedRoleIds.Select(s => ulong.TryParse(s, out var id) ? id : 0UL).Where(id => id != 0).ToHashSet();
        var restrictedStaffRoles = new HashSet<ulong> {
            1553561065835470899, // ♂️ Мужчина
            1553561142004285461, // ♀️ Женщина
            1548720962386665683  // 18+
        };
        chosenIds.RemoveWhere(id => restrictedStaffRoles.Contains(id));
        var member = (SocketGuildUser)Context.User;

        if (category == "gender")
        {
            var genderRoleIds = new ulong[] { 1553561065835470899, 1553561142004285461 };
            foreach (var gId in genderRoleIds)
            {
                if (!chosenIds.Contains(gId) && member.Roles.Any(r => r.Id == gId))
                {
                    if (Context.Guild.GetRole(gId) is { } gRole && gRole.Position < Context.Guild.CurrentUser.Hierarchy)
                        await member.RemoveRoleAsync(gRole);
                }
            }
        }

        foreach (var rId in chosenIds)
        {
            if (Context.Guild.GetRole(rId) is { } role && role.Position < Context.Guild.CurrentUser.Hierarchy)
            {
                if (!member.Roles.Any(r => r.Id == rId))
                    await member.AddRoleAsync(role);
            }
        }

        await RespondAsync(embed: embeds.Success("Роли обновлены", "Ваши роли успешно обновлены!"), ephemeral: true);
    }

    [ComponentInteraction("rr:select:*", true)]
    public async Task HandleReactionRoleSelectAsync(string panelIdStr, string[] selectedRoleIds)
    {
        if (!long.TryParse(panelIdStr, out var panelId))
            return;

        var panel = await eventsRepo.GetReactionRoleByIdAsync(panelId);
        if (panel is null)
            return;

        var mappings = JsonSerializer.Deserialize<List<ReactionRoleMappingDto>>(panel.MappingsJson) ?? [];
        var panelRoleIds = mappings.Select(m => m.RoleId).ToHashSet();
        var chosenIds = selectedRoleIds.Select(s => ulong.TryParse(s, out var id) ? id : 0UL).Where(id => id != 0).ToHashSet();

        var member = (SocketGuildUser)Context.User;
        foreach (var rId in panelRoleIds)
        {
            if (Context.Guild.GetRole(rId) is not { } role || role.Position >= Context.Guild.CurrentUser.Hierarchy)
                continue;

            var hasRole = member.Roles.Any(r => r.Id == rId);
            var shouldHave = chosenIds.Contains(rId);

            if (shouldHave && !hasRole)
                await member.AddRoleAsync(role);
            else if (!shouldHave && hasRole)
                await member.RemoveRoleAsync(role);
        }

        await RespondAsync(embed: embeds.Success("Роли обновлены", "Ваш набор ролей синхронизирован с выбором в меню."), ephemeral: true);
    }

    [SlashCommand("poll", "Создать интерактивный опрос сообщества")]
    [RequireModerator]
    public async Task PollCommandAsync(
        [Summary("question", "Вопрос голосования")] string question,
        [Summary("options", "Варианты через точку с запятой (2-5 вариантов)")] string optionsRaw,
        [Summary("duration", "Длительность (например: 10m, 2h, 1d)")] string duration = "1h",
        [Summary("anonymous", "Анонимное голосование")] bool anonymous = false)
        => await CreatePollInternalAsync(question, optionsRaw, duration, anonymous);

    [SlashCommand("poll-v2", "Создать интерактивный опрос сообщества")]
    [RequireModerator]
    public async Task PollV2CommandAsync(
        [Summary("question", "Вопрос голосования")] string question,
        [Summary("options", "Варианты через точку с запятой (2-5 вариантов)")] string optionsRaw,
        [Summary("duration", "Длительность (например: 10m, 2h, 1d)")] string duration = "1h",
        [Summary("anonymous", "Анонимное голосование")] bool anonymous = false)
        => await CreatePollInternalAsync(question, optionsRaw, duration, anonymous);

    private async Task CreatePollInternalAsync(
        string question,
        string optionsRaw,
        string duration,
        bool anonymous)
    {
        if (!TimeParser.TryParseDuration(duration, out var span))
        {
            await RespondAsync(embed: embeds.Error("Неверный формат времени. Используйте `10m`, `2h`, `1d`."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var options = optionsRaw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .ToList();

        if (options.Count < 2)
        {
            await FollowupAsync(embed: embeds.Error("Укажите минимум 2 варианта ответа через `;`."), ephemeral: true);
            return;
        }

        var endsAt = DateTimeOffset.UtcNow.Add(span);
        var pollId = await eventsRepo.CreatePollV2Async(
            Context.Guild.Id,
            Context.Channel.Id,
            Context.User.Id,
            question.Trim(),
            JsonSerializer.Serialize(options),
            anonymous,
            endsAt);

        var pollEntity = await eventsRepo.GetPollByIdAsync(pollId);
        var chartPng = await eventsService.RenderPollChartPngAsync(pollEntity!, options, []);

        string[] numberEmojis = ["1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣"];
        var comp = new ComponentBuilder();
        for (var i = 0; i < options.Count; i++)
        {
            var btnLabel = options[i].Length > 32 ? options[i][..31] + "…" : options[i];
            var emojiStr = i < numberEmojis.Length ? numberEmojis[i] : $"{i + 1}";
            comp.WithButton(
                label: btnLabel,
                customId: $"pollv2:vote:{pollId}:{i}",
                style: ButtonStyle.Secondary,
                emote: new Emoji(emojiStr),
                row: 0);
        }

        if (!anonymous)
        {
            comp.WithButton("Кто голосовал", $"pollv2:voters:{pollId}", ButtonStyle.Primary, new Emoji("🔍"), row: 1);
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(56, 189, 248))
            .WithImageUrl("attachment://poll-chart.png")
            .Build();

        await using var stream = new MemoryStream(chartPng);
        var sent = await FollowupWithFileAsync(stream, "poll-chart.png", embed: embed, components: comp.Build());
        await eventsRepo.UpdatePollMessageIdAsync(pollId, sent.Id);
    }

    [ComponentInteraction("pollv2:vote:*:*", true)]
    public async Task HandlePollV2VoteButtonAsync(string pollIdStr, string optIndexStr)
    {
        if (!long.TryParse(pollIdStr, out var pollId) || !int.TryParse(optIndexStr, out var optionIndex))
            return;

        var poll = await eventsRepo.GetPollByIdAsync(pollId);
        if (poll is null || poll.Status != "active" || poll.EndsAt <= DateTimeOffset.UtcNow)
        {
            await RespondAsync(embed: embeds.Error("Этот опрос уже завершён."), ephemeral: true);
            return;
        }

        await DeferAsync();
        await eventsRepo.CastPollVoteAsync(pollId, Context.Guild.Id, Context.User.Id, optionIndex);
        var options = JsonSerializer.Deserialize<List<string>>(poll.OptionsJson) ?? [];
        var votes = await eventsRepo.GetPollVotesAsync(pollId);
        var chartPng = await eventsService.RenderPollChartPngAsync(poll, options, votes);

        var embed = new EmbedBuilder()
            .WithColor(new Color(56, 189, 248))
            .WithImageUrl("attachment://poll-chart.png")
            .Build();

        await using var stream = new MemoryStream(chartPng);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Attachments = new List<FileAttachment> { new(stream, "poll-chart.png") };
        });

        var chosenText = optionIndex >= 0 && optionIndex < options.Count ? options[optionIndex] : $"{optionIndex + 1}";
        await FollowupAsync($"Ваш выбор: **{chosenText}**", ephemeral: true);
    }

    [ComponentInteraction("pollv2:voters:*", true)]
    public async Task HandlePollVotersButtonAsync(string pollIdStr)
    {
        if (!long.TryParse(pollIdStr, out var pollId))
            return;

        var poll = await eventsRepo.GetPollByIdAsync(pollId);
        if (poll is null)
        {
            await RespondAsync(embed: embeds.Error("Опрос не найден."), ephemeral: true);
            return;
        }

        if (poll.IsAnonymous)
        {
            await RespondAsync(embed: embeds.Warning("Анонимный опрос", "В этом опросе включено тайное голосование."), ephemeral: true);
            return;
        }

        var options = JsonSerializer.Deserialize<List<string>>(poll.OptionsJson) ?? [];
        var votes = await eventsRepo.GetPollVotesAsync(pollId);
        if (votes.Count == 0)
        {
            await RespondAsync(embed: embeds.Info("Список голосов", "В этом опросе пока никто не проголосовал."), ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < options.Count; i++)
        {
            var optVotes = votes.Where(v => v.OptionIndex == i).ToList();
            sb.AppendLine($"**{i + 1}. {options[i]}** ({optVotes.Count}):");
            sb.AppendLine(optVotes.Count == 0
                ? "— *нет голосов*"
                : string.Join(", ", optVotes.Take(25).Select(v => $"<@{v.UserId}>")));
            sb.AppendLine();
        }

        await RespondAsync(embed: embeds.Info($"Участники голосования — {poll.Question}", sb.ToString()), ephemeral: true);
    }

    [Group("giveaway", "Проведение розыгрышей призов с проверкой условий участия")]
    [RequireModerator]
    public sealed class GiveawaySubgroup(
        CommunityEventsRepository eventsRepo,
        CommunityEventsService eventsService,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("create", "Запустить новый розыгрыш с таймером и условиями")]
        public async Task CreateGiveawayCommandAsync(
            [Summary("prize", "Разыгрываемый приз")] string prize,
            [Summary("duration", "Длительность (10m, 2h, 1d)")] string duration,
            [Summary("winners", "Количество победителей")] [MinValue(1), MaxValue(20)] int winners = 1,
            [Summary("required_role", "Обязательная роль для участия")] SocketRole? requiredRole = null,
            [Summary("min_level", "Минимальный уровень участника")] [MinValue(1)] int minLevel = 1,
            [Summary("min_balance", "Минимальный баланс валюты")] [MinValue(0)] long minBalance = 0)
        {
            if (!TimeParser.TryParseDuration(duration, out var span))
            {
                await RespondAsync(embed: embeds.Error("Неверный формат длительности."), ephemeral: true);
                return;
            }

            var endsAt = DateTimeOffset.UtcNow.Add(span);
            var gwId = await eventsRepo.CreateGiveawayAsync(
                Context.Guild.Id,
                Context.Channel.Id,
                Context.User.Id,
                prize.Trim(),
                winners,
                requiredRole?.Id,
                minLevel,
                minBalance,
                endsAt);

            var reqLines = new List<string> { $"• Минимальный уровень: **{minLevel}**" };
            if (requiredRole is not null) reqLines.Add($"• Необходимая роль: <@&{requiredRole.Id}>");
            if (minBalance > 0) reqLines.Add($"• Минимальный баланс: **{minBalance:N0}**");

            var desc = string.Join(Environment.NewLine,
                $"**Приз:** {prize.Trim()}",
                $"**Количество победителей:** {winners}",
                $"**Организатор:** <@{Context.User.Id}>",
                $"**Завершение:** {TimestampTag.FromDateTimeOffset(endsAt, TimestampTagStyles.Relative)}",
                string.Empty,
                "**Условия участия:**",
                string.Join(Environment.NewLine, reqLines));

            var comp = new ComponentBuilder()
                .WithButton("Участвовать в розыгрыше (0)", $"gw:join:{gwId}", ButtonStyle.Success)
                .Build();

            await RespondAsync(embed: embeds.Info($"Розыгрыш #{gwId} — {prize.Trim()}", desc), components: comp);
            var msg = await GetOriginalResponseAsync();
            await eventsRepo.UpdateGiveawayMessageIdAsync(gwId, msg.Id);
        }

        [SlashCommand("end", "Досрочно завершить активный розыгрыш и выбрать победителей")]
        public async Task EndGiveawayCommandAsync([Summary("giveaway_id", "Номер розыгрыша")] long giveawayId)
        {
            var gw = await eventsRepo.GetGiveawayByIdAsync(giveawayId);
            if (gw is null || gw.Status != "active")
            {
                await RespondAsync(embed: embeds.Error("Активный розыгрыш с таким номером не найден."), ephemeral: true);
                return;
            }

            var selected = await eventsService.FinalizeGiveawayAsync(Context.Client, gw, isReroll: false);
            await RespondAsync(embed: embeds.Success("Розыгрыш завершён",
                $"Выбрано победителей: **{selected.Count}**."), ephemeral: true);
        }

        [SlashCommand("reroll", "Перевыбрать нового победителя для завершённого розыгрыша")]
        public async Task RerollGiveawayCommandAsync([Summary("giveaway_id", "Номер розыгрыша")] long giveawayId)
        {
            var gw = await eventsRepo.GetGiveawayByIdAsync(giveawayId);
            if (gw is null || gw.Status != "finished")
            {
                await RespondAsync(embed: embeds.Error("Завершённый розыгрыш с таким номером не найден."), ephemeral: true);
                return;
            }

            var selected = await eventsService.FinalizeGiveawayAsync(Context.Client, gw, isReroll: true);
            if (selected.Count == 0)
            {
                await RespondAsync(embed: embeds.Error("Больше нет доступных участников для перевыбора."), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Success("Перевыбор выполнен", $"Новый победитель: <@{selected[0]}>!"), ephemeral: true);
        }
    }

    [ComponentInteraction("gw:join:*", true)]
    public async Task HandleGiveawayJoinButtonAsync(string gwIdStr)
    {
        if (!long.TryParse(gwIdStr, out var gwId))
            return;

        var gw = await eventsRepo.GetGiveawayByIdAsync(gwId);
        if (gw is null || gw.Status != "active" || gw.EndsAt <= DateTimeOffset.UtcNow)
        {
            await RespondAsync(embed: embeds.Error("Приём заявок на этот розыгрыш уже закрыт."), ephemeral: true);
            return;
        }

        var member = (SocketGuildUser)Context.User;
        if (gw.RequiredRoleId.HasValue && !member.Roles.Any(r => r.Id == gw.RequiredRoleId.Value))
        {
            await RespondAsync(embed: embeds.Error($"Для участия требуется роль <@&{gw.RequiredRoleId.Value}>."), ephemeral: true);
            return;
        }

        var profile = await userRepo.GetOrCreateAsync(Context.Guild.Id, member.Id);
        if (profile.Level < gw.MinLevel)
        {
            await RespondAsync(embed: embeds.Error($"Для участия необходим минимум **{gw.MinLevel} уровень** (у вас {profile.Level})."), ephemeral: true);
            return;
        }

        if (profile.Balance < gw.MinBalance)
        {
            await RespondAsync(embed: embeds.Error($"Для участия необходим баланс не менее **{gw.MinBalance:N0}**."), ephemeral: true);
            return;
        }

        var (added, total) = await eventsRepo.TryEnterGiveawayAsync(gw.Id, Context.Guild.Id, member.Id);
        if (!added)
        {
            await RespondAsync(embed: embeds.Info("Вы уже участвуете", "Ваша заявка уже зарегистрирована в этом розыгрыше."), ephemeral: true);
            return;
        }

        var updatedComp = new ComponentBuilder()
            .WithButton($"Участвовать в розыгрыше ({total})", $"gw:join:{gw.Id}", ButtonStyle.Success)
            .Build();

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m => m.Components = updatedComp);
    }

    [Group("dbbackup", "Резервное копирование базы данных SQLite (только владелец сервера)")]
    [RequireGuildOwnerCustom]
    public sealed class DbBackupSubgroup(
        DatabaseBackupService dbBackupService,
        CommunityEventsRepository eventsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("now", "Создать горячую резервную копию SQLite через VACUUM INTO прямо сейчас")]
        public async Task BackupNowCommandAsync()
        {
            await DeferAsync(ephemeral: true);
            var (ok, msg, _, _) = await dbBackupService.CreateDatabaseBackupNowAsync(
                Context.Client, Context.Guild.Id, Context.User.Id);

            if (!ok)
            {
                await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
                return;
            }

            await FollowupAsync(embed: embeds.Success("Копия базы данных создана", msg), ephemeral: true);
        }

        [SlashCommand("list", "Показать журнал созданных резервных копий базы данных")]
        public async Task ListBackupsCommandAsync()
        {
            var logs = await eventsRepo.GetDatabaseBackupsAsync(Context.Guild.Id, 15);
            if (logs.Count == 0)
            {
                await RespondAsync(embed: embeds.Info("Журнал бэкапов БД", "Резервные копии ещё не создавались."), ephemeral: true);
                return;
            }

            var sb = new StringBuilder();
            foreach (var b in logs)
            {
                sb.AppendLine($"• **#{b.Id}** `{b.FileName}` ({b.SizeBytes / 1024.0:F1} КБ) — {TimestampTag.FromDateTimeOffset(b.CreatedAt, TimestampTagStyles.ShortDateTime)}");
            }

            await RespondAsync(embed: embeds.Info("Журнал копий SQLite", sb.ToString()), ephemeral: true);
        }
    }
}
