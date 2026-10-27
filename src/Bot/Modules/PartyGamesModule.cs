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

public sealed class MafiaSecretNoteModal : IModal
{
    public string Title => "Тайная записка синдикату Мафии";

    [InputLabel("Текст сообщения для напарников")]
    [ModalTextInput("note", TextInputStyle.Paragraph, "Например: Сегодня ночью убираем #3, днём топим #5...", minLength: 2, maxLength: 300)]
    public string Note { get; set; } = string.Empty;
}

public sealed class QuizAnswerModal : IModal
{
    public string Title => "Ответ на вопрос Своей Игры";

    [InputLabel("Ваш ответ")]
    [ModalTextInput("answer", TextInputStyle.Short, "Введите точный ответ...", minLength: 1, maxLength: 120)]
    public string Answer { get; set; } = string.Empty;
}

public sealed class CodenamesClueModal : IModal
{
    public string Title => "Шифровка Капитана Кодовых Имён";

    [InputLabel("Слово-ассоциация (одно слово)")]
    [ModalTextInput("clue_word", TextInputStyle.Short, "Например: КОСМОС", minLength: 2, maxLength: 40)]
    public string ClueWord { get; set; } = string.Empty;

    [InputLabel("Количество загаданных слов (1-5)")]
    [ModalTextInput("clue_count", TextInputStyle.Short, "2", minLength: 1, maxLength: 2)]
    public string ClueCount { get; set; } = "1";
}

