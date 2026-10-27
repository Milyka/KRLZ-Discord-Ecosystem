using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class ProgressionModule(
    ProgressionRepository progRepo,
    ProgressionService progService,
    AchievementRepository achievementRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private const int AchPageSize = 5;

    [SlashCommand("quests", "Посмотреть ваши активные ежедневные и недельные квесты")]
    public async Task QuestsCommandAsync()
    {
        var (embed, comp) = await BuildQuestsViewAsync(Context.User.Id);
        await RespondAsync(embed: embed, components: comp);
    }

    [ComponentInteraction("quests:claim:*", true)]
    public async Task HandleClaimQuestsButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
        {
            await RespondAsync(embed: embeds.Error("Это панель заданий другого участника."), ephemeral: true);
            return;
        }

        var (count, coins, xp, bpXp) = await progService.ClaimCompletedQuestsAsync(Context.Guild.Id, userId);
        if (count == 0)
        {
            await RespondAsync(embed: embeds.Error("У вас пока нет выполненных заданий, готовых к получению награды."), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var (updatedEmbed, updatedComp) = await BuildQuestsViewAsync(userId);

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = updatedEmbed;
            m.Components = updatedComp;
        });

        await FollowupAsync(embed: embeds.Success("Награды за квесты получены!",
            $"Завершено заданий: **{count}**\n" +
            $"• Валюта: **+{coins:N0} {cfg.CurrencySymbol}**\n" +
            $"• Опыт уровня: **+{xp:N0} XP**\n" +
            $"• Опыт Батлпасса: **+{bpXp:N0} BP XP**"), ephemeral: true);
    }

    [SlashCommand("achievements", "Каталог достижений сервера KRLZ и ваш личный прогресс")]
    public async Task AchievementsCommandAsync([Summary("user", "Участник")] SocketGuildUser? target = null)
    {
        var member = target ?? (SocketGuildUser)Context.User;
        var (embed, comp) = await BuildAchievementsPageAsync(member.Id, member.DisplayName, 0);
        await RespondAsync(embed: embed, components: comp);
    }

    [ComponentInteraction("ach:page:*:*", true)]
    public async Task HandleAchievementsPageAsync(string userIdStr, string pageStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || !int.TryParse(pageStr, out var page))
            return;

        var name = Context.Guild.GetUser(userId)?.DisplayName ?? $"ID {userId}";
        var (embed, comp) = await BuildAchievementsPageAsync(userId, name, page);

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Embed = embed;
            m.Components = comp;
        });
    }

    [Group("battlepass", "Сезонный боевой пропуск KRLZ с бесплатной и премиум-веткой наград")]
    public sealed class BattlePassSubgroup(
        ProgressionRepository progRepo,
        ProgressionService progService,
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("view", "Открыть графический трек текущего сезона Батлпасса")]
        public async Task ViewBattlePassCommandAsync()
        {
            await DeferAsync();
            var bpCfg = settingsProvider.Current.BattlePass;
            var season = await progRepo.GetOrCreateActiveSeasonAsync(
                Context.Guild.Id, bpCfg.DefaultSeasonDays, bpCfg.MaxLevel, bpCfg.XpPerLevel, bpCfg.PremiumPrice);
            var prog = await progRepo.GetOrCreateUserProgressAsync(Context.Guild.Id, season.Id, Context.User.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var png = await progService.RenderBattlePassTrackImageAsync(season, prog, Context.User.Username);

            var comp = new ComponentBuilder()
                .WithButton("Забрать доступные награды", $"bp:claim:{Context.User.Id}", ButtonStyle.Success)
                .WithButton($"Купить Премиум ({season.PremiumPrice:N0} {cfg.CurrencySymbol})", $"bp:buy_prem:{Context.User.Id}", ButtonStyle.Primary, disabled: prog.IsPremium)
                .Build();

            await using var stream = new MemoryStream(png);
            await FollowupWithFileAsync(stream, "battlepass.png", components: comp);
        }

        [SlashCommand("claim", "Забрать все открытые награды Батлпасса")]
        public async Task ClaimBattlePassCommandAsync()
        {
            var (count, list) = await progService.ClaimBattlePassRewardsAsync(Context.Guild.Id, Context.User.Id);
            if (count == 0)
            {
                await RespondAsync(embed: embeds.Error("Нет новых доступных наград для получения. Повышайте уровень Батлпасса за активность и квесты!"), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Success($"Получено наград Батлпасса: {count}", string.Join(Environment.NewLine, list)));
        }

        [SlashCommand("premium", "Приобрести Премиум-пропуск текущего сезона за валюту сервера")]
        public async Task BuyPremiumCommandAsync()
        {
            var bpCfg = settingsProvider.Current.BattlePass;
            var season = await progRepo.GetOrCreateActiveSeasonAsync(
                Context.Guild.Id, bpCfg.DefaultSeasonDays, bpCfg.MaxLevel, bpCfg.XpPerLevel, bpCfg.PremiumPrice);

            var (ok, err) = await progRepo.UpgradeToPremiumAsync(Context.Guild.Id, season.Id, Context.User.Id, season.PremiumPrice);
            if (!ok)
            {
                await RespondAsync(embed: embeds.Error(err), ephemeral: true);
                return;
            }

            await RespondAsync(embed: embeds.Success("Премиум Батлпасс активирован!",
                $"Вы открыли доступ ко всей премиум-ветке сезона **{season.Name}**! Используйте `/battlepass claim` для сбора наград."));
        }
    }

    [ComponentInteraction("bp:claim:*", true)]
    public async Task HandleBpClaimButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
        {
            await RespondAsync(embed: embeds.Error("Эта кнопка привязана к профилю другого участника."), ephemeral: true);
            return;
        }

        var (count, list) = await progService.ClaimBattlePassRewardsAsync(Context.Guild.Id, userId);
        if (count == 0)
        {
            await RespondAsync(embed: embeds.Error("Все доступные на текущем уровне награды уже собраны."), ephemeral: true);
            return;
        }

        await RespondAsync(embed: embeds.Success($"Собрано наград: {count}", string.Join(Environment.NewLine, list)), ephemeral: true);
    }

    [ComponentInteraction("bp:buy_prem:*", true)]
    public async Task HandleBpBuyPremButtonAsync(string userIdStr)
    {
        if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
        {
            await RespondAsync(embed: embeds.Error("Эта кнопка привязана к профилю другого участника."), ephemeral: true);
            return;
        }

        var bpCfg = settingsProvider.Current.BattlePass;
        var season = await progRepo.GetOrCreateActiveSeasonAsync(
            Context.Guild.Id, bpCfg.DefaultSeasonDays, bpCfg.MaxLevel, bpCfg.XpPerLevel, bpCfg.PremiumPrice);

        var (ok, err) = await progRepo.UpgradeToPremiumAsync(Context.Guild.Id, season.Id, userId, season.PremiumPrice);
        if (!ok)
        {
            await RespondAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        await RespondAsync(embed: embeds.Success("Премиум-ветка открыта!",
            $"Вы приобрели Премиум-пропуск сезона **{season.Name}**! Нажмите **Забрать доступные награды**."), ephemeral: true);
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildQuestsViewAsync(ulong userId)
    {
        var quests = await progRepo.GetOrAssignActiveQuestsAsync(Context.Guild.Id, userId);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var sb = new StringBuilder();

        foreach (var q in quests)
        {
            var periodTag = q.Definition.QuestPeriod == "weekly" ? "Еженедельный" : "Ежедневный";
            var bar = BuildAsciiBar(q.Progress.Progress, q.Definition.TargetValue, 10);
            var status = q.Progress.Claimed
                ? "Награда получена"
                : q.Progress.Completed
                    ? "Готово к получению!"
                    : $"{q.Progress.Progress:N0} / {q.Definition.TargetValue:N0}";

            sb.AppendLine($"**[{periodTag}] {q.Definition.Name}**");
            sb.AppendLine($"{q.Definition.Description}");
            sb.AppendLine($"`{bar}` — **{status}**");
            sb.AppendLine($"Награда: `{q.Definition.RewardCoins:N0} {cfg.CurrencySymbol}` • `{q.Definition.RewardXp} XP` • `{q.Definition.RewardBpXp} BP XP` • Сброс: {TimestampTag.FromDateTimeOffset(q.Progress.ExpiresAt, TimestampTagStyles.Relative)}\n");
        }

        var anyClaimable = quests.Any(x => x.Progress.Completed && !x.Progress.Claimed);
        var comp = new ComponentBuilder()
            .WithButton("Забрать награду за выполненные квесты", $"quests:claim:{userId}", ButtonStyle.Success, disabled: !anyClaimable)
            .Build();

        return (embeds.Info("Доска заданий KRLZ", sb.ToString()), comp);
    }

    private async Task<(Embed Embed, MessageComponent Components)> BuildAchievementsPageAsync(ulong userId, string displayName, int page)
    {
        var all = await achievementRepo.GetUserAchievementsAsync(Context.Guild.Id, userId);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        var totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)AchPageSize));
        page = Math.Clamp(page, 0, totalPages - 1);
        var slice = all.Skip(page * AchPageSize).Take(AchPageSize).ToList();

        var unlockedCount = all.Count(x => x.Progress.Unlocked);
        var sb = new StringBuilder();
        sb.AppendLine($"Разблокировано достижений: **{unlockedCount} / {all.Count}** (выбрать значки на карточку: `/profile-badges`)\n");

        foreach (var item in slice)
        {
            var a = item.Achievement;
            var p = item.Progress;
            var state = p.Unlocked ? "РАЗБЛОКИРОВАНО" : $"{p.CurrentValue:N0} / {a.TargetValue:N0}";
            var slotStr = p.EquippedSlot.HasValue ? $" • `[Слот #{p.EquippedSlot.Value}]`" : string.Empty;

            sb.AppendLine($"• **{a.Name}** (`{a.Code}`) [{ItemService.FormatRarityLabel(a.Rarity)}] — Значок: `{a.BadgeIcon}`{slotStr}");
            sb.AppendLine($"  {a.Description}");
            sb.AppendLine($"  Статус: **{state}** • Награда: `{a.RewardCoins:N0} {cfg.CurrencySymbol}` + `{a.RewardXp} XP`\n");
        }

        var embed = embeds.Info($"Достижения — {displayName} (Стр. {page + 1}/{totalPages})", sb.ToString());
        var comp = new ComponentBuilder()
            .WithButton("Назад", $"ach:page:{userId}:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
            .WithButton("Вперёд", $"ach:page:{userId}:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages - 1)
            .Build();

        return (embed, comp);
    }

    private static string BuildAsciiBar(long current, long max, int segments)
    {
        var ratio = max > 0 ? Math.Clamp((double)current / max, 0.0, 1.0) : 0.0;
        var filled = (int)Math.Round(ratio * segments);
        return new string('█', filled) + new string('░', Math.Max(0, segments - filled));
    }
}
