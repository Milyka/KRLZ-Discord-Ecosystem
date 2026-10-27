using System.Text;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Bot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class EconomyModule(
    EconomyService economyService,
    CasinoService casinoService,
    ProfileCardService profileCardService,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly (string Symbol, int Weight, double TripleMultiplier)[] SlotSymbols =
    [
        ("🍒", 34, 2.0),
        ("🍋", 26, 3.0),
        ("🍇", 18, 4.0),
        ("🔔", 12, 6.0),
        ("💎", 7, 10.0),
        ("7️⃣", 3, 25.0)
    ];

    [SlashCommand("balance", "Открыть графическую карточку финансового баланса и места в рейтинге")]
    public async Task BalanceCommandAsync([Summary("user", "Участник")] SocketGuildUser? target = null)
    {
        await DeferAsync();
        var member = target ?? (SocketGuildUser)Context.User;
        var profile = await economyService.GetProfileAsync(Context.Guild.Id, member.Id);
        var balanceRank = await userRepo.GetUserRankByBalanceAsync(Context.Guild.Id, member.Id);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        var png = await profileCardService.RenderBalanceCardAsync(
            member, profile, cfg.CurrencyName, cfg.CurrencySymbol, balanceRank);

        var embed = new EmbedBuilder()
            .WithColor(embeds.GetThemeColor())
            .WithImageUrl("attachment://balance-card.png")
            .Build();

        await using var stream = new MemoryStream(png);
        await FollowupWithFileAsync(stream, "balance-card.png", embed: embed);
    }

    [SlashCommand("pay", "Перевести валюту другому участнику сервера")]
    [RateLimit(5)]
    public async Task PayCommandAsync(
        [Summary("user", "Получатель перевода")] SocketGuildUser recipient,
        [Summary("amount", "Сумма перевода")] [MinValue(1)] long amount,
        [Summary("comment", "Комментарий к переводу")] string comment = "Прямой перевод")
    {
        if (recipient.IsBot || recipient.Id == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Нельзя переводить средства себе или ботам."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var ok = await userRepo.TryTransferAsync(Context.Guild.Id, Context.User.Id, recipient.Id, amount, comment);

        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error("Недостаточно средств на балансе для выполнения перевода."), ephemeral: true);
            return;
        }

        var lines = new[]
        {
            $"**Отправитель:** <@{Context.User.Id}>",
            $"**Получатель:** <@{recipient.Id}>",
            $"**Сумма:** **{amount:N0} {cfg.CurrencySymbol}**",
            $"**Комментарий:** {comment}"
        };

        await FollowupAsync(embed: embeds.Success("Перевод выполнен", string.Join(Environment.NewLine, lines)));
    }

    [SlashCommand("daily", "Получить ежедневную награду в валюте сервера")]
    public async Task DailyCommandAsync()
    {
        await DeferAsync();
        var (ok, msg, _) = await economyService.ClaimDailyAsync(Context.Guild.Id, Context.User.Id);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Ежедневный бонус", msg));
    }

    [SlashCommand("work", "Выполнить смену и заработать валюту сервера")]
    public async Task WorkCommandAsync()
    {
        await DeferAsync();
        var (ok, msg, _) = await economyService.WorkAsync(Context.Guild.Id, Context.User.Id);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(msg), ephemeral: true);
            return;
        }

        await FollowupAsync(embed: embeds.Success("Рабочая смена завершена", msg));
    }

    [SlashCommand("slots", "Сыграть в игровой автомат на валюту сервера")]
    public async Task SlotsCommandAsync([Summary("bet", "Ставка")] [MinValue(10)] long bet)
    {
        if (!economyService.CheckSlotsCooldown(Context.Guild.Id, Context.User.Id, out var waitSec))
        {
            await RespondAsync(embed: embeds.Error($"Подождите ещё **{waitSec} сек.** перед следующим вращением барабанов."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var (allowed, err) = await casinoService.ValidateAndHoldBetAsync(
            Context.Guild.Id, Context.User.Id, bet, "slots_bet", "Ставка в слотах", checkCooldown: false);
        if (!allowed)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var spin1 = RollSymbolCrypto();
        var spin2 = RollSymbolCrypto();
        var spin3 = RollSymbolCrypto();

        var frame1 = embeds.CreateBase("Игровой автомат KRLZ")
            .WithDescription(string.Join(Environment.NewLine,
                "# `[ ❔ | ❔ | ❔ ]`",
                string.Empty,
                $"**Ставка:** {bet:N0} {cfg.CurrencySymbol}"))
            .Build();

        await FollowupAsync(embed: frame1);

        await Task.Delay(650);
        var frame2 = embeds.CreateBase("Игровой автомат KRLZ")
            .WithDescription(string.Join(Environment.NewLine,
                $"# `[ {spin1.Symbol} | ❔ | ❔ ]`",
                string.Empty,
                $"**Ставка:** {bet:N0} {cfg.CurrencySymbol}"))
            .Build();
        await ModifyOriginalResponseAsync(m => m.Embed = frame2);

        await Task.Delay(650);
        var frame3 = embeds.CreateBase("Игровой автомат KRLZ")
            .WithDescription(string.Join(Environment.NewLine,
                $"# `[ {spin1.Symbol} | {spin2.Symbol} | ❔ ]`",
                string.Empty,
                $"**Ставка:** {bet:N0} {cfg.CurrencySymbol}"))
            .Build();
        await ModifyOriginalResponseAsync(m => m.Embed = frame3);

        await Task.Delay(650);

        double multiplier = 0;
        if (spin1.Symbol == spin2.Symbol && spin2.Symbol == spin3.Symbol)
            multiplier = spin1.TripleMultiplier;
        else if (spin1.Symbol == spin2.Symbol || spin2.Symbol == spin3.Symbol || spin1.Symbol == spin3.Symbol)
            multiplier = 1.5;

        var payout = (long)Math.Round(bet * multiplier);
        if (payout > 0)
        {
            await casinoService.PayoutWinAsync(
                Context.Guild, Context.User.Id, payout, "slots_win", $"Выигрыш в слотах ({multiplier}x)", Context.Channel);
        }

        var outcomeLine = payout > 0
            ? $"Вы выиграли **{payout:N0} {cfg.CurrencySymbol}** (множитель **{multiplier}x**)!"
            : $"Вы проиграли ставку **{bet:N0} {cfg.CurrencySymbol}**.";

        var finalEmbed = embeds.CreateBase(
                "Игровой автомат KRLZ",
                string.Join(Environment.NewLine,
                    $"# `[ {spin1.Symbol} | {spin2.Symbol} | {spin3.Symbol} ]`",
                    string.Empty,
                    outcomeLine),
                payout > 0 ? new Color(87, 242, 135) : new Color(237, 66, 69))
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Множители", "slots:payouts", ButtonStyle.Secondary, emote: new Emoji("💡"))
            .Build();

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = finalEmbed;
            m.Components = comp;
        });
    }

    [ComponentInteraction("slots:payouts", true)]
    public async Task HandleSlotsPayoutsButtonAsync()
    {
        var desc = string.Join(Environment.NewLine,
            "**Таблица множителей выигрыша:**",
            string.Empty,
            "• 7️⃣ 7️⃣ 7️⃣ — **25x**",
            "• 💎 💎 💎 — **10x**",
            "• 🔔 🔔 🔔 — **6x**",
            "• 🍇 🍇 🍇 — **4x**",
            "• 🍋 🍋 🍋 — **3x**",
            "• 🍒 🍒 🍒 — **2x**",
            string.Empty,
            "• **Любая пара (2 совпадения):** **1.5x**");

        var embed = new EmbedBuilder()
            .WithTitle("💡 Множители игрового автомата")
            .WithColor(new Color(43, 45, 49))
            .WithDescription(desc)
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    [SlashCommand("coinflip", "Подбросить монетку на ставку (орёл или решка)")]
    [RateLimit(4)]
    public async Task CoinflipCommandAsync(
        [Summary("side", "Выбор стороны"), Choice("Орёл", "eagle"), Choice("Решка", "tails")] string side,
        [Summary("bet", "Сумма ставки")] [MinValue(10)] long bet)
    {
        await DeferAsync();
        var (allowed, err) = await casinoService.ValidateAndHoldBetAsync(
            Context.Guild.Id, Context.User.Id, bet, "coinflip_bet", "Ставка в монетке");
        if (!allowed)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var rolled = CasinoService.NextCryptoInt(0, 2) == 0 ? "eagle" : "tails";
        var rolledName = rolled == "eagle" ? "Орёл" : "Решка";
        var won = string.Equals(side, rolled, StringComparison.OrdinalIgnoreCase);

        if (won)
        {
            var winAmount = bet * 2;
            await casinoService.PayoutWinAsync(
                Context.Guild, Context.User.Id, winAmount, "coinflip_win", "Победа в монетке", Context.Channel);
            await FollowupAsync(embed: embeds.Success("Монетка — Победа!",
                $"Выпало: **{rolledName}**\nВаш чистый выигрыш: **+{bet:N0} {cfg.CurrencySymbol}** (получено {winAmount:N0} {cfg.CurrencySymbol})."));
        }
        else
        {
            await FollowupAsync(embed: embeds.Error($"Выпало: **{rolledName}**. Вы потеряли **{bet:N0} {cfg.CurrencySymbol}**.", "Монетка — Проигрыш"));
        }
    }

    [SlashCommand("leaderboard-money", "Топ богатейших участников сервера")]
    public async Task LeaderboardMoneyCommandAsync()
    {
        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var top = await userRepo.GetTopAsync(Context.Guild.Id, "balance", "all", 10, 0);

        if (top.Count == 0)
        {
            await FollowupAsync(embed: embeds.Info("Рейтинг богачей", "Данных пока нет."));
            return;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < top.Count; i++)
        {
            var item = top[i];
            sb.AppendLine($"**{i + 1}.** <@{item.UserId}> — **{item.Value:N0} {cfg.CurrencySymbol}** (Ур. {item.Level})");
        }

        await FollowupAsync(embed: embeds.Info($"Топ-10 по балансу ({cfg.CurrencyName})", sb.ToString()));
    }

    private static (string Symbol, int Weight, double TripleMultiplier) RollSymbolCrypto()
    {
        var totalWeight = SlotSymbols.Sum(s => s.Weight);
        var roll = CasinoService.NextCryptoInt(0, totalWeight);
        var cumulative = 0;

        foreach (var sym in SlotSymbols)
        {
            cumulative += sym.Weight;
            if (roll < cumulative)
                return sym;
        }

        return SlotSymbols[0];
    }
}