[RequireContext(ContextType.Guild)]
public sealed class PartyGamesModule(
    PartyGameRepository partyRepo,
    PartyGameService partyService,
    GuildSettingsRepository settingsRepo,
    EmbedFactory embeds) : InteractionModuleBase<SocketInteractionContext>
{
    // =========================================================================
    // SLASH COMMANDS: CREATE LOBBIES & RAID BOSS
    // =========================================================================

    [SlashCommand("party-help", "Справочник по всем компанейским и настольным играм KRLZ")]
    public async Task PartyHelpCommandAsync()
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var sb = new StringBuilder();
        sb.AppendLine("Все компанейские игры работают **без личных сообщений (ЛС)** — секретные роли, карты и ночные ходы открываются персонально вам по кнопкам прямо в канале игры и отрисовываются на графических столах **SkiaSharp**!");
        sb.AppendLine();
        sb.AppendLine("• `/mafia [bet] [voice_sync]` — **Мафия KRLZ** (4–12 чел.): Дон, Мафия, Комиссар, Доктор, Любовница, Маньяк, Мирные. Авто-мут в голосовом канале ночью и для погибших.");
        sb.AppendLine("• `/bunker [bet]` — **Бункер: Катастрофа** (3–10 чел.): генерация апокалипсиса, 6 характеристик досье, спецкарты, пошаговое вскрытие и симуляция выживания колонии.");
        sb.AppendLine("• `/spyfall [bet]` — **Шпион** (3–10 чел.): 16 секретных локаций сервера, допрос участников, голосование и досрочное угадывание локации шпионом.");
        sb.AppendLine("• `/liarsbar [bet]` — **Бар Лжецов** (2–4 чел.): карточный блеф Дам/Королей/Тузов с проверкой «Лжец!» и русской рулеткой `1/6..6/6`.");
        sb.AppendLine("• `/quiz [bet]` — **Своя Игра / Брейн-Ринг** (2–8 чел.): табло `4×5` за 100–500 очков с реактивной кнопкой ответа.");
        sb.AppendLine("• `/codenames [bet]` — **Кодовые Имена** (4–10 чел.): поле `5×5` из 25 слов, секретная карта капитанов и командный поиск агентов.");
        sb.AppendLine($"• `/raid [level]` — **Мировой Рейд-Босс**: кооперативное сражение для всего сервера с наградами в **{cfg.CurrencySymbol}** и **💎 кристаллах**.");

        await RespondAsync(embed: embeds.Info("🎲 Настольные и Компанейские Игры KRLZ", sb.ToString()));
    }

    [SlashCommand("mafia", "Создать стол для игры в Мафию со скрытыми ночными ходами и графическим столом")]
    public async Task MafiaCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0,
        [Summary("voice_sync", "Синхронизировать ночной авто-мут с вашим голосовым каналом")] bool voiceSync = true)
    {
        await CreateLobbyInternalAsync("mafia", bet, voiceSync);
    }

    [SlashCommand("bunker", "Создать бункер на выживание с генерацией катастрофы, досье и спецкарт")]
    public async Task BunkerCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0)
    {
        await CreateLobbyInternalAsync("bunker", bet, voiceSync: false);
    }

    [SlashCommand("spyfall", "Запустить партию в «Шпиона» с сеткой локаций KRLZ")]
    public async Task SpyfallCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0)
    {
        await CreateLobbyInternalAsync("spyfall", bet, voiceSync: false);
    }

    [SlashCommand("liarsbar", "Создать стол «Бар Лжецов» (блеф на картах + револьвер)")]
    public async Task LiarsBarCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0)
    {
        await CreateLobbyInternalAsync("liarsbar", bet, voiceSync: false);
    }

    [SlashCommand("quiz", "Запустить интеллектуальное табло «Своя Игра: Брейн-Ринг»")]
    public async Task QuizCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0)
    {
        await CreateLobbyInternalAsync("quiz", bet, voiceSync: false);
    }

    [SlashCommand("codenames", "Создать командный матч в «Кодовые Имена» (поле 5x5)")]
    public async Task CodenamesCommandAsync(
        [Summary("bet", "Вступительный взнос в банк игры (0 — бесплатный стол)")] [MinValue(0)] long bet = 0)
    {
        await CreateLobbyInternalAsync("codenames", bet, voiceSync: false);
    }

    [SlashCommand("raid", "Призвать или показать активного Мирового Рейд-Босса сервера")]
    public async Task RaidBossCommandAsync(
        [Summary("level", "Уровень сложности босса (1–10)")] [MinValue(1), MaxValue(10)] int level = 3,
        [Summary("boss_name", "Особое имя босса (необязательно)")] string? bossName = null)
    {
        await DeferAsync();
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var active = await partyRepo.GetActiveRaidBossAsync(Context.Guild.Id);

        if (active is null)
        {
            var names = new[]
            {
                "Кибер-Дракон Пустоты KRLZ",
                "Архонт Хаоса • Левиафан",
                "Механический Титан Разлома",
                "Хранитель Золотой Бездны"
            };
            var chosenName = string.IsNullOrWhiteSpace(bossName)
                ? names[PartyGameService.NextInt(0, names.Length)]
                : bossName.Trim();

            var maxHp = level * 4500L;
            var rewardCoins = level * 6000L;
            var rewardCrystals = level * 12;

            active = await partyRepo.CreateRaidBossAsync(
                Context.Guild.Id,
                Context.Channel.Id,
                $"boss_lvl_{level}",
                chosenName,
                level,
                maxHp,
                rewardCoins,
                rewardCrystals,
                TimeSpan.FromHours(2));
        }

        var hits = await partyRepo.GetRaidHitsAsync(active.Id);
        var png = await partyService.RenderRaidBossArenaAsync(active, hits, cfg.CurrencySymbol);
        await using var stream = new MemoryStream(png);

        var embed = new EmbedBuilder()
            .WithColor(new Color(244, 63, 94))
            .WithTitle($"🐉 Мировой Рейд-Босс: {active.BossName} (Ур. {active.BossLevel})")
            .WithDescription($"Нажимайте кнопки выбора класса ниже, чтобы атаковать босса! Урон усиливается от вашего уровня, активного питомца (`/pet`) и предметов инвентаря.\n**Пул наград:** `{active.RewardPoolCoins:N0} {cfg.CurrencySymbol}` + `{active.RewardCrystals} 💎`")
            .WithImageUrl("attachment://raid-arena.png")
            .Build();

        var comp = BuildRaidComponents(active.Id, active.Status == "active");
        var msg = await FollowupWithFileAsync(stream, "raid-arena.png", embed: embed, components: comp);
        await partyRepo.UpdateRaidBossMessageIdAsync(active.Id, msg.Id);
    }

    private static MessageComponent? BuildRaidComponents(long raidId, bool isActive)
    {
        if (!isActive)
            return null;

        return new ComponentBuilder()
            .WithButton("Атака Воина", $"raid:hit:{raidId}:warrior", ButtonStyle.Primary, new Emoji("⚔️"))
            .WithButton("Залп Мага", $"raid:hit:{raidId}:mage", ButtonStyle.Secondary, new Emoji("🔮"))
            .WithButton("Удар Ассасина (Крит)", $"raid:hit:{raidId}:assassin", ButtonStyle.Danger, new Emoji("🗡️"))
            .Build();
    }

    [ComponentInteraction("raid:hit:*:*", true)]
    public async Task HandleRaidHitAsync(string raidIdStr, string heroClass)
    {
        if (!long.TryParse(raidIdStr, out var raidId))
            return;

        await DeferAsync();
        var member = Context.User as SocketGuildUser;
        var displayName = member?.DisplayName ?? Context.User.Username;

        var (ok, err, defeated, _, _, rewards) = await partyService.ExecuteRaidAttackAsync(
            Context.Guild,
            raidId,
            Context.Guild.Id,
            Context.User.Id,
            displayName,
            heroClass,
            Context.Channel);

        if (!ok)
        {
            await FollowupAsync(embed: embeds.Warning("Перезарядка атаки", err), ephemeral: true);
            return;
        }
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var boss = (await partyRepo.GetRaidBossByIdAsync(raidId))!;
        var hits = await partyRepo.GetRaidHitsAsync(raidId);
        var png = await partyService.RenderRaidBossArenaAsync(boss, hits, cfg.CurrencySymbol);
        await using var stream = new MemoryStream(png);

        var desc = defeated
            ? $"🏆 **БОСС ПОВЕРЖЕН!** Герои сервера разделили `{boss.RewardPoolCoins:N0} {cfg.CurrencySymbol}` и `{boss.RewardCrystals} 💎` пропорционально нанесённому урону!\n" +
              string.Join("\n", rewards.Take(8).Select(kv => $"• <@{kv.Key}> получил **+{kv.Value.Coins:N0} {cfg.CurrencySymbol}** и **+{kv.Value.Crystals} 💎**"))
            : $"Бой продолжается! Осталось **{boss.CurrentHp:N0} / {boss.MaxHp:N0} HP**.\nПоследний удар: *{boss.LastLog}*";

        var embed = new EmbedBuilder()
            .WithColor(defeated ? new Color(16, 185, 129) : new Color(244, 63, 94))
            .WithTitle($"🐉 Мировой Рейд-Босс: {boss.BossName} (Ур. {boss.BossLevel})")
            .WithDescription(desc)
            .WithImageUrl("attachment://raid-arena.png")
            .Build();

        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Attachments = new List<FileAttachment> { new(stream, "raid-arena.png") };
            m.Components = BuildRaidComponents(raidId, !defeated);
        });
    }

    // =========================================================================
    // SHARED LOBBY SYSTEM (CREATE / JOIN / LEAVE / CANCEL / START)
    // =========================================================================

    private async Task CreateLobbyInternalAsync(string gameType, long bet, bool voiceSync)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        var member = Context.User as SocketGuildUser;
        var displayName = member?.DisplayName ?? Context.User.Username;
        var voiceChannelId = voiceSync && member?.VoiceChannel is not null ? member.VoiceChannel.Id : 0UL;

        await DeferAsync();

        var (ok, err, session) = await partyRepo.CreateLobbySessionAsync(
            Context.Guild.Id,
            Context.Channel.Id,
            voiceChannelId,
            Context.User.Id,
            displayName,
            gameType,
            bet,
            "{}",
            TimeSpan.FromMinutes(25));

        if (!ok || session is null)
        {
            await FollowupAsync(embed: embeds.Error(err), ephemeral: true);
            return;
        }

        var players = await partyRepo.GetPlayersAsync(session.Id);
        var (png, embed, comp) = await BuildLobbyVisualAsync(session, players, cfg.CurrencySymbol);
        await using var stream = new MemoryStream(png);

        var msg = await FollowupWithFileAsync(stream, "party-lobby.png", embed: embed, components: comp);
        await partyRepo.UpdateSessionMessageIdAsync(session.Id, msg.Id);
    }

    private async Task<(byte[] Png, Embed Embed, MessageComponent Components)> BuildLobbyVisualAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var (minP, maxP, title, subtitle) = PartyGameService.GetGameMeta(session.GameType);
        var png = await partyService.RenderLobbyBannerAsync(session, players, currencySymbol);

        var voiceNote = session.VoiceChannelId != 0
            ? $"\n🔊 **Голосовая синхронизация:** <#{session.VoiceChannelId}> (авто-мут ночью)"
            : string.Empty;

        var embed = new EmbedBuilder()
            .WithColor(new Color(56, 189, 248))
            .WithTitle($"🎲 {title} — Набор игроков (#{session.Id})")
            .WithDescription($"{subtitle}\n**Организатор:** <@{session.HostId}> • **Игроков:** `{players.Count}/{maxP}` (минимум `{minP}`)\n**Вступительный взнос:** `{session.Bet:N0} {currencySymbol}` • **Текущий банк:** `{session.PrizePool:N0} {currencySymbol}`{voiceNote}")
            .WithImageUrl("attachment://party-lobby.png")
            .Build();

        var comp = new ComponentBuilder()
            .WithButton("Войти в игру", $"pg:join:{session.Id}", ButtonStyle.Success, new Emoji("✅"))
            .WithButton("Выйти", $"pg:leave:{session.Id}", ButtonStyle.Secondary, new Emoji("🚪"))
            .WithButton($"Начать ({players.Count}/{minP}+)", $"pg:start:{session.Id}", ButtonStyle.Primary, new Emoji("🚀"))
            .WithButton("Отменить стол", $"pg:cancel:{session.Id}", ButtonStyle.Danger, new Emoji("✖️"))
            .Build();

        return (png, embed, comp);
    }

    [ComponentInteraction("pg:join:*", true)]
    public async Task HandleLobbyJoinAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "lobby")
            {
                await RespondAsync(embed: embeds.Error("Это лобби уже запущено или закрыто."), ephemeral: true);
                return;
            }

            var (_, maxP, _, _) = PartyGameService.GetGameMeta(session.GameType);
            var displayName = (Context.User as SocketGuildUser)?.DisplayName ?? Context.User.Username;
            var (ok, err) = await partyRepo.TryJoinLobbyAsync(sessionId, Context.User.Id, displayName, maxP);
            if (!ok)
            {
                await RespondAsync(embed: embeds.Error(err), ephemeral: true);
                return;
            }

            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            session = (await partyRepo.GetSessionByIdAsync(sessionId))!;
            var players = await partyRepo.GetPlayersAsync(sessionId);
            var (png, embed, comp) = await BuildLobbyVisualAsync(session, players, cfg.CurrencySymbol);
            await using var stream = new MemoryStream(png);

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Attachments = new List<FileAttachment> { new(stream, "party-lobby.png") };
                m.Components = comp;
            });
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("pg:leave:*", true)]
    public async Task HandleLobbyLeaveAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var (ok, err) = await partyRepo.TryLeaveLobbyAsync(sessionId, Context.User.Id);
            if (!ok)
            {
                await RespondAsync(embed: embeds.Error(err), ephemeral: true);
                return;
            }

            await DeferAsync();
            var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
            var session = (await partyRepo.GetSessionByIdAsync(sessionId))!;
            var players = await partyRepo.GetPlayersAsync(sessionId);
            var (png, embed, comp) = await BuildLobbyVisualAsync(session, players, cfg.CurrencySymbol);
            await using var stream = new MemoryStream(png);

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embed;
                m.Attachments = new List<FileAttachment> { new(stream, "party-lobby.png") };
                m.Components = comp;
            });
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("pg:cancel:*", true)]
    public async Task HandleLobbyCancelAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        await DeferAsync();
        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status is not ("lobby" or "active"))
            {
                await FollowupAsync(embed: embeds.Error("Эта игра уже завершена."), ephemeral: true);
                return;
            }

            var member = Context.User as SocketGuildUser;
            var isAdmin = member?.GuildPermissions.Administrator == true || member?.GuildPermissions.ManageChannels == true;
            if (Context.User.Id != session.HostId && !isAdmin)
            {
                await FollowupAsync(embed: embeds.Error("Отменить стол может только его создатель или администратор."), ephemeral: true);
                return;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            await partyService.UnmuteSessionUsersAsync(Context.Guild, session.VoiceChannelId, session.MutedUserIds, players.Select(x => x.UserId));
            await partyRepo.CancelAndRefundSessionAsync(sessionId, "cancelled");

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = embeds.Warning("Стол закрыт", $"Игра #{sessionId} отменена организатором <@{Context.User.Id}>. Все вступительные взносы возвращены на балансы участников.");
                m.Components = null;
            });
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("pg:start:*", true)]
    public async Task HandleLobbyStartAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "lobby")
            {
                await RespondAsync(embed: embeds.Error("Партия уже запущена или закрыта."), ephemeral: true);
                return;
            }

            if (Context.User.Id != session.HostId)
            {
                await RespondAsync(embed: embeds.Error("Запустить партию может только создатель лобби."), ephemeral: true);
                return;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var (minP, _, title, _) = PartyGameService.GetGameMeta(session.GameType);
            if (players.Count < minP)
            {
                await RespondAsync(embed: embeds.Warning("Недостаточно игроков", $"Для старта игры «{title}» необходимо минимум **{minP}** участников (сейчас: **{players.Count}**)."), ephemeral: true);
                return;
            }

            await DeferAsync();

            (session, players) = session.GameType switch
            {
                "mafia" => await partyService.StartMafiaGameAsync(Context.Guild, session, players),
                "bunker" => await partyService.StartBunkerGameAsync(session, players),
                "spyfall" => await partyService.StartSpyfallGameAsync(session, players),
                "liarsbar" => await partyService.StartLiarsBarGameAsync(session, players),
                "quiz" => await partyService.StartQuizGameAsync(session, players),
                "codenames" => await partyService.StartCodenamesGameAsync(session, players),
                _ => (session, players)
            };

            await RefreshGameTableMessageAsync(session, players);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task RefreshGameTableMessageAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players)
    {
        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        byte[] png;
        Embed embed;
        MessageComponent? comp;

        switch (session.GameType)
        {
            case "mafia":
                png = await partyService.RenderMafiaTableAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildMafiaMessage(session, players, cfg.CurrencySymbol);
                break;
            case "bunker":
                png = await partyService.RenderBunkerBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildBunkerMessage(session, players, cfg.CurrencySymbol);
                break;
            case "spyfall":
                png = await partyService.RenderSpyfallBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildSpyfallMessage(session, players, cfg.CurrencySymbol);
                break;
            case "liarsbar":
                png = await partyService.RenderLiarsBarTableAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildLiarsBarMessage(session, players, cfg.CurrencySymbol);
                break;
            case "quiz":
                png = await partyService.RenderQuizBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildQuizMessage(session, players, cfg.CurrencySymbol);
                break;
            case "codenames":
                png = await partyService.RenderCodenamesBoardAsync(session, showCaptainKeymap: false);
                (embed, comp) = BuildCodenamesMessage(session, players, cfg.CurrencySymbol);
                break;
            default:
                return;
        }

        await using var stream = new MemoryStream(png);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.Attachments = new List<FileAttachment> { new(stream, "game-board.png") };
            m.Components = comp;
        });
    }

    // =========================================================================
    // 1. MAFIA INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildMafiaMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var isFinished = session.Status == "finished";
        var isNight = session.Phase == "night";

        var desc = isFinished
            ? $"{session.WinnerSummary}\n\n**События финала:** {state.LastEventBanner}"
            : isNight
                ? $"🌙 **Ночь {session.RoundNum}:** Город спит. Все живые ролевые игроки нажимают **«🎭 Моя роль и ночной ход»** ниже, чтобы сделать тайный выбор без ЛС!"
                : $"☀️ **День {session.RoundNum}:** {state.LastEventBanner}\nОбсудите улики и нажмите **«🗳️ Голосовать на суде»**, чтобы выбрать подозреваемого.";

        var embed = new EmbedBuilder()
            .WithColor(isFinished ? new Color(245, 158, 11) : isNight ? new Color(99, 102, 241) : new Color(244, 63, 94))
            .WithTitle($"🎩 Мафия KRLZ #{session.Id}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder()
            .WithButton("Моя роль и ночной ход", $"mafia:role:{session.Id}", ButtonStyle.Primary, new Emoji("🎭"));

        if (!isNight)
        {
            cb.WithButton("Голосовать на суде", $"mafia:vote_btn:{session.Id}", ButtonStyle.Danger, new Emoji("🗳️"));
        }

        cb.WithButton(isNight ? "Завершить ночь (Ведущий)" : "Подвести итог суда", $"mafia:advance:{session.Id}", ButtonStyle.Secondary, new Emoji("⏩"))
          .WithButton("Хроника", $"mafia:logs:{session.Id}", ButtonStyle.Secondary, new Emoji("📜"))
          .WithButton("Закрыть", $"pg:cancel:{session.Id}", ButtonStyle.Danger, new Emoji("✖️"));

        return (embed, cb.Build());
    }

    [ComponentInteraction("mafia:role:*", true)]
    public async Task HandleMafiaRoleButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
        {
            await RespondAsync(embed: embeds.Error("Эта партия в Мафию уже завершена."), ephemeral: true);
            return;
        }

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null)
        {
            await RespondAsync(embed: embeds.Error("Вы не участвуете в этой партии."), ephemeral: true);
            return;
        }

        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var sb = new StringBuilder();
        sb.AppendLine($"**Ваша роль:** {PartyGameService.FormatMafiaRoleName(me.RoleCode)}");
        sb.AppendLine($"**Статус:** {(me.IsAlive ? "🟢 Жив" : "🔴 Погиб (наблюдатель)")}");
        sb.AppendLine($"*{PartyGameService.FormatMafiaRoleDescription(me.RoleCode)}*");

        if (me.TeamCode == "mafia")
        {
            var comrades = players.Where(x => x.TeamCode == "mafia").Select(x => $"#{x.SeatIndex} {x.DisplayName} ({PartyGameService.FormatMafiaRoleName(x.RoleCode)})");
            sb.AppendLine($"\n🤝 **Состав синдиката:** {string.Join(", ", comrades)}");
            if (state.MafiaChatNotes.Count > 0)
            {
                sb.AppendLine("\n💬 **Тайные записки мафии:**");
                foreach (var n in state.MafiaChatNotes.TakeLast(4))
                    sb.AppendLine($"• {n}");
            }
        }

        var cb = new ComponentBuilder();
        var aliveTargets = players.Where(x => x.IsAlive).ToList();

        if (me.IsAlive && session.Phase == "night")
        {
            if (me.RoleCode is "mafia" or "don")
            {
                var menu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:kill")
                    .WithPlaceholder("🔪 Выберите жертву мафии на эту ночь...");
                foreach (var t in aliveTargets.Where(x => x.TeamCode != "mafia").Take(25))
                    menu.AddOption($"#{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());

                if (menu.Options.Count > 0)
                    cb.WithSelectMenu(menu);

                cb.WithButton("Тайная записка мафии", $"mafia:note_btn:{sessionId}", ButtonStyle.Secondary, new Emoji("💬"));
            }

            if (me.RoleCode == "don")
            {
                var donMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:don_check")
                    .WithPlaceholder("🎩 Проверить игрока на роль Комиссара...");
                foreach (var t in aliveTargets.Where(x => x.UserId != me.UserId).Take(25))
                    donMenu.AddOption($"Проверить #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                if (donMenu.Options.Count > 0)
                    cb.WithSelectMenu(donMenu);
            }
            else if (me.RoleCode == "commissar")
            {
                var checkMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:comm_check")
                    .WithPlaceholder("🕵️ Проверить досье подозреваемого...");
                var shootMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:comm_shoot")
                    .WithPlaceholder("🔫 Произвести выстрел на поражение...");

                foreach (var t in aliveTargets.Where(x => x.UserId != me.UserId).Take(25))
                {
                    checkMenu.AddOption($"Проверить #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                    shootMenu.AddOption($"Стрелять в #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                }
                if (checkMenu.Options.Count > 0)
                {
                    cb.WithSelectMenu(checkMenu);
                    cb.WithSelectMenu(shootMenu);
                }
            }
            else if (me.RoleCode == "doctor")
            {
                var docMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:doc_heal")
                    .WithPlaceholder("🩺 Кого вылечить этой ночью?");
                foreach (var t in aliveTargets.Take(25))
                    docMenu.AddOption($"Лечить #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                cb.WithSelectMenu(docMenu);
            }
            else if (me.RoleCode == "mistress")
            {
                var misMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:mistress")
                    .WithPlaceholder("💋 К кому прийти в гости (блок + алиби)?");
                foreach (var t in aliveTargets.Where(x => x.UserId != me.UserId).Take(25))
                    misMenu.AddOption($"Посетить #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                if (misMenu.Options.Count > 0)
                    cb.WithSelectMenu(misMenu);
            }
            else if (me.RoleCode == "maniac")
            {
                var manMenu = new SelectMenuBuilder()
                    .WithCustomId($"mafia:act:{sessionId}:maniac")
                    .WithPlaceholder("🪓 Выберите жертву ночной охоты...");
                foreach (var t in aliveTargets.Where(x => x.UserId != me.UserId).Take(25))
                    manMenu.AddOption($"Устранить #{t.SeatIndex} {t.DisplayName}", t.UserId.ToString());
                if (manMenu.Options.Count > 0)
                    cb.WithSelectMenu(manMenu);
            }
        }

        await RespondAsync(embed: embeds.Info($"Секретное досье • Мафия #{sessionId}", sb.ToString()), components: cb.Build(), ephemeral: true);
    }

    [ComponentInteraction("mafia:note_btn:*", true)]
    public async Task HandleMafiaNoteButtonAsync(string sessionIdStr)
    {
        await RespondWithModalAsync<MafiaSecretNoteModal>($"mafia:note_modal:{sessionIdStr}");
    }

    [ModalInteraction("mafia:note_modal:*", true)]
    public async Task HandleMafiaNoteModalAsync(string sessionIdStr, MafiaSecretNoteModal modal)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
            {
                await RespondAsync(embed: embeds.Error("Игра уже завершена."), ephemeral: true);
                return;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
            if (me is null || me.TeamCode != "mafia")
            {
                await RespondAsync(embed: embeds.Error("Записки доступны только членам Мафии."), ephemeral: true);
                return;
            }

            var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
            state.MafiaChatNotes.Add($"**{me.DisplayName}:** {modal.Note.Trim()}");

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            await RespondAsync(embed: embeds.Success("Записка передана", "Ваши напарники увидят её при нажатии на кнопку «Моя роль и ночной ход»."), ephemeral: true);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("mafia:act:*:*", true)]
    public async Task HandleMafiaNightActionSelectAsync(string sessionIdStr, string actionType, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !ulong.TryParse(values[0], out var targetId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active" || session.Phase != "night")
            {
                await RespondAsync(embed: embeds.Error("Ночная фаза уже завершилась."), ephemeral: true);
                return;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var me = players.FirstOrDefault(x => x.UserId == Context.User.Id && x.IsAlive);
            var target = players.FirstOrDefault(x => x.UserId == targetId && x.IsAlive);
            if (me is null || target is null)
            {
                await RespondAsync(embed: embeds.Error("Недопустимая цель ночного действия."), ephemeral: true);
                return;
            }

            var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
            string replyText;

            switch (actionType)
            {
                case "kill" when me.RoleCode is "mafia" or "don":
                    state.NightKillVotes[me.UserId] = targetId;
                    replyText = $"🔪 Вы выбрали целью покушения мафии: **#{target.SeatIndex} {target.DisplayName}**.";
                    break;
                case "don_check" when me.RoleCode == "don":
                    state.DonCheckTargetId = targetId;
                    var isComm = target.RoleCode == "commissar";
                    replyText = $"🎩 Результат проверки Дона: **#{target.SeatIndex} {target.DisplayName}** — {(isComm ? "🕵️ ЭТО КОМИССАР!" : "НЕ является Комиссаром.")}";
                    break;
                case "comm_check" when me.RoleCode == "commissar":
                    state.CommissarTargetId = targetId;
                    state.CommissarMode = "check";
                    var isMaf = target.TeamCode == "mafia";
                    replyText = $"🕵️ Секретное досье: **#{target.SeatIndex} {target.DisplayName}** — {(isMaf ? "🔴 ПРИНАДЛЕЖИТ К МАФИИ!" : "🟢 Не состоит в Мафии.")}";
                    break;
                case "comm_shoot" when me.RoleCode == "commissar":
                    state.CommissarTargetId = targetId;
                    state.CommissarMode = "shoot";
                    replyText = $"🔫 Вы приняли решение стрелять этой ночью в **#{target.SeatIndex} {target.DisplayName}**!";
                    break;
                case "doc_heal" when me.RoleCode == "doctor":
                    state.DoctorTargetId = targetId;
                    replyText = $"🩺 Вы выехали на ночное дежурство к **#{target.SeatIndex} {target.DisplayName}**.";
                    break;
                case "mistress" when me.RoleCode == "mistress":
                    state.MistressTargetId = targetId;
                    replyText = $"💋 Вы провели ночь у **#{target.SeatIndex} {target.DisplayName}** (его ночной ход заблокирован, днём у него алиби).";
                    break;
                case "maniac" when me.RoleCode == "maniac":
                    state.ManiacTargetId = targetId;
                    replyText = $"🪓 Ваша цель на эту ночь: **#{target.SeatIndex} {target.DisplayName}**.";
                    break;
                default:
                    await RespondAsync(embed: embeds.Error("Это действие недоступно вашей роли."), ephemeral: true);
                    return;
            }

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            var allActed = PartyGameService.HaveAllMafiaNightRolesActed(state, players);
            if (allActed)
            {
                var (newSession, newPlayers, finished, winners) = await partyService.ResolveMafiaNightAsync(Context.Guild, (await partyRepo.GetSessionByIdAsync(sessionId))!, players);
                await RespondAsync(embed: embeds.Success("Ночной ход принят", replyText + "\n☀️ Все ночные роли сделали ход — наступает утро!"), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync(newSession, newPlayers);

                if (finished)
                    await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "mafia", newPlayers, winners, Context.Channel);
                return;
            }

            await RespondAsync(embed: embeds.Success("Ночной ход зарегистрирован", replyText), ephemeral: true);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("mafia:vote_btn:*", true)]
    public async Task HandleMafiaVoteButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active" || session.Phase == "night")
        {
            await RespondAsync(embed: embeds.Error("Сейчас не идёт дневное голосование."), ephemeral: true);
            return;
        }

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null || !me.IsAlive)
        {
            await RespondAsync(embed: embeds.Error("Голосовать могут только живые участники партии."), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"mafia:day_vote:{sessionId}")
            .WithPlaceholder("⚖️ Выберите подозреваемого для изгнания...")
            .AddOption("🏳️ Воздержаться (Никого не казнить)", "0");

        foreach (var p in players.Where(x => x.IsAlive && x.UserId != me.UserId).Take(24))
            menu.AddOption($"Изгнать #{p.SeatIndex} {p.DisplayName}", p.UserId.ToString());

        var comp = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(embed: embeds.Info("Дневной суд города", "Выберите кандидата на изгнание или воздержитесь:"), components: comp, ephemeral: true);
    }

    [ComponentInteraction("mafia:day_vote:*", true)]
    public async Task HandleMafiaDayVoteSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !ulong.TryParse(values[0], out var targetId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active" || session.Phase == "night")
            {
                await RespondAsync(embed: embeds.Error("Голосование уже закрыто."), ephemeral: true);
                return;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var me = players.FirstOrDefault(x => x.UserId == Context.User.Id && x.IsAlive);
            if (me is null)
            {
                await RespondAsync(embed: embeds.Error("Только живые игроки могут голосовать."), ephemeral: true);
                return;
            }

            var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
            state.DayVotes[me.UserId] = targetId;

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            var aliveCount = players.Count(x => x.IsAlive);
            if (state.DayVotes.Count >= aliveCount)
            {
                var (newSession, newPlayers, finished, winners) = await partyService.ResolveMafiaDayVoteAsync(Context.Guild, (await partyRepo.GetSessionByIdAsync(sessionId))!, players);
                await RespondAsync(embed: embeds.Success("Голос учтён", "Все живые игроки проголосовали! Подводим итоги дневного суда."), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync(newSession, newPlayers);

                if (finished)
                    await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "mafia", newPlayers, winners, Context.Channel);
                return;
            }

            await RespondAsync(embed: embeds.Success("Голос принят", $"Проголосовало: **{state.DayVotes.Count} / {aliveCount}**."), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("mafia:advance:*", true)]
    public async Task HandleMafiaAdvancePhaseAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
            {
                await RespondAsync(embed: embeds.Error("Игра уже завершена."), ephemeral: true);
                return;
            }

            var member = Context.User as SocketGuildUser;
            var isAdmin = member?.GuildPermissions.Administrator == true;
            if (Context.User.Id != session.HostId && !isAdmin)
            {
                await RespondAsync(embed: embeds.Error("Переключить фазу досрочно может только организатор стола или администратор."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var players = await partyRepo.GetPlayersAsync(sessionId);

            var (newSession, newPlayers, finished, winners) = session.Phase == "night"
                ? await partyService.ResolveMafiaNightAsync(Context.Guild, session, players)
                : await partyService.ResolveMafiaDayVoteAsync(Context.Guild, session, players);

            await RefreshGameTableMessageAsync(newSession, newPlayers);

            if (finished)
                await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "mafia", newPlayers, winners, Context.Channel);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("mafia:logs:*", true)]
    public async Task HandleMafiaLogsAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null)
            return;

        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var text = state.ChronicleLogs.Count == 0
            ? "Хроника пока пуста."
            : string.Join("\n• ", state.ChronicleLogs.TakeLast(15));

        await RespondAsync(embed: embeds.Info($"📜 Хроника партии Мафии #{sessionId}", "• " + text), ephemeral: true);
    }

    // =========================================================================
    // 2. BUNKER INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildBunkerMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
        var isFinished = session.Status == "finished";
        var isVote = session.Phase == "vote";

        var desc = isFinished
            ? $"{state.EpilogueReport}\n\n**Катастрофа:** {state.DisasterTitle}"
            : isVote
                ? $"🗳️ **Раунд {session.RoundNum} — Голосование за изгнание:** Обсудите открытые характеристики и выберите, кто НЕ попадёт в бункер!"
                : $"📂 **Раунд {session.RoundNum} — Вскрытие характеристик:** Каждый выживший нажимает **«🪪 Мое досье и спецкарта»** и раскрывает одну свою закрытую характеристику.";

        var embed = new EmbedBuilder()
            .WithColor(isFinished ? new Color(16, 185, 129) : new Color(245, 158, 11))
            .WithTitle($"☢️ Бункер KRLZ #{session.Id}  •  Мест: {state.BunkerCapacity}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder()
            .WithButton("Мое досье и спецкарта", $"bunker:dossier:{session.Id}", ButtonStyle.Primary, new Emoji("🪪"))
            .WithButton("Голосовать за изгнание", $"bunker:vote_btn:{session.Id}", ButtonStyle.Danger, new Emoji("🗳️"))
            .WithButton("Следующий этап (Ведущий)", $"bunker:advance:{session.Id}", ButtonStyle.Secondary, new Emoji("⏩"))
            .WithButton("Закрыть", $"pg:cancel:{session.Id}", ButtonStyle.Danger, new Emoji("✖️"));

        return (embed, cb.Build());
    }

    [ComponentInteraction("bunker:dossier:*", true)]
    public async Task HandleBunkerDossierAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
        {
            await RespondAsync(embed: embeds.Error("Партия в Бункер уже завершена."), ephemeral: true);
            return;
        }

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null)
        {
            await RespondAsync(embed: embeds.Error("Вы не участвуете в этой партии."), ephemeral: true);
            return;
        }

        var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(me.PlayerDataJson) ?? new BunkerPlayerStateDto();
        var sb = new StringBuilder();
        sb.AppendLine($"**Статус:** {(me.IsAlive ? "🟢 Претендент на место в бункере" : "🔴 Изгнан из бункера")}");
        sb.AppendLine($"**Суммарный индекс полезности:** `{pState.Traits.Sum(x => x.SurvivalScore)}` ед.\n");

        foreach (var t in pState.Traits)
        {
            var badge = t.Revealed ? "🔓 [Открыто всем]" : "🔒 [Скрыто]";
            sb.AppendLine($"• **{t.Label}:** {t.Value} `{badge}`");
        }

        sb.AppendLine($"\n🃏 **Спецкарта:** {pState.SpecialCardTitle} — {(pState.SpecialCardUsed ? "`Использована`" : "`Доступна`")}");

        var cb = new ComponentBuilder();
        if (me.IsAlive)
        {
            var hidden = pState.Traits.Where(x => !x.Revealed).ToList();
            if (hidden.Count > 0 && !pState.RevealedThisRound)
            {
                var menu = new SelectMenuBuilder()
                    .WithCustomId($"bunker:reveal_select:{sessionId}")
                    .WithPlaceholder("📂 Выберите характеристику для вскрытия в этом раунде...");
                foreach (var h in hidden)
                    menu.AddOption($"Вскрыть: {h.Label} ({h.Value})", h.Category);
                cb.WithSelectMenu(menu);
            }

            if (!pState.SpecialCardUsed)
            {
                cb.WithButton("Активировать спецкарту!", $"bunker:spec_btn:{sessionId}", ButtonStyle.Success, new Emoji("🃏"));
            }
        }

        await RespondAsync(embed: embeds.Info($"Личное досье выжившего • Бункер #{sessionId}", sb.ToString()), components: cb.Build(), ephemeral: true);
    }

    [ComponentInteraction("bunker:reveal_select:*", true)]
    public async Task HandleBunkerRevealSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0)
            return;

        var category = values[0];
        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
            {
                await RespondAsync(embed: embeds.Error("Игра уже завершена."), ephemeral: true);
                return;
            }

            var players = (await partyRepo.GetPlayersAsync(sessionId)).ToList();
            var idx = players.FindIndex(x => x.UserId == Context.User.Id && x.IsAlive);
            if (idx < 0)
            {
                await RespondAsync(embed: embeds.Error("Вы не можете вскрывать карты."), ephemeral: true);
                return;
            }

            var me = players[idx];
            var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(me.PlayerDataJson) ?? new BunkerPlayerStateDto();
            var trait = pState.Traits.FirstOrDefault(x => x.Category == category);
            if (trait is null || trait.Revealed)
            {
                await RespondAsync(embed: embeds.Error("Эта характеристика уже открыта."), ephemeral: true);
                return;
            }

            trait.Revealed = true;
            pState.RevealedThisRound = true;
            players[idx] = me with { PlayerDataJson = JsonSerializer.Serialize(pState) };

            var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
            state.EventLogs.Add($"📂 **{me.DisplayName}** рассекретил «{trait.Label}»: **{trait.Value}**!");

            var allRevealed = players.Where(x => x.IsAlive).All(x =>
            {
                var ps = JsonSerializer.Deserialize<BunkerPlayerStateDto>(x.PlayerDataJson);
                return ps is not null && (ps.RevealedThisRound || ps.Traits.All(t => t.Revealed));
            });

            var nextPhase = allRevealed ? "vote" : session.Phase;

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, nextPhase, session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt, players);

            await RespondAsync(embed: embeds.Success("Характеристика рассекречена!", $"Вы открыли **{trait.Label}: {trait.Value}**. Таблица бункера обновлена!"), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("bunker:spec_btn:*", true)]
    public async Task HandleBunkerSpecialCardAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
            {
                await RespondAsync(embed: embeds.Error("Игра уже завершена."), ephemeral: true);
                return;
            }

            var players = (await partyRepo.GetPlayersAsync(sessionId)).ToList();
            var idx = players.FindIndex(x => x.UserId == Context.User.Id && x.IsAlive);
            if (idx < 0)
                return;

            var me = players[idx];
            var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(me.PlayerDataJson) ?? new BunkerPlayerStateDto();
            if (pState.SpecialCardUsed)
            {
                await RespondAsync(embed: embeds.Error("Ваша спецкарта уже использована."), ephemeral: true);
                return;
            }

            pState.SpecialCardUsed = true;
            var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
            string effectText;

            switch (pState.SpecialCardCode)
            {
                case "heal_self":
                {
                    var hlth = pState.Traits.FirstOrDefault(x => x.Category == "health");
                    if (hlth is not null)
                    {
                        hlth.Value = "Идеально здоров (Излечен Панацеей)";
                        hlth.SurvivalScore = 26;
                        hlth.Revealed = true;
                    }
                    effectText = $"💉 **{me.DisplayName}** применил спецкарту «Панацея» и полностью излечился!";
                    break;
                }
                case "reroll_job":
                {
                    var job = pState.Traits.FirstOrDefault(x => x.Category == "job");
                    if (job is not null)
                    {
                        job.Value = "Ведущий инженер жизнеобеспечения бункера";
                        job.SurvivalScore = 36;
                        job.Revealed = true;
                    }
                    effectText = $"🎓 **{me.DisplayName}** прошёл экстренную переквалификацию и стал **Ведущим инженером бункера**!";
                    break;
                }
                case "upgrade_bag":
                {
                    var bag = pState.Traits.FirstOrDefault(x => x.Category == "baggage");
                    if (bag is not null)
                    {
                        bag.Value = "Армейский кейс выживания + генератор";
                        bag.SurvivalScore = 26;
                        bag.Revealed = true;
                    }
                    effectText = $"🧰 **{me.DisplayName}** вскрыл спецкарту «Военный склад» и получил элитный армейский кейс!";
                    break;
                }
                case "spy_reveal":
                {
                    var others = players.Where(x => x.IsAlive && x.UserId != me.UserId).ToList();
                    if (others.Count > 0)
                    {
                        var victim = others[PartyGameService.NextInt(0, others.Count)];
                        var vIdx = players.FindIndex(x => x.UserId == victim.UserId);
                        var vState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(victim.PlayerDataJson) ?? new BunkerPlayerStateDto();
                        var vHealth = vState.Traits.FirstOrDefault(x => x.Category == "health") ?? vState.Traits.First();
                        vHealth.Revealed = true;
                        players[vIdx] = victim with { PlayerDataJson = JsonSerializer.Serialize(vState) };
                        effectText = $"🔍 **{me.DisplayName}** применил «Детектор правды» и рассекретил здоровье **{victim.DisplayName}**: `{vHealth.Value}`!";
                    }
                    else
                    {
                        effectText = $"🔍 **{me.DisplayName}** активировал спецкарту разведки!";
                    }
                    break;
                }
                default:
                {
                    var phob = pState.Traits.FirstOrDefault(x => x.Category == "phobia");
                    if (phob is not null)
                    {
                        phob.Value = "Хладнокровный лидер (без фобий)";
                        phob.SurvivalScore = 20;
                        phob.Revealed = true;
                    }
                    effectText = $"🧠 **{me.DisplayName}** прошёл психотренинг и избавился от фобий!";
                    break;
                }
            }

            players[idx] = me with
            {
                Score = pState.Traits.Sum(x => x.SurvivalScore),
                PlayerDataJson = JsonSerializer.Serialize(pState)
            };
            state.EventLogs.Add(effectText);

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt, players);

            await RespondAsync(embed: embeds.Success("Спецкарта активирована!", effectText), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("bunker:vote_btn:*", true)]
    public async Task HandleBunkerVoteButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id && x.IsAlive);
        if (me is null)
        {
            await RespondAsync(embed: embeds.Error("Голосовать могут только претенденты, оставшиеся у входа в бункер."), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"bunker:vote_select:{sessionId}")
            .WithPlaceholder("🚪 Выберите, кого изгнать из очереди в бункер...");

        foreach (var p in players.Where(x => x.IsAlive && x.UserId != me.UserId).Take(25))
            menu.AddOption($"Изгнать #{p.SeatIndex} {p.DisplayName}", p.UserId.ToString());

        var comp = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(embed: embeds.Info("Голосование за изгнание из Бункера", "Выберите наименее полезного кандидата:"), components: comp, ephemeral: true);
    }

    [ComponentInteraction("bunker:vote_select:*", true)]
    public async Task HandleBunkerVoteSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !ulong.TryParse(values[0], out var targetId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var me = players.FirstOrDefault(x => x.UserId == Context.User.Id && x.IsAlive);
            if (me is null)
                return;

            var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
            state.ExileVotes[me.UserId] = targetId;

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, "vote", session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            var aliveCount = players.Count(x => x.IsAlive);
            if (state.ExileVotes.Count >= aliveCount)
            {
                var (newSession, newPlayers, finished, winners) = await partyService.ResolveBunkerExileVoteAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
                await RespondAsync(embed: embeds.Success("Голос принят", "Все выжившие проголосовали! Двери бункера закрываются за изгнанным..."), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync(newSession, newPlayers);

                if (finished)
                    await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "bunker", newPlayers, winners, Context.Channel);
                return;
            }

            await RespondAsync(embed: embeds.Success("Голос учтён", $"Проголосовало: **{state.ExileVotes.Count} / {aliveCount}**."), ephemeral: true);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("bunker:advance:*", true)]
    public async Task HandleBunkerAdvanceAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            if (Context.User.Id != session.HostId && (Context.User as SocketGuildUser)?.GuildPermissions.Administrator != true)
            {
                await RespondAsync(embed: embeds.Error("Завершить раунд голосования может только организатор стола."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var players = await partyRepo.GetPlayersAsync(sessionId);
            var (newSession, newPlayers, finished, winners) = await partyService.ResolveBunkerExileVoteAsync(session, players);
            await RefreshGameTableMessageAsync(newSession, newPlayers);

            if (finished)
                await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "bunker", newPlayers, winners, Context.Channel);
        }
        finally
        {
            sem.Release();
        }
    }

    // =========================================================================
    // 3. SPYFALL INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildSpyfallMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<SpyfallGameStateDto>(session.StateJson) ?? new SpyfallGameStateDto();
        var isFinished = session.Status == "finished";

        var desc = isFinished
            ? $"{session.WinnerSummary}\n**Секретная локация:** `{state.SecretLocation}` • **Шпион:** <@{state.SpyUserId}>"
            : $"Нажмите **«🕵️ Моя локация / Назвать локацию»**, чтобы узнать свою легенду (или угадать локацию, если вы Шпион).\nКогда будете готовы разоблачить агента — жмите **«🗳️ Голосовать против Шпиона»**!";

        var embed = new EmbedBuilder()
            .WithColor(isFinished ? new Color(16, 185, 129) : new Color(56, 189, 248))
            .WithTitle($"🕵️ Шпион (Spyfall) #{session.Id}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder()
            .WithButton("Моя локация / Угадать локацию", $"spy:card:{session.Id}", ButtonStyle.Primary, new Emoji("🕵️"))
            .WithButton("Голосовать против Шпиона", $"spy:vote_btn:{session.Id}", ButtonStyle.Danger, new Emoji("🗳️"))
            .WithButton("Закрыть", $"pg:cancel:{session.Id}", ButtonStyle.Secondary, new Emoji("✖️"));

        return (embed, cb.Build());
    }

    [ComponentInteraction("spy:card:*", true)]
    public async Task HandleSpyfallCardAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null)
        {
            await RespondAsync(embed: embeds.Error("Вы не участвуете в этой партии."), ephemeral: true);
            return;
        }

        var state = JsonSerializer.Deserialize<SpyfallGameStateDto>(session.StateJson) ?? new SpyfallGameStateDto();
        var isSpy = me.UserId == state.SpyUserId;

        if (!isSpy)
        {
            var role = state.PlayerRoles.GetValueOrDefault(me.UserId, "Сотрудник");
            await RespondAsync(
                embed: embeds.Info(
                    "Секретный допуск агента",
                    $"📍 **Ваша локация:** `{state.SecretLocation}`\n🪪 **Ваша роль на объекте:** `{role}`\n\nЗадавайте вопросы другим участникам, чтобы вычислить Шпиона, но не выдайте локацию слишком очевидными намёками!"),
                ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"spy:guess_loc:{sessionId}")
            .WithPlaceholder("🎯 Вабанком назвать секретную локацию...");

        foreach (var loc in state.AllLocations.Take(25))
            menu.AddOption(loc, loc);

        var comp = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(
            embed: embeds.Warning(
                "🕶️ ВЫ — ШПИОН!",
                "Вы не знаете, на какой локации находятся остальные игроки!\nВнимательно слушайте их вопросы и ответы. В любой момент вы можете выбрать локацию из меню ниже: если угадаете — мгновенно заберёте весь банк!"),
            components: comp,
            ephemeral: true);
    }

    [ComponentInteraction("spy:guess_loc:*", true)]
    public async Task HandleSpyfallGuessLocationAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0)
            return;

        var guessedLoc = values[0];
        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var state = JsonSerializer.Deserialize<SpyfallGameStateDto>(session.StateJson) ?? new SpyfallGameStateDto();
            if (Context.User.Id != state.SpyUserId)
                return;

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var spyWon = string.Equals(guessedLoc, state.SecretLocation, StringComparison.OrdinalIgnoreCase);

            var summary = spyWon
                ? $"🕶️ **ПОБЕДА ШПИОНА!** <@{state.SpyUserId}> сбросил маскировку и точно назвал секретную локацию **«{state.SecretLocation}»**!"
                : $"🚨 **ШПИОН ПРОСЧИТАЛСЯ!** <@{state.SpyUserId}> назвал локацию «{guessedLoc}», но агенты находились на объекте **«{state.SecretLocation}»**! Победа агентов!";

            state.LastLog = summary;
            var winners = spyWon
                ? new List<ulong> { state.SpyUserId }
                : players.Where(x => x.UserId != state.SpyUserId).Select(x => x.UserId).ToList();

            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id, winners, baseBonusPerWinner: 350, JsonSerializer.Serialize(state), summary);

            var updatedSession = (await partyRepo.GetSessionByIdAsync(sessionId))!;
            await RespondAsync(embed: embeds.Success("Финал операции!", summary), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync(updatedSession, players);
            await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "spyfall", players, winners, Context.Channel);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("spy:vote_btn:*", true)]
    public async Task HandleSpyfallVoteButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var players = await partyRepo.GetPlayersAsync(sessionId);
        if (players.All(x => x.UserId != Context.User.Id))
        {
            await RespondAsync(embed: embeds.Error("Вы не участвуете в этой игре."), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"spy:accuse:{sessionId}")
            .WithPlaceholder("🔍 Кто по вашему мнению является Шпионом?");

        foreach (var p in players.Where(x => x.UserId != Context.User.Id).Take(25))
            menu.AddOption($"Обвинить #{p.SeatIndex} {p.DisplayName}", p.UserId.ToString());

        await RespondAsync(embed: embeds.Info("Голосование контрразведки", "Выберите подозреваемого:"), components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    [ComponentInteraction("spy:accuse:*", true)]
    public async Task HandleSpyfallAccuseSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !ulong.TryParse(values[0], out var targetId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var state = JsonSerializer.Deserialize<SpyfallGameStateDto>(session.StateJson) ?? new SpyfallGameStateDto();
            state.AccuseVotes[Context.User.Id] = targetId;

            if (state.AccuseVotes.Count < players.Count - 1)
            {
                await partyRepo.SaveFullGameStateAsync(
                    session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                    JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);
                await RespondAsync(embed: embeds.Success("Голос принят", $"Проголосовало: **{state.AccuseVotes.Count} / {players.Count - 1}**."), ephemeral: true);
                return;
            }

            var topSuspect = state.AccuseVotes
                .GroupBy(kv => kv.Value)
                .OrderByDescending(g => g.Count())
                .First().Key;

            var caughtSpy = topSuspect == state.SpyUserId;
            var summary = caughtSpy
                ? $"🏆 **АГЕНТЫ РАЗОБЛАЧИЛИ ШПИОНА!** Большинством голосов пойман <@{state.SpyUserId}> на объекте **«{state.SecretLocation}»**!"
                : $"🕶️ **ПОБЕДА ШПИОНА!** Агенты ошибочно обвинили невиновного <@{topSuspect}>, пока настоящий Шпион <@{state.SpyUserId}> успешно скрылся!";

            state.LastLog = summary;
            var winners = caughtSpy
                ? players.Where(x => x.UserId != state.SpyUserId).Select(x => x.UserId).ToList()
                : new List<ulong> { state.SpyUserId };

            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id, winners, baseBonusPerWinner: 350, JsonSerializer.Serialize(state), summary);

            await RespondAsync(embed: embeds.Success("Голосование завершено!", summary), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
            await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "spyfall", players, winners, Context.Channel);
        }
        finally
        {
            sem.Release();
        }
    }

    // =========================================================================
    // 4. LIAR'S BAR INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildLiarsBarMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<LiarsBarGameStateDto>(session.StateJson) ?? new LiarsBarGameStateDto();
        var isFinished = session.Status == "finished";

        var desc = isFinished
            ? $"{session.WinnerSummary}\n**Финальный лог:** {state.LastActionLog}"
            : $"🃏 **Целевая карта раунда: {PartyGameService.FormatCardLabel(state.TableRank)}** (Джокеры ★ заменяют любую карту).\n" +
              $"Сейчас ход: <@{session.TurnUserId}>\n*{state.LastActionLog}*";

        var embed = new EmbedBuilder()
            .WithColor(isFinished ? new Color(16, 185, 129) : new Color(244, 63, 94))
            .WithTitle($"🔫 Бар Лжецов #{session.Id}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder()
            .WithButton("Мои карты и Сделать ход", $"liars:hand:{session.Id}", ButtonStyle.Primary, new Emoji("🃏"));

        if (state.LastPlayerId.HasValue)
        {
            cb.WithButton("ЛЖЕЦ! Вскрыть карты!", $"liars:call:{session.Id}", ButtonStyle.Danger, new Emoji("🔫"));
        }

        cb.WithButton("Закрыть", $"pg:cancel:{session.Id}", ButtonStyle.Secondary, new Emoji("✖️"));
        return (embed, cb.Build());
    }

    [ComponentInteraction("liars:hand:*", true)]
    public async Task HandleLiarsHandAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null)
        {
            await RespondAsync(embed: embeds.Error("Вы не сидите за этим столом."), ephemeral: true);
            return;
        }

        var state = JsonSerializer.Deserialize<LiarsBarGameStateDto>(session.StateJson) ?? new LiarsBarGameStateDto();
        var pDto = JsonSerializer.Deserialize<LiarsBarPlayerDto>(me.PlayerDataJson) ?? new LiarsBarPlayerDto();

        var cardsFormatted = pDto.Hand.Count == 0
            ? "Карты сброшены"
            : string.Join(", ", pDto.Hand.Select(PartyGameService.FormatCardLabel));

        var sb = new StringBuilder();
        sb.AppendLine($"**Целевая карта стола:** `{PartyGameService.FormatCardLabel(state.TableRank)}`");
        sb.AppendLine($"**Ваша рука ({pDto.Hand.Count}):** {cardsFormatted}");
        sb.AppendLine($"**Ваш револьвер:** сделано `{pDto.RevolverShotsTaken} / 6` выстрелов");

        var cb = new ComponentBuilder();
        if (me.IsAlive && session.TurnUserId == me.UserId && pDto.Hand.Count > 0)
        {
            var maxPlay = Math.Min(3, pDto.Hand.Count);
            var menu = new SelectMenuBuilder()
                .WithCustomId($"liars:play_select:{sessionId}")
                .WithPlaceholder($"Выложите от 1 до {maxPlay} карт рубашкой вверх...")
                .WithMinValues(1)
                .WithMaxValues(maxPlay);

            for (var i = 0; i < pDto.Hand.Count; i++)
            {
                menu.AddOption($"Карта #{i + 1}: {PartyGameService.FormatCardLabel(pDto.Hand[i])}", i.ToString());
            }

            cb.WithSelectMenu(menu);
        }

        await RespondAsync(embed: embeds.Info("Ваша рука • Бар Лжецов", sb.ToString()), components: cb.Build(), ephemeral: true);
    }

    [ComponentInteraction("liars:play_select:*", true)]
    public async Task HandleLiarsPlaySelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0)
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            if (Context.User.Id != session.TurnUserId)
            {
                await RespondAsync(embed: embeds.Error("Сейчас ход другого игрока!"), ephemeral: true);
                return;
            }

            var players = (await partyRepo.GetPlayersAsync(sessionId)).ToList();
            var idx = players.FindIndex(x => x.UserId == Context.User.Id && x.IsAlive);
            if (idx < 0)
                return;

            var me = players[idx];
            var pDto = JsonSerializer.Deserialize<LiarsBarPlayerDto>(me.PlayerDataJson) ?? new LiarsBarPlayerDto();
            var indices = values.Select(v => int.TryParse(v, out var n) ? n : -1)
                .Where(n => n >= 0 && n < pDto.Hand.Count)
                .Distinct()
                .OrderByDescending(n => n)
                .ToList();

            if (indices.Count == 0)
                return;

            var played = new List<string>();
            foreach (var i in indices)
            {
                played.Add(pDto.Hand[i]);
                pDto.Hand.RemoveAt(i);
            }

            players[idx] = me with { PlayerDataJson = JsonSerializer.Serialize(pDto) };

            var state = JsonSerializer.Deserialize<LiarsBarGameStateDto>(session.StateJson) ?? new LiarsBarGameStateDto();
            state.LastPlayerId = me.UserId;
            state.LastPlayedCards = played;
            state.PileCount += played.Count;

            // Advance turn to next living player who has cards (or next living player)
            var alive = players.Where(x => x.IsAlive).ToList();
            var curAlivePos = alive.FindIndex(x => x.UserId == me.UserId);
            var nextPlayer = alive[(curAlivePos + 1) % alive.Count];

            state.LastActionLog = $"🃏 {me.DisplayName} выложил {played.Count} карт(ы) и заявил: «Это {PartyGameService.FormatCardLabel(state.TableRank)}»! Ход {nextPlayer.DisplayName}.";

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum, nextPlayer.UserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt, players);

            await RespondAsync(embed: embeds.Success("Ход сделан", $"Вы выложили **{played.Count}** карт(ы) рубашкой вверх."), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("liars:call:*", true)]
    public async Task HandleLiarsCallBluffAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            if (Context.User.Id != session.TurnUserId)
            {
                await RespondAsync(embed: embeds.Error("Бросить вызов «Лжец!» может только игрок, чья очередь ходить сейчас."), ephemeral: true);
                return;
            }

            var state = JsonSerializer.Deserialize<LiarsBarGameStateDto>(session.StateJson) ?? new LiarsBarGameStateDto();
            if (!state.LastPlayerId.HasValue || state.LastPlayedCards.Count == 0)
            {
                await RespondAsync(embed: embeds.Error("На столе ещё нет сброшенных карт для проверки."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var players = (await partyRepo.GetPlayersAsync(sessionId)).ToList();
            var challengerIdx = players.FindIndex(x => x.UserId == Context.User.Id);
            var accusedIdx = players.FindIndex(x => x.UserId == state.LastPlayerId.Value);
            if (challengerIdx < 0 || accusedIdx < 0)
                return;

            var challenger = players[challengerIdx];
            var accused = players[accusedIdx];

            var isBluff = state.LastPlayedCards.Any(c => c != state.TableRank && c != "JOKER");
            var cardsShown = string.Join(", ", state.LastPlayedCards.Select(PartyGameService.FormatCardLabel));

            var shooterIdx = isBluff ? accusedIdx : challengerIdx;
            var shooter = players[shooterIdx];
            var shooterDto = JsonSerializer.Deserialize<LiarsBarPlayerDto>(shooter.PlayerDataJson) ?? new LiarsBarPlayerDto();

            shooterDto.RevolverShotsTaken++;
            var eliminated = shooterDto.RevolverShotsTaken >= shooterDto.RevolverBulletSlot;

            players[shooterIdx] = shooter with
            {
                IsAlive = !eliminated,
                PlayerDataJson = JsonSerializer.Serialize(shooterDto)
            };

            var verdictStr = isBluff
                ? $"🔥 БЛЕФ РАСКРЫТ! {accused.DisplayName} выложил [{cardsShown}] на столе {state.TableRank}!"
                : $"❄️ КАРТЫ ЧЕСТНЫЕ! У {accused.DisplayName} действительно были [{cardsShown}]!";

            var shotStr = eliminated
                ? $"💥 БАХ! На выстреле {shooterDto.RevolverShotsTaken}/6 револьвер **{shooter.DisplayName}** выстрелил! Игрок выбывает!"
                : $"💨 ЩЕЛЧОК! Камора {shooterDto.RevolverShotsTaken}/6 оказалась пустой — **{shooter.DisplayName}** остаётся в живых!";

            state.LastActionLog = $"{verdictStr} {shotStr}";

            var aliveRemaining = players.Where(x => x.IsAlive).ToList();
            if (aliveRemaining.Count <= 1)
            {
                var winner = aliveRemaining.FirstOrDefault() ?? challenger;
                var winSummary = $"🏆 **ПОБЕДИТЕЛЬ БАРА ЛЖЕЦОВ — <@{winner.UserId}>!** Выжил в русской рулетке и забирает весь банк!";

                await partyRepo.FinishSessionAndDistributePrizeAsync(
                    session.Id, [winner.UserId], baseBonusPerWinner: 400, JsonSerializer.Serialize(state), winSummary, players);

                var finSession = (await partyRepo.GetSessionByIdAsync(sessionId))!;
                await RefreshGameTableMessageAsync(finSession, players);
                await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "liarsbar", players, [winner.UserId], Context.Channel);
                return;
            }

            var nextStarter = players[shooterIdx].IsAlive ? players[shooterIdx].UserId : aliveRemaining[0].UserId;
            PartyGameService.DealNewLiarsSubround(state, players, nextStarter);

            await partyRepo.SaveFullGameStateAsync(
                session.Id, "active", "round", session.RoundNum + 1, nextStarter,
                JsonSerializer.Serialize(state), session.MutedUserIds, string.Empty, DateTimeOffset.UtcNow.AddMinutes(20), players);

            await RefreshGameTableMessageAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    // =========================================================================
    // 5. QUIZ / BRAIN-RING INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildQuizMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();
        var isFinished = session.Status == "finished";

        string desc;
        if (isFinished)
        {
            desc = $"{session.WinnerSummary}\n*{state.LastLog}*";
        }
        else if (state.ActiveCellIndex.HasValue && state.ActiveCellIndex.Value < state.Cells.Count)
        {
            var c = state.Cells[state.ActiveCellIndex.Value];
            desc = $"❓ **Категория: {c.Topic} за {c.Points} очков**\n> **{c.Question}**\n\nЖмите кнопку **«🔔 Ответить!»** быстрее соперников!";
        }
        else
        {
            desc = $"{state.LastLog}\nНажмите **«📋 Выбрать вопрос»**, чтобы открыть ячейку на табло.";
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(245, 158, 11))
            .WithTitle($"🧠 Своя Игра: Брейн-Ринг #{session.Id}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder();
        if (state.ActiveCellIndex.HasValue)
        {
            cb.WithButton("Ответить!", $"quiz:buzz:{session.Id}:{state.ActiveCellIndex.Value}", ButtonStyle.Success, new Emoji("🔔"))
              .WithButton("Пропустить вопрос", $"quiz:skip:{session.Id}", ButtonStyle.Secondary, new Emoji("⏭️"));
        }
        else
        {
            cb.WithButton("Выбрать вопрос", $"quiz:pick_btn:{session.Id}", ButtonStyle.Primary, new Emoji("📋"));
        }

        cb.WithButton("Завершить и наградить лидера", $"quiz:finish:{session.Id}", ButtonStyle.Secondary, new Emoji("🏆"))
          .WithButton("Отмена", $"pg:cancel:{session.Id}", ButtonStyle.Danger, new Emoji("✖️"));

        return (embed, cb.Build());
    }

    [ComponentInteraction("quiz:pick_btn:*", true)]
    public async Task HandleQuizPickButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();
        var menu = new SelectMenuBuilder()
            .WithCustomId($"quiz:pick_cell:{sessionId}")
            .WithPlaceholder("📋 Выберите тему и стоимость вопроса...");

        for (var i = 0; i < state.Cells.Count; i++)
        {
            var c = state.Cells[i];
            if (!c.Answered)
                menu.AddOption($"{c.Topic} — {c.Points} очк.", i.ToString());
        }

        if (menu.Options.Count == 0)
        {
            await RespondAsync(embed: embeds.Info("Все вопросы разыграны", "Нажмите «Завершить и наградить лидера»."), ephemeral: true);
            return;
        }

        await RespondAsync(embed: embeds.Info("Выбор вопроса табло", "Выберите ячейку:"), components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    [ComponentInteraction("quiz:pick_cell:*", true)]
    public async Task HandleQuizPickCellSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !int.TryParse(values[0], out var cellIdx))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();
            if (cellIdx < 0 || cellIdx >= state.Cells.Count || state.Cells[cellIdx].Answered)
            {
                await RespondAsync(embed: embeds.Error("Этот вопрос уже разыгран."), ephemeral: true);
                return;
            }

            state.ActiveCellIndex = cellIdx;
            var cell = state.Cells[cellIdx];
            state.LastLog = $"Открыт вопрос: {cell.Topic} за {cell.Points}! Жмите «Ответить!»";

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, "question", session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            var players = await partyRepo.GetPlayersAsync(sessionId);
            await RespondAsync(embed: embeds.Success("Вопрос открыт!", $"**{cell.Topic} за {cell.Points}:** {cell.Question}"), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("quiz:buzz:*:*", true)]
    public async Task HandleQuizBuzzAsync(string sessionIdStr, string cellIdxStr)
    {
        await RespondWithModalAsync<QuizAnswerModal>($"quiz:ans_modal:{sessionIdStr}:{cellIdxStr}");
    }

    [ModalInteraction("quiz:ans_modal:*:*", true)]
    public async Task HandleQuizAnswerModalAsync(string sessionIdStr, string cellIdxStr, QuizAnswerModal modal)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || !int.TryParse(cellIdxStr, out var cellIdx))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var players = (await partyRepo.GetPlayersAsync(sessionId)).ToList();
            var pIdx = players.FindIndex(x => x.UserId == Context.User.Id);
            if (pIdx < 0)
            {
                await RespondAsync(embed: embeds.Error("Вы не зарегистрированы в этом матче."), ephemeral: true);
                return;
            }

            var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();
            if (state.ActiveCellIndex != cellIdx || cellIdx < 0 || cellIdx >= state.Cells.Count || state.Cells[cellIdx].Answered)
            {
                await RespondAsync(embed: embeds.Warning("Опоздание!", "На этот вопрос уже успел ответить другой участник!"), ephemeral: true);
                return;
            }

            var cell = state.Cells[cellIdx];
            var userAns = modal.Answer.Trim().ToLowerInvariant();
            var expectedVariants = cell.Answer.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var isCorrect = expectedVariants.Any(v => userAns.Contains(v, StringComparison.OrdinalIgnoreCase));

            var me = players[pIdx];
            if (isCorrect)
            {
                cell.Answered = true;
                state.ActiveCellIndex = null;
                players[pIdx] = me with { Score = me.Score + cell.Points };
                state.LastLog = $"✅ {me.DisplayName} дал верный ответ («{modal.Answer.Trim()}») и получил +{cell.Points} очк.!";

                await partyRepo.SaveFullGameStateAsync(
                    session.Id, session.Status, "select_cell", session.RoundNum + 1, me.UserId,
                    JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt, players);

                await RespondAsync(embed: embeds.Success("Верно!", $"Вы заработали **+{cell.Points}** очков! Теперь ваш счёт: **{players[pIdx].Score}**."), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
            }
            else
            {
                var penalty = cell.Points / 2;
                players[pIdx] = me with { Score = me.Score - penalty };
                state.LastLog = $"❌ {me.DisplayName} ошибся («{modal.Answer.Trim()}», -{penalty} очк.). Вопрос всё ещё открыт!";

                await partyRepo.SaveFullGameStateAsync(
                    session.Id, session.Status, session.Phase, session.RoundNum, session.TurnUserId,
                    JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt, players);

                await RespondAsync(embed: embeds.Error($"Неверный ответ! Списано -{penalty} очков. Другие игроки могут попробовать ответить."), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("quiz:skip:*", true)]
    public async Task HandleQuizSkipQuestionAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            await DeferAsync();
            var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();
            if (state.ActiveCellIndex.HasValue && state.ActiveCellIndex.Value < state.Cells.Count)
            {
                var cell = state.Cells[state.ActiveCellIndex.Value];
                cell.Answered = true;
                var rightAns = cell.Answer.Split('|').FirstOrDefault() ?? cell.Answer;
                state.LastLog = $"⏭️ Вопрос пропущен. Правильный ответ был: «{rightAns}».";
                state.ActiveCellIndex = null;
            }

            var players = await partyRepo.GetPlayersAsync(sessionId);
            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, "select_cell", session.RoundNum + 1, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            await RefreshGameTableMessageAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("quiz:finish:*", true)]
    public async Task HandleQuizFinishAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            if (Context.User.Id != session.HostId && (Context.User as SocketGuildUser)?.GuildPermissions.Administrator != true)
            {
                await RespondAsync(embed: embeds.Error("Подвести финальный итог может только ведущий стола."), ephemeral: true);
                return;
            }

            await DeferAsync();
            var players = await partyRepo.GetPlayersAsync(sessionId);
            var maxScore = players.Max(x => x.Score);
            var winners = players.Where(x => x.Score == maxScore).Select(x => x.UserId).ToList();
            var summary = $"🏆 **Матч Своей Игры завершён!** Победитель с результатом **{maxScore} очк.**: {string.Join(", ", winners.Select(id => $"<@{id}>"))}!";

            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id, winners, baseBonusPerWinner: 350, session.StateJson, summary);

            var finSession = (await partyRepo.GetSessionByIdAsync(sessionId))!;
            await RefreshGameTableMessageAsync(finSession, players);
            await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "quiz", players, winners, Context.Channel);
        }
        finally
        {
            sem.Release();
        }
    }

    // =========================================================================
    // 6. CODENAMES INTERACTIONS
    // =========================================================================

    private static (Embed Embed, MessageComponent? Components) BuildCodenamesMessage(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol)
    {
        var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
        var isFinished = session.Status == "finished";
        var teamName = state.CurrentTeam == "red" ? "🔴 КРАСНАЯ КОМАНДА" : "🔵 СИНЯЯ КОМАНДА";
        var capId = state.CurrentTeam == "red" ? state.RedCaptainId : state.BlueCaptainId;

        var desc = isFinished
            ? $"{session.WinnerSummary}\n*{state.LastLog}*"
            : $"**Ход:** {teamName} (Капитан: <@{capId}>)\n" +
              (string.IsNullOrWhiteSpace(state.CurrentClue)
                  ? "Ожидание шифровки от капитана..."
                  : $"📡 **Активная шифровка:** `{state.CurrentClue}` (Осталось попыток: `{state.GuessesRemaining}`)") +
              $"\n*{state.LastLog}*";

        var embed = new EmbedBuilder()
            .WithColor(state.CurrentTeam == "red" ? new Color(244, 63, 94) : new Color(56, 189, 248))
            .WithTitle($"🗺️ Кодовые Имена (Codenames) #{session.Id}  •  Банк: {session.PrizePool:N0} {currencySymbol}")
            .WithDescription(desc)
            .WithImageUrl("attachment://game-board.png")
            .Build();

        if (isFinished)
            return (embed, null);

        var cb = new ComponentBuilder()
            .WithButton("Карта Капитана", $"cn:captain:{session.Id}", ButtonStyle.Secondary, new Emoji("🗺️"))
            .WithButton("Дать шифровку (Капитан)", $"cn:clue_btn:{session.Id}", ButtonStyle.Primary, new Emoji("📡"))
            .WithButton("Выбрать слово на поле", $"cn:guess_btn:{session.Id}", ButtonStyle.Success, new Emoji("🎯"))
            .WithButton("Завершить ход команды", $"cn:end_turn:{session.Id}", ButtonStyle.Secondary, new Emoji("⏭️"))
            .WithButton("Отмена", $"pg:cancel:{session.Id}", ButtonStyle.Danger, new Emoji("✖️"));

        return (embed, cb.Build());
    }

    [ComponentInteraction("cn:captain:*", true)]
    public async Task HandleCodenamesCaptainMapAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
        if (Context.User.Id != state.RedCaptainId && Context.User.Id != state.BlueCaptainId)
        {
            await RespondAsync(embed: embeds.Error("Открыть секретную карту цветов могут только Капитаны команд!"), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var png = await partyService.RenderCodenamesBoardAsync(session, showCaptainKeymap: true);
        await using var stream = new MemoryStream(png);

        var embed = new EmbedBuilder()
            .WithColor(new Color(245, 158, 11))
            .WithTitle("🗺️ Секретная матрица агентов (Только для Капитанов)")
            .WithDescription("🔴 Красные — агенты Красных | 🔵 Синие — агенты Синих | ⚪ Серые — мирные жители | ⚫ Чёрная клетка с золотой рамкой — **УБИЙЦА (мгновенное поражение при открытии)**!")
            .WithImageUrl("attachment://captain-map.png")
            .Build();

        await FollowupWithFileAsync(stream, "captain-map.png", embed: embed, ephemeral: true);
    }

    [ComponentInteraction("cn:clue_btn:*", true)]
    public async Task HandleCodenamesClueButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
        var activeCap = state.CurrentTeam == "red" ? state.RedCaptainId : state.BlueCaptainId;
        if (Context.User.Id != activeCap)
        {
            await RespondAsync(embed: embeds.Error($"Сейчас шифровку даёт капитан активной команды (<@{activeCap}>)."), ephemeral: true);
            return;
        }

        await RespondWithModalAsync<CodenamesClueModal>($"cn:clue_modal:{sessionId}");
    }

    [ModalInteraction("cn:clue_modal:*", true)]
    public async Task HandleCodenamesClueModalAsync(string sessionIdStr, CodenamesClueModal modal)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
            var count = int.TryParse(modal.ClueCount.Trim(), out var c) ? Math.Clamp(c, 1, 9) : 1;
            var word = modal.ClueWord.Trim().ToUpperInvariant();

            state.CurrentClue = $"{word} — {count}";
            state.GuessesRemaining = count + 1;
            state.LastLog = $"📡 Капитан передал шифровку: «{state.CurrentClue}»! Оперативники выбирают слова.";

            var players = await partyRepo.GetPlayersAsync(sessionId);
            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, "guess", session.RoundNum, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            await RespondAsync(embed: embeds.Success("Шифровка опубликована!", $"Ваша команда получила подсказку: **{state.CurrentClue}**."), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("cn:guess_btn:*", true)]
    public async Task HandleCodenamesGuessButtonAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var session = await partyRepo.GetSessionByIdAsync(sessionId);
        if (session is null || session.Status != "active")
            return;

        var players = await partyRepo.GetPlayersAsync(sessionId);
        var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
        var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
        if (me is null || me.TeamCode != state.CurrentTeam)
        {
            await RespondAsync(embed: embeds.Error("Сейчас ход противоположной команды!"), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"cn:guess_select:{sessionId}")
            .WithPlaceholder("🎯 Выберите кодовое слово на поле...");

        for (var i = 0; i < state.Grid.Count; i++)
        {
            if (!state.Grid[i].Revealed)
                menu.AddOption($"#{i + 1} {state.Grid[i].Word}", i.ToString());
        }

        await RespondAsync(embed: embeds.Info("Выбор слова на поле", "Нажмите на слово, которое загадал ваш капитан:"), components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    [ComponentInteraction("cn:guess_select:*", true)]
    public async Task HandleCodenamesGuessSelectAsync(string sessionIdStr, string[] values)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId) || values.Length == 0 || !int.TryParse(values[0], out var cellIdx))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            var players = await partyRepo.GetPlayersAsync(sessionId);
            var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
            var me = players.FirstOrDefault(x => x.UserId == Context.User.Id);
            if (me is null || me.TeamCode != state.CurrentTeam)
            {
                await RespondAsync(embed: embeds.Error("Сейчас ход другой команды."), ephemeral: true);
                return;
            }

            if (cellIdx < 0 || cellIdx >= state.Grid.Count || state.Grid[cellIdx].Revealed)
                return;

            var cell = state.Grid[cellIdx];
            cell.Revealed = true;

            string? winTeam = null;
            string winReason = string.Empty;

            if (cell.Color == "assassin")
            {
                winTeam = state.CurrentTeam == "red" ? "blue" : "red";
                winReason = $"💀 **{me.DisplayName}** открыл чёрную метку **УБИЙЦЫ («{cell.Word}»)**! Победа переходит команде соперников!";
            }
            else if (cell.Color == state.CurrentTeam)
            {
                state.GuessesRemaining = Math.Max(0, state.GuessesRemaining - 1);
                state.LastLog = $"✅ **{me.DisplayName}** открыл своего агента **«{cell.Word}»**!";
                if (state.GuessesRemaining == 0)
                {
                    state.CurrentTeam = state.CurrentTeam == "red" ? "blue" : "red";
                    state.CurrentClue = string.Empty;
                }
            }
            else
            {
                var otherTeam = state.CurrentTeam == "red" ? "blue" : "red";
                state.LastLog = cell.Color == "neutral"
                    ? $"⚪ **{me.DisplayName}** попал в мирного жителя **«{cell.Word}»**! Ход переходит соперникам."
                    : $"🚨 **{me.DisplayName}** открыл чужого агента **«{cell.Word}»**! Ход переходит соперникам.";
                state.CurrentTeam = otherTeam;
                state.CurrentClue = string.Empty;
                state.GuessesRemaining = 0;
            }

            if (winTeam is null)
            {
                if (state.Grid.Where(x => x.Color == "red").All(x => x.Revealed))
                {
                    winTeam = "red";
                    winReason = "🏆 **КРАСНАЯ КОМАНДА РАСКРЫЛА ВСЕХ СВОИХ АГЕНТОВ!**";
                }
                else if (state.Grid.Where(x => x.Color == "blue").All(x => x.Revealed))
                {
                    winTeam = "blue";
                    winReason = "🏆 **СИНЯЯ КОМАНДА РАСКРЫЛА ВСЕХ СВОИХ АГЕНТОВ!**";
                }
            }

            if (winTeam is not null)
            {
                state.LastLog = winReason;
                var winners = players.Where(x => x.TeamCode == winTeam).Select(x => x.UserId).ToList();
                await partyRepo.FinishSessionAndDistributePrizeAsync(
                    session.Id, winners, baseBonusPerWinner: 350, JsonSerializer.Serialize(state), winReason);

                await RespondAsync(embed: embeds.Success("Матч завершён!", winReason), ephemeral: true);
                await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
                await partyService.RewardWinnersProgressionAsync(Context.Guild, Context.Guild.Id, "codenames", players, winners, Context.Channel);
                return;
            }

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, session.Phase, session.RoundNum + 1, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            await RespondAsync(embed: embeds.Info("Клетка открыта", state.LastLog), ephemeral: true);
            await UpdateChannelBoardMessageByIdAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    [ComponentInteraction("cn:end_turn:*", true)]
    public async Task HandleCodenamesEndTurnAsync(string sessionIdStr)
    {
        if (!long.TryParse(sessionIdStr, out var sessionId))
            return;

        var sem = partyService.GetSessionLock(sessionId);
        await sem.WaitAsync();
        try
        {
            var session = await partyRepo.GetSessionByIdAsync(sessionId);
            if (session is null || session.Status != "active")
                return;

            await DeferAsync();
            var players = await partyRepo.GetPlayersAsync(sessionId);
            var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
            state.CurrentTeam = state.CurrentTeam == "red" ? "blue" : "red";
            state.CurrentClue = string.Empty;
            state.GuessesRemaining = 0;
            state.LastLog = $"⏭️ Команда завершила догадки. Теперь ход {(state.CurrentTeam == "red" ? "КРАСНЫХ" : "СИНИХ")}!";

            await partyRepo.SaveFullGameStateAsync(
                session.Id, session.Status, "clue", session.RoundNum + 1, session.TurnUserId,
                JsonSerializer.Serialize(state), session.MutedUserIds, session.WinnerSummary, session.ExpiresAt);

            await RefreshGameTableMessageAsync((await partyRepo.GetSessionByIdAsync(sessionId))!, players);
        }
        finally
        {
            sem.Release();
        }
    }

    // =========================================================================
    // HELPER: UPDATE PUBLIC BOARD MESSAGE FROM EPHEMERAL INTERACTIONS
    // =========================================================================

    private async Task UpdateChannelBoardMessageByIdAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players)
    {
        if (session.MessageId == 0 || Context.Channel is not IMessageChannel channel)
            return;

        var cfg = await settingsRepo.GetOrCreateAsync(Context.Guild.Id);
        byte[] png;
        Embed embed;
        MessageComponent? comp;

        switch (session.GameType)
        {
            case "mafia":
                png = await partyService.RenderMafiaTableAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildMafiaMessage(session, players, cfg.CurrencySymbol);
                break;
            case "bunker":
                png = await partyService.RenderBunkerBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildBunkerMessage(session, players, cfg.CurrencySymbol);
                break;
            case "spyfall":
                png = await partyService.RenderSpyfallBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildSpyfallMessage(session, players, cfg.CurrencySymbol);
                break;
            case "liarsbar":
                png = await partyService.RenderLiarsBarTableAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildLiarsBarMessage(session, players, cfg.CurrencySymbol);
                break;
            case "quiz":
                png = await partyService.RenderQuizBoardAsync(session, players, cfg.CurrencySymbol);
                (embed, comp) = BuildQuizMessage(session, players, cfg.CurrencySymbol);
                break;
            case "codenames":
                png = await partyService.RenderCodenamesBoardAsync(session, showCaptainKeymap: false);
                (embed, comp) = BuildCodenamesMessage(session, players, cfg.CurrencySymbol);
                break;
            default:
                return;
        }

        try
        {
            await using var stream = new MemoryStream(png);
            await channel.ModifyMessageAsync(session.MessageId, m =>
            {
                m.Embed = embed;
                m.Attachments = new List<FileAttachment> { new(stream, "game-board.png") };
                m.Components = comp;
            });
        }
        catch
        {
        }
    }
}
