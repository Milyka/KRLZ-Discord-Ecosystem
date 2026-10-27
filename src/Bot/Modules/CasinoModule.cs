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
public sealed class CasinoModule(
    CasinoService casinoService,
    CasinoRepository casinoRepo,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    public sealed class RouletteBetModal : IModal
    {
        public string Title => "Ставка на стол рулетки";

        [InputLabel("Сумма ставки")]
        [ModalTextInput("amount", TextInputStyle.Short, "250", minLength: 1, maxLength: 12)]
        public string Amount { get; set; } = string.Empty;

        [InputLabel("Число от 0 до 36 (только для ставки на число)")]
        [ModalTextInput("number", TextInputStyle.Short, "17", minLength: 0, maxLength: 2)]
        public string? Number { get; set; }
    }

    [SlashCommand("blackjack", "Сыграть партию в блэкджек против дилера KRLZ")]
    public async Task BlackjackCommandAsync([Summary("bet", "Сумма ставки")] [MinValue(10)] long bet)
    {
        await DeferAsync();
        var existing = await casinoRepo.GetActiveBlackjackAsync(Context.Guild.Id, Context.User.Id);
        if (existing is not null)
        {
            var cfgExisting = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var resetComp = new ComponentBuilder()
                .WithButton("Сбросить зависшую раздачу и вернуть ставку", $"bj:reset:{existing.Id}", ButtonStyle.Danger)
                .Build();

            await FollowupAsync(
                embed: embeds.Warning(
                    "Незавершённая раздача",
                    $"У вас уже есть незавершённая раздача блэкджека (ставка: **{existing.Bet:N0} {cfgExisting.CurrencySymbol}**).\n\n" +
                    "Если сообщение с картами потеряно или бот перезапускался во время игры, нажмите кнопку ниже, чтобы вернуть ставку на баланс и начать заново."),
                components: resetComp,
                ephemeral: true);
            return;
        }

        var (allowed, err) = await casinoService.ValidateAndHoldBetAsync(
            Context.Guild.Id, Context.User.Id, bet, "blackjack_bet", "Ставка в блэкджеке");
        if (!allowed)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var deck = CasinoService.CreateShuffledDeck();
        var playerCards = new List<BlackjackCardDto> { PopCard(deck), PopCard(deck) };
        var dealerCards = new List<BlackjackCardDto> { PopCard(deck), PopCard(deck) };
        var hands = new List<BlackjackHandDto> { new(playerCards, bet, false, false) };

        var session = await casinoRepo.CreateBlackjackSessionAsync(
            Context.Guild.Id,
            Context.User.Id,
            Context.Channel.Id,
            bet,
            JsonSerializer.Serialize(deck),
            JsonSerializer.Serialize(hands),
            JsonSerializer.Serialize(dealerCards));

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        if (CasinoService.CalculateHandScore(playerCards) == 21)
        {
            var sem = casinoService.GetBlackjackLock(session.Id);
            await sem.WaitAsync();
            try
            {
                await SettleBlackjackSessionLockedAsync(session, deck, hands, dealerCards, cfg.CurrencySymbol, isInitialResponse: true);
            }
            finally
            {
                sem.Release();
                casinoService.ReleaseBlackjackLock(session.Id);
            }
            return;
        }

        var (embed, comp) = BuildBlackjackPlayView(session, hands, dealerCards, cfg.CurrencySymbol);
        await FollowupAsync(embed: embed, components: comp);
    }

    [ComponentInteraction("bj:*:*", true)]
    public async Task HandleBlackjackActionAsync(string action, string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        await DeferAsync();
        var sem = casinoService.GetBlackjackLock(sessionId);
        if (!await sem.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            await FollowupAsync(embed: embeds.Error("Подождите завершения предыдущего действия."), ephemeral: true);
            return;
        }

        var finishedNow = false;
        try
        {
            var session = await casinoRepo.GetBlackjackByIdAsync(sessionId);
            if (session is null || session.Status != "playing")
            {
                await FollowupAsync(embed: embeds.Error("Эта раздача уже завершена."), ephemeral: true);
                return;
            }

            if (session.UserId != Context.User.Id)
            {
                await FollowupAsync(embed: embeds.Error("Это карточный стол другого игрока."), ephemeral: true);
                return;
            }

            if (action == "reset")
            {
                await casinoRepo.UpdateBlackjackStatusAsync(session.Id, "finished");
                if (session.Bet > 0)
                {
                    await userRepo.TryModifyBalanceAsync(Context.Guild.Id, Context.User.Id, session.Bet, "blackjack_refund", "Сброс зависшей раздачи блэкджека");
                }
                finishedNow = true;
                var cfgReset = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
                await FollowupAsync(embed: embeds.Success("Раздача сброшена", $"Зависшая раздача блэкджека отменена! Ставка **{session.Bet:N0} {cfgReset.CurrencySymbol}** возвращена на ваш баланс."), ephemeral: true);
                return;
            }

            var deck = JsonSerializer.Deserialize<List<BlackjackCardDto>>(session.DeckJson) ?? CasinoService.CreateShuffledDeck();
            var hands = JsonSerializer.Deserialize<List<BlackjackHandDto>>(session.PlayerHandsJson) ?? [];
            var dealerCards = JsonSerializer.Deserialize<List<BlackjackCardDto>>(session.DealerHandJson) ?? [];
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var idx = Math.Clamp(session.ActiveHandIndex, 0, hands.Count - 1);
            var currentHand = hands[idx];

            if (action == "insure")
            {
                var insCost = Math.Max(1, currentHand.Bet / 2);
                var paid = await userRepo.TryModifyBalanceAsync(
                    Context.Guild.Id, Context.User.Id, -insCost, "blackjack_bet", "Страховка от блэкджека дилера");
                if (!paid)
                {
                    await FollowupAsync(embed: embeds.Error("Недостаточно средств для покупки страховки."), ephemeral: true);
                    return;
                }
                session = session with { InsuranceBet = insCost };
            }
            else if (action == "hit")
            {
                currentHand.Cards.Add(PopCard(deck));
                if (CasinoService.CalculateHandScore(currentHand.Cards) >= 21)
                {
                    hands[idx] = currentHand with { Stood = true };
                    session = AdvanceHandIndex(session, hands);
                }
            }
            else if (action == "stand")
            {
                hands[idx] = currentHand with { Stood = true };
                session = AdvanceHandIndex(session, hands);
            }
            else if (action == "double")
            {
                var paid = await userRepo.TryModifyBalanceAsync(
                    Context.Guild.Id, Context.User.Id, -currentHand.Bet, "blackjack_bet", "Удвоение ставки (Double Down)");
                if (!paid)
                {
                    await FollowupAsync(embed: embeds.Error("Недостаточно средств на балансе для удвоения ставки."), ephemeral: true);
                    return;
                }

                currentHand.Cards.Add(PopCard(deck));
                hands[idx] = currentHand with { Bet = currentHand.Bet * 2, Stood = true, Doubled = true };
                session = AdvanceHandIndex(session, hands);
            }
            else if (action == "split" && hands.Count == 1 && currentHand.Cards.Count == 2)
            {
                var paid = await userRepo.TryModifyBalanceAsync(
                    Context.Guild.Id, Context.User.Id, -currentHand.Bet, "blackjack_bet", "Разделение руки (Split)");
                if (!paid)
                {
                    await FollowupAsync(embed: embeds.Error("Недостаточно средств на балансе для разделения руки."), ephemeral: true);
                    return;
                }

                var c1 = currentHand.Cards[0];
                var c2 = currentHand.Cards[1];
                hands =
                [
                    new BlackjackHandDto([c1, PopCard(deck)], currentHand.Bet, false, false),
                    new BlackjackHandDto([c2, PopCard(deck)], currentHand.Bet, false, false)
                ];
                session = session with { ActiveHandIndex = 0 };
            }

            if (hands.All(h => h.Stood || CasinoService.CalculateHandScore(h.Cards) >= 21))
            {
                finishedNow = true;
                await SettleBlackjackSessionLockedAsync(session, deck, hands, dealerCards, cfg.CurrencySymbol, isInitialResponse: false);
                return;
            }

            session = session with
            {
                DeckJson = JsonSerializer.Serialize(deck),
                PlayerHandsJson = JsonSerializer.Serialize(hands),
                DealerHandJson = JsonSerializer.Serialize(dealerCards)
            };
            await casinoRepo.UpdatePlayingBlackjackSessionAsync(session);

            var (embed, comp) = BuildBlackjackPlayView(session, hands, dealerCards, cfg.CurrencySymbol);
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Components = comp;
            });
        }
        finally
        {
            sem.Release();
            if (finishedNow)
                casinoService.ReleaseBlackjackLock(sessionId);
        }
    }

    [SlashCommand("roulette", "Присоединиться к мультиплеерному столу рулетки (раунд 30 секунд)")]
    public async Task RouletteCommandAsync(
        [Summary("bet", "Сумма вашей ставки")] [MinValue(10)] long bet,
        [Summary("choice", "Вариант ставки (red, black, green, even, odd или число 0-36)")] string choice)
    {
        if (!ParseRouletteChoice(choice, out var betType, out var normalizedVal))
        {
            await RespondAsync(embed: embeds.Error("Укажите `red`, `black`, `green`, `even`, `odd` либо конкретное число от `0` до `36`."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var (accepted, err, round) = await casinoService.PlaceRouletteBetAsync(
            Context.Guild, Context.Channel.Id, Context.User.Id, betType, normalizedVal, bet);

        if (!accepted || round is null)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var (embed, comp) = BuildRouletteTableMessage(round, cfg.CurrencySymbol);
        await FollowupAsync(embed: embed, components: comp);
    }

    [ComponentInteraction("roul:bet:*", true)]
    public async Task HandleRouletteQuickBetButtonAsync(string kind)
    {
        await RespondWithModalAsync<RouletteBetModal>($"roul:modal:{kind}");
    }

    [ModalInteraction("roul:modal:*", true)]
    public async Task HandleRouletteBetModalAsync(string kind, RouletteBetModal modal)
    {
        if (!long.TryParse(modal.Amount.Trim(), out var amount) || amount <= 0)
        {
            await RespondAsync(embed: embeds.Error("Укажите корректную сумму ставки."), ephemeral: true);
            return;
        }

        var rawChoice = kind == "number" ? (modal.Number?.Trim() ?? "-1") : kind;
        if (!ParseRouletteChoice(rawChoice, out var betType, out var normalizedVal))
        {
            await RespondAsync(embed: embeds.Error("Для ставки на число укажите значение от 0 до 36."), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var (accepted, err, _) = await casinoService.PlaceRouletteBetAsync(
            Context.Guild, Context.Channel.Id, Context.User.Id, betType, normalizedVal, amount);

        if (!accepted)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await FollowupAsync(embed: embeds.Success("Ставка принята на стол рулетки",
            $"Ваша ставка **{amount:N0} {cfg.CurrencySymbol}** на `{normalizedVal}` добавлена в текущий раунд!"), ephemeral: true);
    }

    [SlashCommand("crash", "Запустить раунд игры Краш с живым графиком и кнопкой Cash Out")]
    public async Task CrashCommandAsync([Summary("bet", "Сумма ставки")] [MinValue(10)] long bet)
    {
        if (casinoService.GetCrashSession(Context.Guild.Id, Context.User.Id) is not null)
        {
            await RespondAsync(embed: embeds.Error("У вас уже запущен активный полёт в игре Краш."), ephemeral: true);
            return;
        }

        await DeferAsync();

        var (allowed, err) = await casinoService.ValidateAndHoldBetAsync(
            Context.Guild.Id, Context.User.Id, bet, "crash_bet", "Ставка в игре Краш");
        if (!allowed)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var crashPoint = casinoService.GenerateCrashMultiplier();
        var dbBetId = await casinoRepo.CreateCrashBetRecordAsync(
            Context.Guild.Id, Context.User.Id, Context.Channel.Id, bet, crashPoint);

        var session = new ActiveCrashSession
        {
            DbBetId = dbBetId,
            GuildId = Context.Guild.Id,
            UserId = Context.User.Id,
            Bet = bet,
            CrashPoint = crashPoint,
            CurrentMultiplier = 1.00
        };

        casinoService.TryRegisterCrashSession(session);
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

        try
        {
            // Плавная шкала множителей для удобного ручного Cash Out
            double[] steps = [1.00, 1.15, 1.32, 1.52, 1.75, 2.05, 2.45, 3.00, 3.80, 5.00, 7.00, 10.50, 16.00, 25.00, 38.00, 50.00];
            var lastEditAt = DateTimeOffset.MinValue;

            for (var i = 0; i < steps.Length; i++)
            {
                bool isCashedOut;
                lock (session.SyncRoot)
                {
                    isCashedOut = session.CashedOut;
                }
                if (isCashedOut)
                    break;

                var targetMult = steps[i];
                if (targetMult >= session.CrashPoint)
                {
                    lock (session.SyncRoot)
                    {
                        if (session.CashedOut)
                            break;
                        session.CurrentMultiplier = session.CrashPoint;
                    }

                    await casinoRepo.CompleteCrashBetRecordAsync(session.DbBetId, "crashed", null);
                    var sinceLast = DateTimeOffset.UtcNow - lastEditAt;
                    if (sinceLast < TimeSpan.FromMilliseconds(1150))
                        await Task.Delay(TimeSpan.FromMilliseconds(1150) - sinceLast);

                    var crashedBytes = await casinoService.RenderCrashChartAsync(
                        session.CrashPoint, true, false, bet, cfg.CurrencySymbol);

                    var crashedEmbed = embeds.Error(
                        $"Ракета взорвалась на отметке **{session.CrashPoint:F2}x**! Вы потеряли **{bet:N0} {cfg.CurrencySymbol}**.",
                        "Краш KRLZ — Взрыв").ToEmbedBuilder().WithImageUrl("attachment://crash.png").Build();

                    await using var cStream = new MemoryStream(crashedBytes);
                    if (i == 0)
                    {
                        await FollowupWithFileAsync(cStream, "crash.png", embed: crashedEmbed);
                    }
                    else
                    {
                        await ModifyOriginalResponseAsync(m =>
                        {
                            m.Embed = crashedEmbed;
                            m.Attachments = new List<FileAttachment> { new(cStream, "crash.png") };
                            m.Components = new ComponentBuilder().Build();
                        });
                    }
                    return;
                }

                lock (session.SyncRoot)
                {
                    session.CurrentMultiplier = targetMult;
                }

                var chartBytes = await casinoService.RenderCrashChartAsync(
                    targetMult, false, false, bet, cfg.CurrencySymbol);

                var comp = new ComponentBuilder()
                    .WithButton($"Забрать {(long)Math.Round(bet * targetMult):N0} {cfg.CurrencySymbol} ({targetMult:F2}x)", $"crash:cashout:{Context.User.Id}", ButtonStyle.Success)
                    .Build();

                var flyEmbed = embeds.CreateBase("Краш KRLZ — Ракета набирает высоту!")
                    .WithDescription($"Игрок: <@{Context.User.Id}> • Ставка: **{bet:N0} {cfg.CurrencySymbol}**\nНажмите **Забрать**, пока график не рухнул!")
                    .WithImageUrl("attachment://crash.png")
                    .Build();

                await using var stream = new MemoryStream(chartBytes);
                if (i == 0)
                {
                    await FollowupWithFileAsync(stream, "crash.png", embed: flyEmbed, components: comp);
                }
                else
                {
                    await ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = flyEmbed;
                        m.Attachments = new List<FileAttachment> { new(stream, "crash.png") };
                        m.Components = comp;
                    });
                }

                lastEditAt = DateTimeOffset.UtcNow;
                await Task.Delay(1350);
            }

            double finalMult;
            bool cashed;
            lock (session.SyncRoot)
            {
                cashed = session.CashedOut;
                finalMult = session.CashedOutAt;
            }

            if (cashed)
            {
                var sinceLast = DateTimeOffset.UtcNow - lastEditAt;
                if (sinceLast < TimeSpan.FromMilliseconds(1150))
                    await Task.Delay(TimeSpan.FromMilliseconds(1150) - sinceLast);

                var winAmount = (long)Math.Round(bet * finalMult);
                var winBytes = await casinoService.RenderCrashChartAsync(
                    finalMult, false, true, bet, cfg.CurrencySymbol);

                var winEmbed = embeds.Success("Краш KRLZ — Успешный вывод!",
                        $"Вы зафиксировали множитель **{finalMult:F2}x** и получили **{winAmount:N0} {cfg.CurrencySymbol}**!\n*(Ракета взорвалась бы на {session.CrashPoint:F2}x)*")
                    .ToEmbedBuilder()
                    .WithImageUrl("attachment://crash.png")
                    .Build();

                await using var wStream = new MemoryStream(winBytes);
                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = winEmbed;
                    m.Attachments = new List<FileAttachment> { new(wStream, "crash.png") };
                    m.Components = new ComponentBuilder().Build();
                });
            }
        }
        finally
        {
            casinoService.RemoveCrashSession(Context.Guild.Id, Context.User.Id);
        }
    }

    [ComponentInteraction("crash:cashout:*", true)]
    public async Task HandleCrashCashoutButtonAsync(string userIdStr)
    {
        await DeferAsync(ephemeral: true);

        if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
        {
            await FollowupAsync(embed: embeds.Error("Эта кнопка принадлежит другому игроку."), ephemeral: true);
            return;
        }

        var session = casinoService.GetCrashSession(Context.Guild.Id, userId);
        if (session is null)
        {
            await FollowupAsync(embed: embeds.Error("Раунд уже завершён."), ephemeral: true);
            return;
        }

        double lockedMult;
        lock (session.SyncRoot)
        {
            if (session.CashedOut || session.CurrentMultiplier >= session.CrashPoint)
            {
                lockedMult = 0;
            }
            else
            {
                session.CashedOut = true;
                session.CashedOutAt = session.CurrentMultiplier;
                lockedMult = session.CashedOutAt;
            }
        }

        if (lockedMult <= 0)
        {
            await FollowupAsync(embed: embeds.Error("Вы уже вывели ставку или ракета взорвалась."), ephemeral: true);
            return;
        }

        var transitioned = await casinoRepo.CompleteCrashBetRecordAsync(session.DbBetId, "cashed_out", lockedMult);
        if (!transitioned)
        {
            await FollowupAsync(embed: embeds.Error("Ставка уже рассчитана."), ephemeral: true);
            return;
        }

        var payout = (long)Math.Round(session.Bet * lockedMult);
        await casinoService.PayoutWinAsync(
            Context.Guild, userId, payout, "crash_win", $"Cash Out в Краше на {lockedMult:F2}x", Context.Channel);

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await FollowupAsync(embed: embeds.Success("Cash Out зафиксирован!",
            $"Вы забрали **{payout:N0} {cfg.CurrencySymbol}** на множителе **{lockedMult:F2}x**!"), ephemeral: true);
    }

    [Group("jackpot", "Общий банк Джекпота с выбором победителя по весу ставок")]
    public sealed class JackpotSubgroup(
        CasinoService casinoService,
        GuildSettingsRepository settingsRepo,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("join", "Внести ставку в общий банк текущего раунда Джекпота")]
        public async Task JoinJackpotCommandAsync([Summary("bet", "Сумма взноса в общий пул")] [MinValue(25)] long bet)
        {
            await DeferAsync();
            var (accepted, err, round, totalPool) = await casinoService.JoinJackpotRoundAsync(
                Context.Guild, Context.Channel.Id, Context.User.Id, bet);

            if (!accepted || round is null)
            {
                await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
                return;
            }

            var lines = new List<string>();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            lock (round.SyncRoot)
            {
                foreach (var kvp in round.Entries.OrderByDescending(x => x.Value))
                {
                    var pct = totalPool > 0 ? kvp.Value * 100.0 / totalPool : 0;
                    lines.Add($"• <@{kvp.Key}> — **{kvp.Value:N0} {cfg.CurrencySymbol}** (шанс: **{pct:F1}%**)");
                }
            }

            var desc = string.Join(Environment.NewLine,
                $"**Общий банк раунда:** `{totalPool:N0} {cfg.CurrencySymbol}`",
                $"**Розыгрыш победителя:** {TimestampTag.FromDateTimeOffset(round.EndsAt, TimestampTagStyles.Relative)}",
                string.Empty,
                "**Участники и шансы:**",
                string.Join(Environment.NewLine, lines));

            await FollowupAsync(embed: embeds.Info("Джекпот KRLZ — Ставка принята", desc));
        }
    }

    [Group("casino", "Статистика и информация об играх казино KRLZ")]
    public sealed class CasinoStatsSubgroup(
        CasinoRepository casinoRepo,
        GuildSettingsRepository settingsRepo,
        SettingsProvider settingsProvider,
        EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
    {
        [SlashCommand("stats", "Посмотреть личную статистику игр, выигрышей и дневного лимита в казино")]
        public async Task CasinoStatsCommandAsync([Summary("user", "Участник")] SocketGuildUser? target = null)
        {
            await DeferAsync(ephemeral: target is null);
            var member = target ?? (SocketGuildUser)Context.User;
            var stats = await casinoRepo.GetUserCasinoStatsAsync(Context.Guild.Id, member.Id);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var dailyLimit = settingsProvider.Current.Casino.DailyLossLimit;

            var profitStr = stats.NetProfit >= 0
                ? $"+{stats.NetProfit:N0} {cfg.CurrencySymbol}"
                : $"{stats.NetProfit:N0} {cfg.CurrencySymbol}";

            var embed = embeds.CreateBase($"Статистика казино — {member.DisplayName}")
                .WithThumbnailUrl(member.GetAvatarUrl() ?? member.GetDefaultAvatarUrl())
                .AddField("Сделано ставок", stats.TotalBetsCount.ToString("N0"), true)
                .AddField("Победных раундов", stats.TotalWinsCount.ToString("N0"), true)
                .AddField("Чистый профит", $"`{profitStr}`", true)
                .AddField("Сумма всех ставок", $"{stats.TotalWagered:N0} {cfg.CurrencySymbol}", true)
                .AddField("Сумма выплат", $"{stats.TotalWon:N0} {cfg.CurrencySymbol}", true)
                .AddField("Дневной стоп-лосс", $"{stats.TodayNetLoss:N0} / {dailyLimit:N0} {cfg.CurrencySymbol}", true)
                .Build();

            await FollowupAsync(embed: embed, ephemeral: target is null);
        }
    }

    private static BlackjackCardDto PopCard(List<BlackjackCardDto> deck)
    {
        if (deck.Count == 0)
            deck.AddRange(CasinoService.CreateShuffledDeck());

        var card = deck[^1];
        deck.RemoveAt(deck.Count - 1);
        return card;
    }

    private static BlackjackSessionEntity AdvanceHandIndex(BlackjackSessionEntity session, List<BlackjackHandDto> hands)
    {
        for (var i = session.ActiveHandIndex + 1; i < hands.Count; i++)
        {
            if (!hands[i].Stood && CasinoService.CalculateHandScore(hands[i].Cards) < 21)
                return session with { ActiveHandIndex = i };
        }
        return session;
    }

    private (Embed Embed, MessageComponent Components) BuildBlackjackPlayView(
        BlackjackSessionEntity session,
        List<BlackjackHandDto> hands,
        List<BlackjackCardDto> dealerCards,
        string currencySymbol)
    {
        var dealerUpCard = dealerCards[0];
        var builder = embeds.CreateBase("Блэкджек KRLZ — Ход игрока")
            .AddField("Карты дилера", $"`[{dealerUpCard.Rank}{dealerUpCard.Suit}]` `[?]`", false);

        for (var i = 0; i < hands.Count; i++)
        {
            var h = hands[i];
            var score = CasinoService.CalculateHandScore(h.Cards);
            var activeMarker = i == session.ActiveHandIndex ? " (Активная рука)" : string.Empty;
            builder.AddField(
                $"Рука #{i + 1}{activeMarker} — Очки: {score} | Ставка: {h.Bet:N0} {currencySymbol}",
                CasinoService.FormatCards(h.Cards),
                false);
        }

        var activeHand = hands[Math.Clamp(session.ActiveHandIndex, 0, hands.Count - 1)];
        var canSplit = hands.Count == 1 &&
                       activeHand.Cards.Count == 2 &&
                       CasinoService.GetCardRankValue(activeHand.Cards[0]) == CasinoService.GetCardRankValue(activeHand.Cards[1]);

        var canDouble = activeHand.Cards.Count == 2;
        var canInsure = dealerUpCard.Rank == "A" && session.InsuranceBet == 0 && activeHand.Cards.Count == 2;

        var comp = new ComponentBuilder()
            .WithButton("Ещё (Hit)", $"bj:hit:{session.Id}", ButtonStyle.Primary)
            .WithButton("Хватит (Stand)", $"bj:stand:{session.Id}", ButtonStyle.Secondary)
            .WithButton("Удвоить (Double)", $"bj:double:{session.Id}", ButtonStyle.Success, disabled: !canDouble)
            .WithButton("Сплит (Split)", $"bj:split:{session.Id}", ButtonStyle.Secondary, disabled: !canSplit);

        if (canInsure)
            comp.WithButton("Страховка (Insurance)", $"bj:insure:{session.Id}", ButtonStyle.Danger, row: 1);

        return (builder.Build(), comp.Build());
    }

    private async Task SettleBlackjackSessionLockedAsync(
        BlackjackSessionEntity session,
        List<BlackjackCardDto> deck,
        List<BlackjackHandDto> hands,
        List<BlackjackCardDto> dealerCards,
        string currencySymbol,
        bool isInitialResponse)
    {
        while (hands.Any(h => CasinoService.CalculateHandScore(h.Cards) <= 21) &&
               CasinoService.CalculateHandScore(dealerCards) < 17)
        {
            dealerCards.Add(PopCard(deck));
        }

        var updatedSession = session with
        {
            DeckJson = JsonSerializer.Serialize(deck),
            PlayerHandsJson = JsonSerializer.Serialize(hands),
            DealerHandJson = JsonSerializer.Serialize(dealerCards)
        };

        var transitioned = await casinoRepo.TryTransitionBlackjackToFinishedAsync(updatedSession, "finished");
        if (!transitioned)
            return;

        var dealerScore = CasinoService.CalculateHandScore(dealerCards);
        var dealerNaturalBj = dealerCards.Count == 2 && dealerScore == 21;
        long totalPayout = 0;
        var resultLines = new List<string>();

        if (session.InsuranceBet > 0 && dealerNaturalBj)
        {
            var insWin = session.InsuranceBet * 3;
            totalPayout += insWin;
            resultLines.Add($"• **Страховка сыграла:** +{insWin:N0} {currencySymbol}");
        }

        for (var i = 0; i < hands.Count; i++)
        {
            var h = hands[i];
            var pScore = CasinoService.CalculateHandScore(h.Cards);
            var playerNaturalBj = hands.Count == 1 && h.Cards.Count == 2 && pScore == 21;

            if (pScore > 21)
            {
                resultLines.Add($"• **Рука #{i + 1}** ({pScore} очк.): Перебор — проигрыш `{h.Bet:N0} {currencySymbol}`");
            }
            else if (playerNaturalBj && !dealerNaturalBj)
            {
                var win = (long)Math.Round(h.Bet * 2.5);
                totalPayout += win;
                resultLines.Add($"• **Рука #{i + 1}** (Блэкджек 21!): Выплата **`{win:N0} {currencySymbol}`** (2.5x)");
            }
            else if (dealerScore > 21 || pScore > dealerScore)
            {
                var win = h.Bet * 2;
                totalPayout += win;
                resultLines.Add($"• **Рука #{i + 1}** ({pScore} vs {dealerScore}): Победа — выплата **`{win:N0} {currencySymbol}`**");
            }
            else if (pScore == dealerScore)
            {
                totalPayout += h.Bet;
                resultLines.Add($"• **Рука #{i + 1}** ({pScore} vs {dealerScore}): Ровно (Push) — возврат `{h.Bet:N0} {currencySymbol}`");
            }
            else
            {
                resultLines.Add($"• **Рука #{i + 1}** ({pScore} vs {dealerScore}): Победа дилера — проигрыш `{h.Bet:N0} {currencySymbol}`");
            }
        }

        if (totalPayout > 0)
        {
            await casinoService.PayoutWinAsync(
                Context.Guild, session.UserId, totalPayout, "blackjack_win", "Выплата в блэкджеке", Context.Channel);
        }

        var builder = embeds.CreateBase(
                "Блэкджек KRLZ — Итог раздачи",
                string.Join(Environment.NewLine, resultLines),
                totalPayout > 0 ? new Color(87, 242, 135) : new Color(237, 66, 69))
            .AddField($"Карты дилера ({dealerScore} очк.)", CasinoService.FormatCards(dealerCards), false);

        for (var i = 0; i < hands.Count; i++)
        {
            var h = hands[i];
            builder.AddField($"Ваша рука #{i + 1} ({CasinoService.CalculateHandScore(h.Cards)} очк.)", CasinoService.FormatCards(h.Cards), true);
        }

        if (isInitialResponse)
        {
            await FollowupAsync(embed: builder.Build());
        }
        else
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = builder.Build();
                m.Components = null;
            });
        }
    }

    private static bool ParseRouletteChoice(string raw, out string betType, out string normalizedVal)
    {
        betType = string.Empty;
        normalizedVal = raw.Trim().ToLowerInvariant();

        if (normalizedVal is "red" or "красное")
        {
            betType = "color";
            normalizedVal = "red";
            return true;
        }
        if (normalizedVal is "black" or "чёрное" or "черное")
        {
            betType = "color";
            normalizedVal = "black";
            return true;
        }
        if (normalizedVal is "green" or "zero" or "зеро")
        {
            betType = "color";
            normalizedVal = "green";
            return true;
        }
        if (normalizedVal is "even" or "чёт" or "чет")
        {
            betType = "parity";
            normalizedVal = "even";
            return true;
        }
        if (normalizedVal is "odd" or "нечёт" or "нечет")
        {
            betType = "parity";
            normalizedVal = "odd";
            return true;
        }
        if (int.TryParse(normalizedVal, out var num) && num is >= 0 and <= 36)
        {
            betType = "number";
            normalizedVal = num.ToString();
            return true;
        }

        return false;
    }

    private (Embed Embed, MessageComponent Components) BuildRouletteTableMessage(RouletteTableRound round, string currencySymbol)
    {
        List<RouletteBetEntry> snapshot;
        lock (round.SyncRoot)
        {
            snapshot = round.Bets.ToList();
        }

        var lines = snapshot
            .TakeLast(12)
            .Select(b => $"• <@{b.UserId}>: **{b.Amount:N0} {currencySymbol}** на `{b.BetValue}`")
            .ToList();

        var desc = string.Join(Environment.NewLine,
            $"Вращение колеса: {TimestampTag.FromDateTimeOffset(round.EndsAt, TimestampTagStyles.Relative)}",
            $"Всего ставок на столе: **{snapshot.Count}** (сумма: **{snapshot.Sum(x => x.Amount):N0} {currencySymbol}**)",
            string.Empty,
            string.Join(Environment.NewLine, lines));

        var comp = new ComponentBuilder()
            .WithButton("Красное (2x)", "roul:bet:red", ButtonStyle.Danger)
            .WithButton("Чёрное (2x)", "roul:bet:black", ButtonStyle.Secondary)
            .WithButton("Зеро (14x)", "roul:bet:green", ButtonStyle.Success)
            .WithButton("Чётное (2x)", "roul:bet:even", ButtonStyle.Primary, row: 1)
            .WithButton("Нечётное (2x)", "roul:bet:odd", ButtonStyle.Primary, row: 1)
            .WithButton("Точное число (36x)", "roul:bet:number", ButtonStyle.Secondary, row: 1)
            .Build();

        return (embeds.Info("Рулетка KRLZ — Приём ставок открыт", desc), comp);
    }
}
