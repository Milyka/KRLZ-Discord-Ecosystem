using System.Collections.Concurrent;
using System.Globalization;
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
public sealed class PvpAndMinesModule(
    SocialExpansionRepository socialExpRepo,
    UserRepository userRepo,
    GuildSettingsRepository settingsRepo,
    ProfileCardService profileCardService,
    AchievementService achievementService,
    SettingsProvider settingsProvider,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> DuelLocks = new();
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> MinesLocks = new();

    [SlashCommand("duel", "Вызвать участника на пошаговую PvP-дуэль с графической ареной")]
    public async Task DuelCommandAsync(
        [Summary("opponent", "Соперник для дуэли")] SocketGuildUser opponent,
        [Summary("bet", "Ставка на дуэль (0 для боя без ставки)")] [MinValue(0)] long bet = 0)
    {
        if (opponent.IsBot || opponent.Id == Context.User.Id)
        {
            await RespondAsync(embed: embeds.Error("Выберите другого реального участника сервера для вызова на дуэль."), ephemeral: true);
            return;
        }

        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var cCfg = settingsProvider.Current.Casino;
        if (bet > 0 && (bet < cCfg.MinBet || bet > cCfg.MaxBet))
        {
            await FollowupAsync(embed: embeds.Error($"Ставка должна быть от **{cCfg.MinBet:N0}** до **{cCfg.MaxBet:N0} {cfg.CurrencySymbol}** (или `0`)."), ephemeral: true);
            return;
        }

        var p1 = await userRepo.GetOrCreateAsync(Context.Guild.Id, Context.User.Id);
        var p2 = await userRepo.GetOrCreateAsync(Context.Guild.Id, opponent.Id);
        if (bet > 0 && (p1.Balance < bet || p2.Balance < bet))
        {
            await FollowupAsync(embed: embeds.Error("У одного из участников недостаточно средств на балансе для указанной ставки."), ephemeral: true);
            return;
        }

        var duelId = await socialExpRepo.CreateDuelAsync(
            Context.Guild.Id,
            Context.Channel.Id,
            Context.User.Id,
            opponent.Id,
            bet,
            DateTimeOffset.UtcNow.AddMinutes(3));

        var comp = new ComponentBuilder()
            .WithButton("Принять вызов", $"duel:accept:{duelId}", ButtonStyle.Success, new Emoji("⚔️"))
            .WithButton("Отклонить", $"duel:decline:{duelId}", ButtonStyle.Danger, new Emoji("🏳️"))
            .Build();

        var betLabel = bet > 0 ? $"**{bet:N0} {cfg.CurrencySymbol}** (Банк: **{bet * 2:N0} {cfg.CurrencySymbol}**)" : "Товарищеский бой (без ставки)";
        var embed = embeds.Info($"Вызов на PvP-дуэль #{duelId}",
            $"<@{Context.User.Id}> вызывает <@{opponent.Id}> на арену KRLZ!\n**Ставка:** {betLabel}\n\nУ <@{opponent.Id}> есть 3 минуты, чтобы принять бой.");

        var msg = await FollowupAsync(text: $"<@{opponent.Id}>", embed: embed, components: comp);
        await socialExpRepo.UpdateDuelMessageIdAsync(duelId, msg.Id);
    }

    [ComponentInteraction("duel:accept:*", true)]
    public async Task HandleDuelAcceptAsync(string duelIdStr)
    {
        if (!long.TryParse(duelIdStr, out var duelId))
            return;

        await DeferAsync();
        var duel = await socialExpRepo.GetDuelByIdAsync(duelId);
        if (duel is null || duel.Status != "pending")
        {
            await FollowupAsync(embed: embeds.Error("Этот вызов на дуэль уже неактуален."), ephemeral: true);
            return;
        }

        if (Context.User.Id != duel.OpponentId)
        {
            await FollowupAsync(embed: embeds.Error("Принять этот вызов может только приглашённый соперник."), ephemeral: true);
            return;
        }

        var (ok, err) = await socialExpRepo.TryAcceptDuelAsync(duelId);
        if (!ok)
        {
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Error(err);
                m.Components = null;
            });
            return;
        }

        duel = (await socialExpRepo.GetDuelByIdAsync(duelId))!;
        var (png, embed, comp) = await BuildDuelVisualAsync(duel);
        await using var stream = new MemoryStream(png);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"Ход за <@{duel.TurnUserId}>!";
            m.Embed = embed;
            m.Attachments = new List<FileAttachment> { new(stream, "duel-arena.png") };
            m.Components = comp;
        });
    }

    [ComponentInteraction("duel:decline:*", true)]
    public async Task HandleDuelDeclineAsync(string duelIdStr)
    {
        if (!long.TryParse(duelIdStr, out var duelId))
            return;

        await DeferAsync();
        var duel = await socialExpRepo.GetDuelByIdAsync(duelId);
        if (duel is null || duel.Status != "pending")
            return;

        if (Context.User.Id != duel.OpponentId && Context.User.Id != duel.ChallengerId)
        {
            await FollowupAsync(embed: embeds.Error("Отменить вызов могут только участники дуэли."), ephemeral: true);
            return;
        }

        await socialExpRepo.CancelPendingDuelAsync(duelId);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = string.Empty;
            m.Embed = embeds.Warning("Дуэль отменена", $"Вызов на дуэль #{duelId} был отклонён.");
            m.Components = null;
        });
    }

    [ComponentInteraction("duel:move:*:*", true)]
    public async Task HandleDuelMoveAsync(string duelIdStr, string moveType)
    {
        if (!long.TryParse(duelIdStr, out var duelId))
            return;

        await DeferAsync();
        var sem = DuelLocks.GetOrAdd(duelId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        DuelSessionEntity? duel = null;
        try
        {
            duel = await socialExpRepo.GetDuelByIdAsync(duelId);
            if (duel is null || duel.Status != "active")
            {
                await FollowupAsync(embed: embeds.Error("Эта дуэль уже завершена."), ephemeral: true);
                return;
            }

            if (Context.User.Id != duel.TurnUserId)
            {
                await FollowupAsync(embed: embeds.Error("Сейчас ход вашего соперника!"), ephemeral: true);
                return;
            }

            var isChallenger = Context.User.Id == duel.ChallengerId;
            var attackerName = (Context.User as SocketGuildUser)?.DisplayName ?? Context.User.Username;
            var myHp = isChallenger ? duel.ChallengerHp : duel.OpponentHp;
            var enemyHp = isChallenger ? duel.OpponentHp : duel.ChallengerHp;

            string actionLog;
            switch (moveType)
            {
                case "head":
                {
                    var roll = CasinoService.NextCryptoInt(1, 101);
                    if (roll <= 25)
                    {
                        actionLog = $"{attackerName} промахнулся ударом в голову!";
                    }
                    else
                    {
                        var dmg = CasinoService.NextCryptoInt(22, 39);
                        enemyHp = Math.Max(0, enemyHp - dmg);
                        actionLog = $"{attackerName} нанёс сокрушительный удар в голову: -{dmg} HP!";
                    }
                    break;
                }
                case "lunge":
                {
                    var dmg = CasinoService.NextCryptoInt(15, 24);
                    var heal = Math.Max(3, dmg / 3);
                    enemyHp = Math.Max(0, enemyHp - dmg);
                    myHp = Math.Min(100, myHp + heal);
                    actionLog = $"{attackerName} провёл вампирический выпад: -{dmg} HP врагу и +{heal} HP себе!";
                    break;
                }
                case "heal":
                {
                    var heal = CasinoService.NextCryptoInt(14, 23);
                    var chip = CasinoService.NextCryptoInt(6, 12);
                    myHp = Math.Min(100, myHp + heal);
                    enemyHp = Math.Max(0, enemyHp - chip);
                    actionLog = $"{attackerName} перевязал раны (+{heal} HP) и контратаковал (-{chip} HP)!";
                    break;
                }
                default:
                {
                    var dmg = CasinoService.NextCryptoInt(18, 28);
                    enemyHp = Math.Max(0, enemyHp - dmg);
                    actionLog = $"{attackerName} пробил точный удар в корпус: -{dmg} HP!";
                    break;
                }
            }

            var nextChallengerHp = isChallenger ? myHp : enemyHp;
            var nextOpponentHp = isChallenger ? enemyHp : myHp;
            var nextTurn = isChallenger ? duel.OpponentId : duel.ChallengerId;

            if (enemyHp <= 0)
            {
                var winnerId = Context.User.Id;
                var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
                var winLog = $"Победа {attackerName}! {actionLog}";
                var finished = await socialExpRepo.TryFinishDuelAsync(duel.Id, winnerId, nextChallengerHp, nextOpponentHp, winLog);
                if (!finished)
                    return;

                duel = (await socialExpRepo.GetDuelByIdAsync(duel.Id))!;
                var (finalPng, finalEmbed, _) = await BuildDuelVisualAsync(duel);
                await using var finalStream = new MemoryStream(finalPng);
                var prizeStr = duel.Bet > 0 ? $" и забирает банк **{duel.Bet * 2:N0} {cfg.CurrencySymbol}**" : string.Empty;

                await ModifyOriginalResponseAsync(m =>
                {
                    m.Content = $"🏆 Победитель дуэли — <@{winnerId}>{prizeStr}!";
                    m.Embed = finalEmbed;
                    m.Attachments = new List<FileAttachment> { new(finalStream, "duel-arena.png") };
                    m.Components = null;
                });

                await achievementService.TrackEventAsync(Context.Guild, winnerId, "casino_wins", 1, isAbsoluteValue: false, Context.Channel as SocketTextChannel);
                return;
            }

            await socialExpRepo.UpdateDuelStateAsync(
                duel.Id,
                nextChallengerHp,
                nextOpponentHp,
                nextTurn,
                duel.RoundNum + 1,
                actionLog);

            duel = (await socialExpRepo.GetDuelByIdAsync(duel.Id))!;
            var (png, embed, comp) = await BuildDuelVisualAsync(duel);
            await using var stream = new MemoryStream(png);

            await ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"Ход за <@{duel.TurnUserId}>!";
                m.Embed = embed;
                m.Attachments = new List<FileAttachment> { new(stream, "duel-arena.png") };
                m.Components = comp;
            });
        }
        finally
        {
            sem.Release();
            if (duel?.Status != "active")
            {
                DuelLocks.TryRemove(duelId, out _);
            }
        }
    }

    private async Task<(byte[] Png, Embed Embed, MessageComponent Components)> BuildDuelVisualAsync(DuelSessionEntity duel)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var u1 = await Context.Guild.GetGuildUserSafeAsync(duel.ChallengerId);
        var u2 = await Context.Guild.GetGuildUserSafeAsync(duel.OpponentId);

        var p1Name = u1?.DisplayName ?? $"ID {duel.ChallengerId}";
        var p1Av = u1?.GetAvatarUrl(ImageFormat.Png, 128) ?? u1?.GetDefaultAvatarUrl();
        var p2Name = u2?.DisplayName ?? $"ID {duel.OpponentId}";
        var p2Av = u2?.GetAvatarUrl(ImageFormat.Png, 128) ?? u2?.GetDefaultAvatarUrl();

        var png = await profileCardService.RenderDuelCardAsync(
            duel,
            p1Name,
            p1Av,
            p2Name,
            p2Av,
            cfg.CurrencySymbol);

        var embed = new EmbedBuilder()
            .WithColor(new Color(244, 63, 94))
            .WithImageUrl("attachment://duel-arena.png")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("В голову (22-38, риск)", $"duel:move:{duel.Id}:head", ButtonStyle.Danger, new Emoji("🗡️"))
            .WithButton("В корпус (18-27)", $"duel:move:{duel.Id}:body", ButtonStyle.Primary, new Emoji("⚔️"))
            .WithButton("Выпад + Вампиризм", $"duel:move:{duel.Id}:lunge", ButtonStyle.Secondary, new Emoji("⚡"))
            .WithButton("Перевязка (+HP)", $"duel:move:{duel.Id}:heal", ButtonStyle.Success, new Emoji("💊"))
            .Build();

        return (png, embed, comp);
    }

    [SlashCommand("mines", "Сыграть в казино «Сапёр» на 20 клетках с растущим множителем")]
    public async Task MinesCommandAsync(
        [Summary("bet", "Сумма ставки")] [MinValue(1)] long bet,
        [Summary("bombs", "Количество бомб на поле (от 1 до 15)")] [MinValue(1), MaxValue(15)] int bombs = 3)
    {
        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var cCfg = settingsProvider.Current.Casino;
        if (bet < cCfg.MinBet || bet > cCfg.MaxBet)
        {
            await FollowupAsync(embed: embeds.Error($"Ставка должна быть в пределах от **{cCfg.MinBet:N0}** до **{cCfg.MaxBet:N0} {cfg.CurrencySymbol}**."), ephemeral: true);
            return;
        }

        var existing = await socialExpRepo.GetActiveMinesSessionAsync(Context.Guild.Id, Context.User.Id);
        if (existing is not null)
        {
            var resetComp = new ComponentBuilder()
                .WithButton("Сбросить зависшую игру и вернуть ставку", $"mines:reset:{existing.Id}", ButtonStyle.Danger)
                .Build();

            await FollowupAsync(
                embed: embeds.Warning(
                    "Активная партия в Сапёр",
                    $"У вас уже есть незавершённая партия в `/mines` на сумму **{existing.Bet:N0} {cfg.CurrencySymbol}**.\n\n" +
                    "Если сообщение с игровым полем потерялось или бот перезапускался во время ставки, нажмите кнопку ниже, чтобы вернуть ставку на баланс и начать заново."),
                components: resetComp,
                ephemeral: true);
            return;
        }

        var pool = Enumerable.Range(0, 20).ToList();
        var bombPositions = new HashSet<int>();
        while (bombPositions.Count < bombs)
        {
            var idx = CasinoService.NextCryptoInt(0, pool.Count);
            bombPositions.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        var (ok, err, session) = await socialExpRepo.TryCreateMinesSessionAsync(
            Context.Guild.Id,
            Context.User.Id,
            Context.Channel.Id,
            bet,
            bombs,
            JsonSerializer.Serialize(bombPositions));

        if (!ok || session is null)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var (embed, comp) = BuildMinesBoard(session, bombPositions, [], cfg.CurrencySymbol, revealAll: false);
        try
        {
            var msg = await FollowupAsync(embed: embed, components: comp);
            await socialExpRepo.UpdateMinesMessageIdAsync(session.Id, msg.Id);
        }
        catch
        {
            await socialExpRepo.TryCancelAndRefundMinesSessionAsync(session.Id, Context.User.Id);
            MinesLocks.TryRemove(session.Id, out _);
            throw;
        }
    }

    [ComponentInteraction("mines:reset:*", true)]
    public async Task HandleMinesResetClickAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        await DeferAsync(ephemeral: true);
        var (ok, refundAmount, err) = await socialExpRepo.TryCancelAndRefundMinesSessionAsync(sessionId, Context.User.Id);
        MinesLocks.TryRemove(sessionId, out _);
        if (!ok)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        await FollowupAsync(
            embed: embeds.Success(
                "Партия сброшена",
                $"Зависшая партия отменена! Ставка **{refundAmount:N0} {cfg.CurrencySymbol}** возвращена на ваш баланс. Теперь вы можете запустить `/mines` заново."),
            ephemeral: true);
    }

    [ComponentInteraction("mines:tile:*:*", true)]
    public async Task HandleMinesTileClickAsync(string sessionIdStr, string tileIdxStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || !int.TryParse(tileIdxStr, out var tileIdx))
            return;

        await DeferAsync();
        var sem = MinesLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        MinesSessionEntity? session = null;
        try
        {
            session = await socialExpRepo.GetMinesSessionByIdAsync(sessionId);
            if (session is null || session.Status != "playing")
            {
                await FollowupAsync(embed: embeds.Error("Эта игра в Сапёр уже завершена."), ephemeral: true);
                return;
            }

            if (Context.User.Id != session.UserId)
            {
                await FollowupAsync(embed: embeds.Error("Это игровое поле принадлежит другому участнику."), ephemeral: true);
                return;
            }

            var bombs = JsonSerializer.Deserialize<HashSet<int>>(session.BombsJson) ?? [];
            var revealed = JsonSerializer.Deserialize<HashSet<int>>(session.RevealedJson) ?? [];
            if (revealed.Contains(tileIdx))
            {
                return;
            }

            revealed.Add(tileIdx);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var revealedJson = JsonSerializer.Serialize(revealed);

            if (bombs.Contains(tileIdx))
            {
                var finished = await socialExpRepo.TryFinishMinesSessionAsync(session.Id, "lost", revealedJson, 0);
                if (!finished)
                    return;

                session = session with { Status = "lost", RevealedJson = revealedJson };
                var (lostEmbed, lostComp) = BuildMinesBoard(session, bombs, revealed, cfg.CurrencySymbol, revealAll: true);
                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = lostEmbed;
                    m.Components = lostComp;
                });
                return;
            }

            var safeTotal = 20 - session.BombCount;
            if (revealed.Count >= safeTotal)
            {
                var mult = CalculateMinesMultiplier(session.BombCount, revealed.Count);
                var payout = (long)Math.Round(session.Bet * mult);
                var finished = await socialExpRepo.TryFinishMinesSessionAsync(session.Id, "won", revealedJson, payout);
                if (!finished)
                    return;

                session = session with { Status = "won", RevealedJson = revealedJson };
                var (winEmbed, winComp) = BuildMinesBoard(session, bombs, revealed, cfg.CurrencySymbol, revealAll: true);
                await ModifyOriginalResponseAsync(m =>
                {
                    m.Embed = winEmbed;
                    m.Components = winComp;
                });
                await achievementService.TrackEventAsync(Context.Guild, session.UserId, "casino_wins", 1, isAbsoluteValue: false, Context.Channel as SocketTextChannel);
                return;
            }

            await socialExpRepo.UpdateMinesRevealedAsync(session.Id, revealedJson);
            session = session with { RevealedJson = revealedJson };
            var (embed, comp) = BuildMinesBoard(session, bombs, revealed, cfg.CurrencySymbol, revealAll: false);
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Components = comp;
            });
        }
        finally
        {
            sem.Release();
            if (session?.Status != "playing")
            {
                MinesLocks.TryRemove(sessionId, out _);
            }
        }
    }

    [ComponentInteraction("mines:cashout:*", true)]
    public async Task HandleMinesCashoutAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        await DeferAsync();
        var sem = MinesLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        MinesSessionEntity? session = null;
        try
        {
            session = await socialExpRepo.GetMinesSessionByIdAsync(sessionId);
            if (session is null || session.Status != "playing")
            {
                return;
            }

            if (Context.User.Id != session.UserId)
            {
                await FollowupAsync(embed: embeds.Error("Забрать выигрыш может только владелец игры."), ephemeral: true);
                return;
            }

            var bombs = JsonSerializer.Deserialize<HashSet<int>>(session.BombsJson) ?? [];
            var revealed = JsonSerializer.Deserialize<HashSet<int>>(session.RevealedJson) ?? [];
            if (revealed.Count == 0)
            {
                await FollowupAsync(embed: embeds.Error("Откройте хотя бы одну безопасную клетку перед выводом выигрыша!"), ephemeral: true);
                return;
            }

            var mult = CalculateMinesMultiplier(session.BombCount, revealed.Count);
            var payout = (long)Math.Round(session.Bet * mult);
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);

            var finished = await socialExpRepo.TryFinishMinesSessionAsync(session.Id, "won", session.RevealedJson, payout);
            if (!finished)
                return;

            session = session with { Status = "won" };
            var (winEmbed, winComp) = BuildMinesBoard(session, bombs, revealed, cfg.CurrencySymbol, revealAll: true);
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = winEmbed;
                m.Components = winComp;
            });

            await achievementService.TrackEventAsync(Context.Guild, session.UserId, "casino_wins", 1, isAbsoluteValue: false, Context.Channel as SocketTextChannel);
        }
        finally
        {
            sem.Release();
            if (session?.Status != "playing")
            {
                MinesLocks.TryRemove(sessionId, out _);
            }
        }
    }

    public static double CalculateMinesMultiplier(int bombs, int revealedSafeCount)
    {
        if (revealedSafeCount <= 0)
            return 1.0;

        var prob = 1.0;
        for (var i = 0; i < revealedSafeCount; i++)
        {
            prob *= (double)(20 - bombs - i) / (20 - i);
        }

        var raw = 0.96 / Math.Max(0.0001, prob);
        return Math.Round(Math.Max(1.05, raw), 2);
    }

    private (Embed Embed, MessageComponent Components) BuildMinesBoard(
        MinesSessionEntity session,
        HashSet<int> bombs,
        HashSet<int> revealed,
        string currencySymbol,
        bool revealAll)
    {
        var safeOpened = revealed.Count(idx => !bombs.Contains(idx));
        var currentMult = CalculateMinesMultiplier(session.BombCount, safeOpened);
        var currentPayout = (long)Math.Round(session.Bet * currentMult);

        var statusLine = session.Status switch
        {
            "won" => $"🎉 **ПОБЕДА!** Вы забрали **{currentPayout:N0} {currencySymbol}** (`x{currentMult.ToString("F2", CultureInfo.InvariantCulture)}`)!",
            "lost" => $"💥 **ВЗРЫВ!** Вы наткнулись на мину и потеряли ставку **{session.Bet:N0} {currencySymbol}**.",
            _ => $"Откройте клетки с кристаллами 💎 и заберите банк до взрыва!"
        };

        var desc = string.Join(Environment.NewLine,
            $"**Игрок:** <@{session.UserId}>",
            $"**Ставка:** `{session.Bet:N0} {currencySymbol}`  •  **Бомб:** `{session.BombCount}`",
            $"**Множитель:** `x{currentMult.ToString("F2", CultureInfo.InvariantCulture)}`  •  **Текущий куш:** `{currentPayout:N0} {currencySymbol}`",
            string.Empty,
            statusLine);

        var embed = session.Status switch
        {
            "won" => embeds.Success($"Сапёр KRLZ #{session.Id} — Выигрыш!", desc),
            "lost" => embeds.Error(desc, $"💥 Сапёр KRLZ #{session.Id} — Поражение"),
            _ => embeds.Info($"Сапёр KRLZ #{session.Id}", desc)
        };

        var cb = new ComponentBuilder();
        for (var i = 0; i < 20; i++)
        {
            var row = i / 5;
            var isRevealed = revealed.Contains(i);
            var isBomb = bombs.Contains(i);

            string label = "\u200B";
            ButtonStyle style = ButtonStyle.Secondary;
            IEmote? emote = null;
            var disabled = revealAll || isRevealed || session.Status != "playing";

            if (isRevealed && isBomb)
            {
                style = ButtonStyle.Danger;
                emote = new Emoji("💥");
            }
            else if (isRevealed)
            {
                style = ButtonStyle.Success;
                emote = new Emoji("💎");
            }
            else if (revealAll && isBomb)
            {
                style = ButtonStyle.Secondary;
                emote = new Emoji("💣");
            }
            else
            {
                label = $"{i + 1}";
            }

            cb.WithButton(
                label: emote is null ? label : null,
                customId: $"mines:tile:{session.Id}:{i}",
                style: style,
                emote: emote,
                disabled: disabled,
                row: row);
        }

        cb.WithButton(
            label: $"Забрать {currentPayout:N0} {currencySymbol} (x{currentMult.ToString("F2", CultureInfo.InvariantCulture)})",
            customId: $"mines:cashout:{session.Id}",
            style: ButtonStyle.Success,
            emote: new Emoji("💰"),
            disabled: session.Status != "playing" || safeOpened == 0,
            row: 4);

        return (embed, cb.Build());
    }
}
