using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Bot.Services;

public sealed record BlackjackCardDto(string Rank, string Suit);

public sealed record BlackjackHandDto(List<BlackjackCardDto> Cards, long Bet, bool Stood, bool Doubled);

public sealed record RouletteBetEntry(long DbBetId, ulong UserId, string BetType, string BetValue, long Amount);

public sealed class RouletteTableRound
{
    public long DbRoundId { get; set; }
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public bool Closed { get; set; }
    public List<RouletteBetEntry> Bets { get; } = [];
    public object SyncRoot { get; } = new();
}

public sealed class JackpotPoolRound
{
    public long DbRoundId { get; set; }
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required DateTimeOffset EndsAt { get; init; }
    public bool Closed { get; set; }
    public Dictionary<ulong, long> Entries { get; } = [];
    public object SyncRoot { get; } = new();
}

public sealed class ActiveCrashSession
{
    public required long DbBetId { get; init; }
    public required ulong GuildId { get; init; }
    public required ulong UserId { get; init; }
    public required long Bet { get; init; }
    public required double CrashPoint { get; init; }
    public double CurrentMultiplier { get; set; } = 1.00;
    public bool CashedOut { get; set; }
    public double CashedOutAt { get; set; }
    public object SyncRoot { get; } = new();
}

public sealed class CasinoService(
    CasinoRepository casinoRepo,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    AchievementService achievementService,
    ProgressionService progressionService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds,
    ILogger<CasinoService> logger)
{
    private static readonly int[] RedNumbers =
    [
        1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36
    ];

    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);
    private static readonly SKTypeface FontRegular = LoadTypeface("Inter-Regular.ttf", SKFontStyle.Normal);

    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTimeOffset> _cooldowns = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _blackjackLocks = new();
    private readonly ConcurrentDictionary<ulong, RouletteTableRound> _rouletteRounds = new();
    private readonly ConcurrentDictionary<ulong, JackpotPoolRound> _jackpotRounds = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), ActiveCrashSession> _crashSessions = new();
    private readonly object _cooldownSync = new();

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle fallbackStyle)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, fallbackStyle);
    }

    public SemaphoreSlim GetBlackjackLock(long sessionId) =>
        _blackjackLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public void ReleaseBlackjackLock(long sessionId)
    {
        if (_blackjackLocks.TryRemove(sessionId, out var sem))
            sem.Dispose();
    }

    public static int NextCryptoInt(int minInclusive, int maxExclusive) =>
        RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);

    public static double NextCryptoDouble()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var ul = BitConverter.ToUInt64(bytes);
        return (ul >> 11) * (1.0 / (1UL << 53));
    }

    public double GenerateCrashMultiplier()
    {
        // Сбалансированная кривая полёта: 82% раундов преодолевают 1.75x+, давая комфортное окно для игры и Cash Out
        var roll = NextCryptoInt(1, 1001); // 1 .. 1000

        if (roll <= 50) // 5.0% — ранний срыв на старте (1.00x .. 1.25x)
            return Math.Round(1.00 + NextCryptoDouble() * 0.25, 2);

        if (roll <= 180) // 13.0% — низкая дистанция (1.30x .. 1.75x)
            return Math.Round(1.30 + NextCryptoDouble() * 0.45, 2);

        if (roll <= 550) // 37.0% — основная рабочая зона (1.80x .. 3.20x)
            return Math.Round(1.80 + NextCryptoDouble() * 1.40, 2);

        if (roll <= 820) // 27.0% — высокий полёт (3.25x .. 6.50x)
            return Math.Round(3.25 + NextCryptoDouble() * 3.25, 2);

        if (roll <= 950) // 13.0% — стратосфера (6.60x .. 15.00x)
            return Math.Round(6.60 + NextCryptoDouble() * 8.40, 2);

        // 5.0% — глубокий космос (15.50x .. 50.00x)
        return Math.Round(15.50 + NextCryptoDouble() * 34.50, 2);
    }

    public async Task RefundInterruptedSessionsOnStartupAsync(CancellationToken ct = default)
    {
        var count = await casinoRepo.RefundUnsettledCasinoBetsOnStartupAsync(ct);
        if (count > 0)
            logger.LogInformation("Возвращено неразыгранных ставок казино после перезапуска: {Count}", count);
    }

    public async Task<(bool Allowed, string Error)> ValidateAndHoldBetAsync(
        ulong guildId,
        ulong userId,
        long bet,
        string txType,
        string description,
        bool checkCooldown = true,
        CancellationToken ct = default)
    {
        var cfg = settingsProvider.Current.Casino;
        var guildCfg = await settingsRepo.GetOrCreateAsync(guildId, ct);

        if (bet < cfg.MinBet || bet > cfg.MaxBet)
        {
            return (false, $"Ставка должна быть в пределах от **{cfg.MinBet:N0}** до **{cfg.MaxBet:N0} {guildCfg.CurrencySymbol}**.");
        }

        var now = DateTimeOffset.UtcNow;
        var key = (guildId, userId);

        if (checkCooldown)
        {
            lock (_cooldownSync)
            {
                if (_cooldowns.TryGetValue(key, out var until) && until > now)
                {
                    var left = Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds));
                    return (false, $"Подождите **{left} сек.** перед следующей игрой в казино.");
                }
            }
        }

        var todayLoss = await casinoRepo.GetTodayNetLossAsync(guildId, userId, ct);
        if (todayLoss + bet > cfg.DailyLossLimit)
        {
            return (false, $"Сработала защита от лудомании: ваш чистый проигрыш за сегодня составил **{todayLoss:N0} {guildCfg.CurrencySymbol}** (дневной лимит: **{cfg.DailyLossLimit:N0} {guildCfg.CurrencySymbol}**).");
        }

        var deducted = await userRepo.TryModifyBalanceAsync(guildId, userId, -bet, txType, description, ct);
        if (!deducted)
        {
            return (false, "На вашем балансе недостаточно средств для этой ставки.");
        }

        if (checkCooldown)
        {
            lock (_cooldownSync)
            {
                _cooldowns[key] = DateTimeOffset.UtcNow.AddSeconds(cfg.CooldownSeconds);
            }
        }

        return (true, string.Empty);
    }

    public async Task PayoutWinAsync(
        SocketGuild guild,
        ulong userId,
        long payoutAmount,
        string txType,
        string description,
        ISocketMessageChannel? channel = null,
        CancellationToken ct = default)
    {
        if (payoutAmount <= 0)
            return;

        await userRepo.TryModifyBalanceAsync(guild.Id, userId, payoutAmount, txType, description, ct);
        await achievementService.TrackEventAsync(guild, userId, "casino_wins", 1, isAbsoluteValue: false, channel, ct);
        await progressionService.TrackActivityAsync(guild.Id, userId, "coins_won", payoutAmount, 15, ct);
    }

    public async Task ProcessAbandonedBlackjackSessionsAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var minutes = Math.Max(5, settingsProvider.Current.Casino.BlackjackAbandonedMinutes);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        var abandoned = await casinoRepo.GetAbandonedBlackjackSessionsAsync(cutoff, ct);

        foreach (var session in abandoned)
        {
            var sem = GetBlackjackLock(session.Id);
            await sem.WaitAsync(ct);
            try
            {
                var fresh = await casinoRepo.GetBlackjackByIdAsync(session.Id, ct);
                if (fresh is null || fresh.Status != "playing")
                    continue;

                var deck = JsonSerializer.Deserialize<List<BlackjackCardDto>>(fresh.DeckJson) ?? CreateShuffledDeck();
                var hands = JsonSerializer.Deserialize<List<BlackjackHandDto>>(fresh.PlayerHandsJson) ?? [];
                var dealerCards = JsonSerializer.Deserialize<List<BlackjackCardDto>>(fresh.DealerHandJson) ?? [];

                while (hands.Any(h => CalculateHandScore(h.Cards) <= 21) && CalculateHandScore(dealerCards) < 17)
                {
                    if (deck.Count == 0) deck.AddRange(CreateShuffledDeck());
                    dealerCards.Add(deck[^1]);
                    deck.RemoveAt(deck.Count - 1);
                }

                var updatedSession = fresh with
                {
                    DeckJson = JsonSerializer.Serialize(deck),
                    PlayerHandsJson = JsonSerializer.Serialize(hands),
                    DealerHandJson = JsonSerializer.Serialize(dealerCards)
                };

                var transitioned = await casinoRepo.TryTransitionBlackjackToFinishedAsync(updatedSession, "auto_finished", ct);
                if (!transitioned)
                    continue;

                var dealerScore = CalculateHandScore(dealerCards);
                var dealerNatural = dealerCards.Count == 2 && dealerScore == 21;
                long totalPayout = 0;

                if (fresh.InsuranceBet > 0 && dealerNatural)
                    totalPayout += fresh.InsuranceBet * 3;

                foreach (var h in hands)
                {
                    var pScore = CalculateHandScore(h.Cards);
                    if (pScore > 21) continue;
                    if (dealerScore > 21 || pScore > dealerScore) totalPayout += h.Bet * 2;
                    else if (pScore == dealerScore) totalPayout += h.Bet;
                }

                if (totalPayout > 0 && client.GetGuild(fresh.GuildId) is { } guild)
                {
                    await PayoutWinAsync(guild, fresh.UserId, totalPayout, "blackjack_win", "Авто-завершение раздачи блэкджека по тайм-ауту", null, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка авто-завершения сессии блэкджека #{Id}", session.Id);
            }
            finally
            {
                sem.Release();
                ReleaseBlackjackLock(session.Id);
            }
        }
    }

    public static List<BlackjackCardDto> CreateShuffledDeck()
    {
        string[] suits = ["♠", "♥", "♦", "♣"];
        string[] ranks = ["2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A"];
        var deck = new List<BlackjackCardDto>(52 * 2);

        for (var d = 0; d < 2; d++)
        {
            foreach (var s in suits)
            {
                foreach (var r in ranks)
                    deck.Add(new BlackjackCardDto(r, s));
            }
        }

        for (var i = deck.Count - 1; i > 0; i--)
        {
            var j = NextCryptoInt(0, i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        return deck;
    }

    public static int CalculateHandScore(IReadOnlyList<BlackjackCardDto> cards)
    {
        var sum = 0;
        var aces = 0;

        foreach (var c in cards)
        {
            if (c.Rank == "A")
            {
                aces++;
                sum += 11;
            }
            else if (c.Rank is "K" or "Q" or "J" or "10")
            {
                sum += 10;
            }
            else if (int.TryParse(c.Rank, out var v))
            {
                sum += v;
            }
        }

        while (sum > 21 && aces > 0)
        {
            sum -= 10;
            aces--;
        }

        return sum;
    }

    public static int GetCardRankValue(BlackjackCardDto card) => card.Rank switch
    {
        "A" => 11,
        "K" or "Q" or "J" or "10" => 10,
        _ => int.TryParse(card.Rank, out var v) ? v : 0
    };

    public static string FormatCards(IEnumerable<BlackjackCardDto> cards) =>
        string.Join(" ", cards.Select(c => $"`[{c.Rank}{c.Suit}]`"));

    public bool TryRegisterCrashSession(ActiveCrashSession session) =>
        _crashSessions.TryAdd((session.GuildId, session.UserId), session);

    public ActiveCrashSession? GetCrashSession(ulong guildId, ulong userId) =>
        _crashSessions.TryGetValue((guildId, userId), out var s) ? s : null;

    public void RemoveCrashSession(ulong guildId, ulong userId) =>
        _crashSessions.TryRemove((guildId, userId), out _);

    public async Task<byte[]> RenderCrashChartAsync(
        double currentMultiplier,
        bool crashed,
        bool cashedOut,
        long bet,
        string currencySymbol,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            const int width = 760;
            const int height = 320;

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(14, 16, 24));

            using var gridPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 18),
                StrokeWidth = 1f,
                IsAntialias = true
            };

            for (var y = 40; y < height - 40; y += 50)
                canvas.DrawLine(50, y, width - 30, y, gridPaint);
            for (var x = 50; x < width - 30; x += 90)
                canvas.DrawLine(x, 30, x, height - 40, gridPaint);

            var lineColor = crashed
                ? new SKColor(239, 68, 68)
                : cashedOut
                    ? new SKColor(16, 185, 129)
                    : new SKColor(99, 102, 241);

            using var curvePath = new SKPath();
            const float startX = 55f;
            const float startY = height - 45f;
            const float plotW = width - 105f;
            const float plotH = height - 95f;

            curvePath.MoveTo(startX, startY);
            const int steps = 40;
            var progress = Math.Clamp((float)Math.Log10(Math.Max(1.0, currentMultiplier)) / 1.4f, 0.08f, 1.0f);

            for (var i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                var px = startX + plotW * progress * t;
                var py = startY - plotH * progress * (t * t);
                curvePath.LineTo(px, py);
            }

            using var strokePaint = new SKPaint
            {
                Color = lineColor,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 4.5f,
                IsAntialias = true
            };
            canvas.DrawPath(curvePath, strokePaint);

            var multFormatted = string.Create(CultureInfo.InvariantCulture, $"{currentMultiplier:F2}x");
            using var multPaint = new SKPaint
            {
                Typeface = FontBold,
                TextSize = 54f,
                Color = lineColor,
                IsAntialias = true,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText(multFormatted, width / 2f, 135f, multPaint);

            var potentialWin = ((long)Math.Round(bet * currentMultiplier)).ToString("N0", CultureInfo.InvariantCulture);
            var statusText = crashed
                ? "КРАШ! Ракета взорвалась"
                : cashedOut
                    ? $"ВЫВЕДЕНО: +{potentialWin} {currencySymbol}"
                    : $"В ПОЛЁТЕ • Потенциал: {potentialWin} {currencySymbol}";

            using var subPaint = new SKPaint
            {
                Typeface = FontRegular,
                TextSize = 20f,
                Color = new SKColor(229, 231, 235),
                IsAntialias = true,
                TextAlign = SKTextAlign.Center
            };
            canvas.DrawText(statusText, width / 2f, 178f, subPaint);

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }, ct);
    }

    public async Task<(bool Accepted, string Error, RouletteTableRound? Round)> PlaceRouletteBetAsync(
        SocketGuild guild,
        ulong channelId,
        ulong userId,
        string betType,
        string betValue,
        long amount,
        CancellationToken ct = default)
    {
        var (allowed, err) = await ValidateAndHoldBetAsync(
            guild.Id, userId, amount, "roulette_bet", $"Ставка в рулетке на {betValue}", checkCooldown: false, ct);
        if (!allowed)
            return (false, err, null);

        RouletteTableRound round;
        var createdNew = false;

        if (!_rouletteRounds.TryGetValue(guild.Id, out var existing))
        {
            var candidate = new RouletteTableRound
            {
                GuildId = guild.Id,
                ChannelId = channelId,
                EndsAt = DateTimeOffset.UtcNow.AddSeconds(settingsProvider.Current.Casino.RouletteRoundSeconds)
            };

            if (_rouletteRounds.TryAdd(guild.Id, candidate))
            {
                round = candidate;
                createdNew = true;
                round.DbRoundId = await casinoRepo.CreateRouletteRoundRecordAsync(guild.Id, channelId, round.EndsAt, ct);
            }
            else
            {
                round = _rouletteRounds[guild.Id];
            }
        }
        else
        {
            round = existing;
        }

        var lateBet = false;
        lock (round.SyncRoot)
        {
            if (round.Closed)
            {
                lateBet = true;
            }
            else
            {
                round.Bets.Add(new RouletteBetEntry(0, userId, betType, betValue, amount));
            }
        }

        if (lateBet)
        {
            await userRepo.TryModifyBalanceAsync(guild.Id, userId, amount, "roulette_refund", "Возврат опоздавшей ставки в рулетке", ct);
            return (false, "Приём ставок на текущий раунд рулетки только что закрылся. Сумма ставки возвращена на ваш баланс.", null);
        }

        if (round.DbRoundId > 0)
        {
            await casinoRepo.AddRouletteBetRecordAsync(round.DbRoundId, guild.Id, userId, betType, betValue, amount, ct);
        }

        if (createdNew)
        {
            _ = Task.Run(() => RunRouletteTimerAsync(guild, round));
        }

        return (true, string.Empty, round);
    }

    public async Task RunRouletteTimerAsync(SocketGuild guild, RouletteTableRound round)
    {
        try
        {
            var delay = round.EndsAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            List<RouletteBetEntry> snapshot;
            lock (round.SyncRoot)
            {
                round.Closed = true;
                snapshot = round.Bets.ToList();
            }

            ((ICollection<KeyValuePair<ulong, RouletteTableRound>>)_rouletteRounds)
                .Remove(new KeyValuePair<ulong, RouletteTableRound>(guild.Id, round));

            var rolledNumber = NextCryptoInt(0, 37);
            var rolledColor = rolledNumber == 0
                ? "green"
                : RedNumbers.Contains(rolledNumber)
                    ? "red"
                    : "black";

            var colorLabel = rolledColor switch
            {
                "red" => "Красное",
                "black" => "Чёрное",
                _ => "Зеро (Зелёное)"
            };

            if (round.DbRoundId > 0)
            {
                await casinoRepo.MarkRouletteRoundSettledAsync(round.DbRoundId, rolledNumber, rolledColor);
            }

            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id);
            var winnersLines = new List<string>();

            foreach (var b in snapshot)
            {
                var mult = EvaluateRouletteMultiplier(b.BetType, b.BetValue, rolledNumber, rolledColor);
                if (mult <= 0)
                    continue;

                var payout = b.Amount * mult;
                await PayoutWinAsync(guild, b.UserId, payout, "roulette_win", $"Выигрыш в рулетке ({rolledNumber} {colorLabel})");
                winnersLines.Add($"• <@{b.UserId}> выиграл **{payout:N0} {cfg.CurrencySymbol}** ({mult}x на `{b.BetValue}`)");
            }

            var channel = guild.GetTextChannel(round.ChannelId);
            if (channel is not null)
            {
                var summary = string.Join(Environment.NewLine,
                    $"Выпало число: **`{rolledNumber}` ({colorLabel})**",
                    string.Empty,
                    winnersLines.Count > 0
                        ? string.Join(Environment.NewLine, winnersLines)
                        : "В этом раунде ни одна ставка не сыграла.");

                await channel.SendMessageAsync(embed: embeds.Info("Рулетка KRLZ — Итоги раунда", summary));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка завершения раунда рулетки в гильдии {GuildId}", guild.Id);
        }
    }

    private static int EvaluateRouletteMultiplier(string betType, string betValue, int rolledNumber, string rolledColor)
    {
        if (betType == "color")
        {
            if (betValue == "green" && rolledNumber == 0) return 14;
            if (betValue == rolledColor) return 2;
            return 0;
        }

        if (betType == "parity" && rolledNumber != 0)
        {
            var isEven = rolledNumber % 2 == 0;
            if (betValue == "even" && isEven) return 2;
            if (betValue == "odd" && !isEven) return 2;
            return 0;
        }

        if (betType == "number" && int.TryParse(betValue, out var targetNum) && targetNum == rolledNumber)
        {
            return 36;
        }

        return 0;
    }

    public async Task<(bool Accepted, string Error, JackpotPoolRound? Round, long TotalPool)> JoinJackpotRoundAsync(
        SocketGuild guild,
        ulong channelId,
        ulong userId,
        long bet,
        CancellationToken ct = default)
    {
        var (allowed, err) = await ValidateAndHoldBetAsync(
            guild.Id, userId, bet, "jackpot_bet", "Взнос в общий пул Джекпота", checkCooldown: false, ct);
        if (!allowed)
            return (false, err, null, 0);

        JackpotPoolRound round;
        var createdNew = false;

        if (!_jackpotRounds.TryGetValue(guild.Id, out var existing))
        {
            var candidate = new JackpotPoolRound
            {
                GuildId = guild.Id,
                ChannelId = channelId,
                EndsAt = DateTimeOffset.UtcNow.AddSeconds(settingsProvider.Current.Casino.JackpotRoundSeconds)
            };

            if (_jackpotRounds.TryAdd(guild.Id, candidate))
            {
                round = candidate;
                createdNew = true;
                round.DbRoundId = await casinoRepo.CreateJackpotRoundRecordAsync(guild.Id, channelId, round.EndsAt, ct);
            }
            else
            {
                round = _jackpotRounds[guild.Id];
            }
        }
        else
        {
            round = existing;
        }

        var lateBet = false;
        long totalPool = 0;

        lock (round.SyncRoot)
        {
            if (round.Closed)
            {
                lateBet = true;
            }
            else
            {
                round.Entries[userId] = round.Entries.GetValueOrDefault(userId) + bet;
                totalPool = round.Entries.Values.Sum();
            }
        }

        if (lateBet)
        {
            await userRepo.TryModifyBalanceAsync(guild.Id, userId, bet, "jackpot_refund", "Возврат опоздавшей ставки в джекпоте", ct);
            return (false, "Текущий раунд Джекпота уже закрылся. Ваша ставка возвращена на баланс.", null, 0);
        }

        if (round.DbRoundId > 0)
        {
            await casinoRepo.AddJackpotBetRecordAsync(round.DbRoundId, guild.Id, userId, bet, ct);
        }

        if (createdNew)
        {
            _ = Task.Run(() => RunJackpotTimerAsync(guild, round));
        }

        return (true, string.Empty, round, totalPool);
    }

    public async Task RunJackpotTimerAsync(SocketGuild guild, JackpotPoolRound round)
    {
        try
        {
            var delay = round.EndsAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            Dictionary<ulong, long> entries;
            lock (round.SyncRoot)
            {
                round.Closed = true;
                entries = new Dictionary<ulong, long>(round.Entries);
            }

            ((ICollection<KeyValuePair<ulong, JackpotPoolRound>>)_jackpotRounds)
                .Remove(new KeyValuePair<ulong, JackpotPoolRound>(guild.Id, round));

            if (entries.Count == 0)
                return;

            var totalPool = entries.Values.Sum();
            var roll = (long)(NextCryptoDouble() * totalPool);
            var winnerId = entries.Keys.First();
            long cumulative = 0;

            foreach (var kvp in entries)
            {
                cumulative += kvp.Value;
                if (roll < cumulative)
                {
                    winnerId = kvp.Key;
                    break;
                }
            }

            if (round.DbRoundId > 0)
            {
                await casinoRepo.MarkJackpotRoundSettledAsync(round.DbRoundId, winnerId);
            }

            var winnerBet = entries[winnerId];
            var chancePct = totalPool > 0 ? winnerBet * 100.0 / totalPool : 100.0;
            await PayoutWinAsync(guild, winnerId, totalPool, "jackpot_win", $"Срыв джекпота ({chancePct:F1}%)");

            var cfg = await settingsRepo.GetOrCreateAsync(guild.Id);
            var channel = guild.GetTextChannel(round.ChannelId);
            if (channel is not null)
            {
                var desc = string.Join(Environment.NewLine,
                    $"Победитель раунда: <@{winnerId}>!",
                    $"• **Выигранный банк:** `{totalPool:N0} {cfg.CurrencySymbol}`",
                    $"• **Взнос победителя:** `{winnerBet:N0} {cfg.CurrencySymbol}` (шанс: **{chancePct:F1}%**)",
                    $"• **Всего участников:** {entries.Count}");

                await channel.SendMessageAsync(embed: embeds.Success("Джекпот KRLZ разыгран!", desc));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка завершения раунда джекпота в гильдии {GuildId}", guild.Id);
        }
    }
}
