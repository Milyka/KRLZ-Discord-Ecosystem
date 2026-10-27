using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class UtilityModule(
    UserRepository userRepo,
    SocialRepository socialRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("avatar", "Получить полноразмерный аватар участника")]
    public async Task AvatarCommandAsync([Summary("user", "Участник")] SocketGuildUser? user = null)
    {
        var target = user ?? (SocketGuildUser)Context.User;
        var url = target.GetGuildAvatarUrl(size: 1024) ?? target.GetAvatarUrl(size: 1024) ?? target.GetDefaultAvatarUrl();

        var embed = embeds.CreateBase($"Аватар — {target.DisplayName}")
            .WithImageUrl(url)
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Открыть оригинал", style: ButtonStyle.Link, url: url)
            .Build();

        await RespondAsync(embed: embed, components: comp);
    }

    [SlashCommand("userinfo", "Подробная карточка информации об аккаунте участника")]
    public async Task UserInfoCommandAsync([Summary("user", "Участник")] SocketGuildUser? user = null)
    {
        var member = user ?? (SocketGuildUser)Context.User;
        var roles = member.Roles
            .Where(r => !r.IsEveryone)
            .OrderByDescending(r => r.Position)
            .Select(r => $"<@&{r.Id}>")
            .ToList();

        var embed = embeds.CreateBase($"Информация об участнике — {member.DisplayName}")
            .WithThumbnailUrl(member.GetAvatarUrl() ?? member.GetDefaultAvatarUrl())
            .AddField("Пользователь", $"{member.Mention} (`{member.Username}`)", true)
            .AddField("Discord ID", $"`{member.Id}`", true)
            .AddField("Статус", member.Status.ToString(), true)
            .AddField("Аккаунт создан", TimestampTag.FromDateTimeOffset(member.CreatedAt, TimestampTagStyles.LongDate), true)
            .AddField("Вступил на сервер", member.JoinedAt.HasValue ? TimestampTag.FromDateTimeOffset(member.JoinedAt.Value, TimestampTagStyles.LongDate) : "Неизвестно", true)
            .AddField($"Роли ({roles.Count})", roles.Count > 0 ? string.Join(", ", roles.Take(15)) : "Нет ролей", false)
            .Build();

        await RespondAsync(embed: embed);
    }

    [SlashCommand("roleinfo", "Информация о выбранной роли сервера")]
    public async Task RoleInfoCommandAsync([Summary("role", "Роль сервера")] SocketRole role)
    {
        var embed = embeds.CreateBase($"Информация о роли — {role.Name}", colorOverride: role.Color.RawValue != 0 ? role.Color : null)
            .AddField("Роль", $"{role.Mention} (`{role.Id}`)", true)
            .AddField("Цвет HEX", $"`#{role.Color.RawValue:X6}`", true)
            .AddField("Позиция", role.Position.ToString(), true)
            .AddField("Участников с ролью", role.Members.Count().ToString(), true)
            .AddField("Отображается отдельно", role.IsHoisted ? "Да" : "Нет", true)
            .AddField("Упоминаемая", role.IsMentionable ? "Да" : "Нет", true)
            .AddField("Дата создания", TimestampTag.FromDateTimeOffset(role.CreatedAt, TimestampTagStyles.ShortDate), true)
            .Build();

        await RespondAsync(embed: embed);
    }

    [SlashCommand("remind", "Установить персональное напоминание по таймеру")]
    public async Task RemindCommandAsync(
        [Summary("time", "Через сколько напомнить (10m, 2h, 3d)")] string timeRaw,
        [Summary("message", "Текст напоминания")] string message)
    {
        if (!TimeParser.TryParseDuration(timeRaw, out var duration))
        {
            await RespondAsync(embed: embeds.Error("Неверный формат времени. Используйте `10m`, `2h`, `3d`."), ephemeral: true);
            return;
        }

        var triggerAt = DateTimeOffset.UtcNow.Add(duration);
        var id = await socialRepo.AddReminderAsync(Context.Guild.Id, Context.User.Id, Context.Channel.Id, message.Trim(), triggerAt);

        await RespondAsync(embed: embeds.Success("Напоминание создано",
            $"Напоминание `#{id}` сработает {TimestampTag.FromDateTimeOffset(triggerAt, TimestampTagStyles.Relative)}.\n**Текст:** {message}"), ephemeral: true);
    }

    [SlashCommand("marry", "Сделать предложение руки и сердца участнику сервера или развестись")]
    public async Task MarryCommandAsync(
        [Summary("partner", "Участник для предложения брака")] SocketGuildUser? partner = null,
        [Summary("divorce", "Установите true для расторжения текущего брака")] bool divorce = false)
    {
        if (divorce)
        {
            var divorced = await socialRepo.DivorceAsync(Context.Guild.Id, Context.User.Id);
            if (!divorced)
            {
                await RespondAsync(embed: embeds.Error("Вы сейчас не состоите в браке."), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Warning("Брак расторгнут", $"Участник <@{Context.User.Id}> официально расторг брачный союз."));
            return;
        }

        if (partner is null || partner.IsBot || partner.Id == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Укажите другого реального участника сервера для предложения брака."), ephemeral: true);
            return;
        }

        var existing1 = await socialRepo.GetMarriageAsync(Context.Guild.Id, Context.User.Id);
        var existing2 = await socialRepo.GetMarriageAsync(Context.Guild.Id, partner.Id);
        if (existing1 is not null || existing2 is not null)
        {
            await RespondAsync(embed: embeds.Error("Один из участников уже состоит в зарегистрированном браке."), ephemeral: true);
            return;
        }

        var embed = embeds.CreateBase("Предложение руки и сердца")
            .WithDescription($"<@{Context.User.Id}> делает предложение участнику <@{partner.Id}>!\nСогласны ли вы вступить в брак на сервере KRLZ?")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Согласиться", $"marry:accept:{Context.User.Id}:{partner.Id}", ButtonStyle.Success)
            .WithButton("Отказаться", $"marry:decline:{Context.User.Id}:{partner.Id}", ButtonStyle.Danger)
            .Build();

        await RespondAsync(text: $"<@{partner.Id}>", embed: embed, components: comp);
    }

    [ComponentInteraction("marry:*:*:*", true)]
    public async Task HandleMarriageDecisionAsync(string decision, string proposerStr, string targetStr)
    {
        if (!ulong.TryParse(proposerStr, out var proposerId) || !ulong.TryParse(targetStr, out var targetId))
            return;

        if (Context.User.Id != targetId)
        {
            await RespondAsync(embed: embeds.Error("Ответить на предложение может только тот, кому оно адресовано."), ephemeral: true);
            return;
        }

        if (decision == "decline")
        {
            await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
            {
                m.Embed = embeds.Warning("Предложение отклонено", $"<@{targetId}> отклонил(а) предложение от <@{proposerId}>.");
                m.Components = null;
            });
            return;
        }

        var created = await socialRepo.CreateMarriageAsync(Context.Guild.Id, proposerId, targetId);
        if (!created)
        {
            await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
            {
                m.Embed = embeds.Error("Не удалось заключить брак: один из участников уже вступил в союз.");
                m.Components = null;
            });
            return;
        }

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embeds.Success("Поздравляем молодожёнов!",
                $"Союз между <@{proposerId}> и <@{targetId}> официально зарегистрирован на сервере KRLZ!");
            m.Components = null;
        });
    }

    [SlashCommand("bio", "Изменить личный статус (описание) в карточке профиля")]
    public async Task BioCommandAsync([Summary("text", "Новое описание профиля (до 120 символов)")] string text)
    {
        var clean = text.Replace("\n", " ").Replace("\n", " ").Trim();
        if (clean.Length > 120)
            clean = clean[..120];

        await userRepo.UpdateBioAsync(Context.Guild.Id, Context.User.Id, clean);
        await RespondAsync(embed: embeds.Success("Описание обновлено", $"Ваше новое Bio: `{clean}`"), ephemeral: true);
    }

    [SlashCommand("birthday", "Указать день рождения в профиле (формат ДД.ММ)")]
    public async Task BirthdayCommandAsync([Summary("date", "Дата в формате ДД.ММ (например 15.08)")] string date)
    {
        var trimmed = date.Trim();
        if (!DateTime.TryParseExact(trimmed + ".2000", "dd.MM.yyyy", null, System.Globalization.DateTimeStyles.None, out _))
        {
            await RespondAsync(embed: embeds.Error("Укажите корректную дату в формате `ДД.ММ` (например, `15.08`)."), ephemeral: true);
            return;
        }

        await userRepo.UpdateBirthdayAsync(Context.Guild.Id, Context.User.Id, trimmed);
        await RespondAsync(embed: embeds.Success("День рождения сохранён", $"В вашем профиле установлена дата рождения: **{trimmed}**."), ephemeral: true);
    }
}
