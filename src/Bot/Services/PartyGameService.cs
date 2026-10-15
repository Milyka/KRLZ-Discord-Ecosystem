using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Bot.Data;
using Bot.Infrastructure;
using Bot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Bot.Services;

public sealed class PartyGameService(
    PartyGameRepository partyRepo,
    UserRepository userRepo,
    SocialExpansionRepository socialExpRepo,
    ItemRepository itemRepo,
    ClanRepository clanRepo,
    AchievementService achievementService,
    ProgressionService progressionService,
    ProfileCardService profileCardService,
    ILogger<PartyGameService> logger)
{
    private const int CanvasWidth = 1024;
    private const int CanvasHeight = 600;

    private static readonly SKTypeface FontRegular = LoadTypeface("Inter-Regular.ttf", SKFontStyle.Normal);
    private static readonly SKTypeface FontBold = LoadTypeface("Inter-Bold.ttf", SKFontStyle.Bold);

    private static readonly SKColor BgDark = new(13, 17, 28);
    private static readonly SKColor GlassPanel = new(20, 27, 45, 215);
    private static readonly SKColor GlassStroke = new(255, 255, 255, 28);
    private static readonly SKColor CyanAccent = new(56, 189, 248);
    private static readonly SKColor GoldAccent = new(245, 158, 11);
    private static readonly SKColor CrimsonAccent = new(244, 63, 94);
    private static readonly SKColor EmeraldAccent = new(16, 185, 129);
    private static readonly SKColor IndigoAccent = new(99, 102, 241);
    private static readonly SKColor MutedText = new(148, 163, 184);

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _sessionLocks = new();

    private static SKTypeface LoadTypeface(string fileName, SKFontStyle style)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", fileName);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName(null, style);
    }

    public SemaphoreSlim GetSessionLock(long sessionId) =>
        _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public static int NextInt(int minInclusive, int maxExclusive) =>
        RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);

    public static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public static (int MinPlayers, int MaxPlayers, string Title, string Subtitle) GetGameMeta(string gameType) => gameType switch
    {
        "mafia" => (4, 12, "МАФИЯ KRLZ", "Голосовая и текстовая психологическая игра со скрытыми ночными ходами"),
        "bunker" => (3, 10, "БУНКЕР: КАТАСТРОФА", "Социальная дедукция на выживание с досье и спецкартами"),
        "spyfall" => (3, 10, "ШПИОН (SPYFALL)", "Вычислите агента под прикрытием раньше, чем он разгадает локацию"),
        "liarsbar" => (2, 4, "БАР ЛЖЕЦОВ (LIAR'S BAR)", "Карточный блеф на вылет с русской рулеткой"),
        "quiz" => (2, 8, "СВОЯ ИГРА: БРЕЙН-РИНГ", "Интеллектуальное табло на скорость реакции и эрудицию"),
        "codenames" => (4, 10, "КОДОВЫЕ ИМЕНА (5x5)", "Командная игра капитанов разведки и оперативников"),
        _ => (2, 10, "КОМПАНЕЙСКАЯ ИГРА KRLZ", "Интерактивный стол сообщества")
    };

    // =========================================================================
    // VOICE MUTE SYNCHRONIZATION & CRASH RECOVERY
    // =========================================================================

    public async Task<string> SyncMafiaVoiceMutesAsync(
        SocketGuild? guild,
        ulong voiceChannelId,
        string phase,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string previousMutedIds)
    {
        if (guild is null || voiceChannelId == 0)
            return string.Empty;

        var voiceChannel = guild.GetVoiceChannel(voiceChannelId);
        if (voiceChannel is null)
            return string.Empty;

        var currentlyMuted = new HashSet<ulong>();
        if (!string.IsNullOrWhiteSpace(previousMutedIds))
        {
            foreach (var part in previousMutedIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ulong.TryParse(part, out var id))
                    currentlyMuted.Add(id);
            }
        }

        var nextMuted = new HashSet<ulong>();

        foreach (var p in players)
        {
            var member = voiceChannel.ConnectedUsers.FirstOrDefault(u => u.Id == p.UserId) ?? guild.GetUser(p.UserId);
            if (member is null || member.VoiceChannel?.Id != voiceChannelId)
                continue;

            var shouldMute = phase == "night" || !p.IsAlive;
            if (phase == "finished" || phase == "lobby")
                shouldMute = false;

            try
            {
                if (shouldMute)
                {
                    if (!member.IsMuted)
                        await member.ModifyAsync(x => x.Mute = true);
                    nextMuted.Add(p.UserId);
                }
                else if (currentlyMuted.Contains(p.UserId) || member.IsMuted)
                {
                    await member.ModifyAsync(x => x.Mute = false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Не удалось переключить серверный мут для {UserId} в канале {VoiceId}", p.UserId, voiceChannelId);
            }
        }

        return string.Join(",", nextMuted);
    }

    public async Task UnmuteSessionUsersAsync(SocketGuild? guild, ulong voiceChannelId, string mutedUserIds, IEnumerable<ulong>? extraUserIds = null)
    {
        if (guild is null || voiceChannelId == 0)
            return;

        var ids = new HashSet<ulong>();
        if (!string.IsNullOrWhiteSpace(mutedUserIds))
        {
            foreach (var part in mutedUserIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ulong.TryParse(part, out var uid))
                    ids.Add(uid);
            }
        }

        if (extraUserIds is not null)
        {
            foreach (var uid in extraUserIds)
                ids.Add(uid);
        }

        foreach (var uid in ids)
        {
            var member = guild.GetUser(uid);
            if (member is not null && member.IsMuted)
            {
                try
                {
                    await member.ModifyAsync(x => x.Mute = false);
                }
                catch
                {
                }
            }
        }
    }

    public async Task RestoreInterruptedPartyGamesOnStartupAsync(DiscordSocketClient client, CancellationToken ct = default)
    {
        var targets = await partyRepo.RefundInterruptedPartySessionsOnStartupAsync(ct);
        if (targets.Count == 0)
            return;

        logger.LogInformation("Возвращены ставки по {Count} прерванным компанейским играм после перезапуска", targets.Count);

        foreach (var (guildId, voiceId, mutedIds) in targets)
        {
            var guild = client.GetGuild(guildId);
            await UnmuteSessionUsersAsync(guild, voiceId, mutedIds);
        }
    }

    public async Task RewardWinnersProgressionAsync(
        SocketGuild? guild,
        ulong guildId,
        string gameType,
        IReadOnlyList<PartyGamePlayerEntity> allPlayers,
        IReadOnlyCollection<ulong> winnerIds,
        ISocketMessageChannel? channel = null,
        CancellationToken ct = default)
    {
        foreach (var p in allPlayers)
        {
            await userRepo.AddMessageProgressAsync(guildId, p.UserId, 60, 0, ct);
            await progressionService.TrackActivityAsync(guildId, p.UserId, "party_games", 1, 55, ct);
            await clanRepo.AddActivityPointsAsync(guildId, p.UserId, 8, ct);
        }

        if (guild is null)
            return;

        var achEvent = gameType switch
        {
            "mafia" => "mafia_wins",
            "bunker" => "bunker_wins",
            "spyfall" => "spyfall_wins",
            "liarsbar" => "liarsbar_wins",
            _ => "party_wins"
        };

        foreach (var wId in winnerIds)
        {
            await achievementService.TrackEventAsync(guild, wId, achEvent, 1, false, channel, ct);
            if (achEvent != "party_wins")
                await achievementService.TrackEventAsync(guild, wId, "party_wins", 1, false, channel, ct);
        }
    }

    // =========================================================================
    // 1. MAFIA ENGINE (/mafia)
    // =========================================================================

    public static string FormatMafiaRoleName(string roleCode) => roleCode switch
    {
        "don" => "🎩 Дон Мафии",
        "mafia" => "🔪 Мафия",
        "commissar" => "🕵️ Комиссар Каттани",
        "doctor" => "🩺 Доктор",
        "mistress" => "💋 Любовница",
        "maniac" => "🪓 Маньяк-одиночка",
        _ => "🏙️ Мирный житель"
    };

    public static string FormatMafiaRoleDescription(string roleCode) => roleCode switch
    {
        "don" => "Глава синдиката. Ночью выбирает жертву вместе с мафией и дополнительно проверяет одного жителя на роль Комиссара.",
        "mafia" => "Член криминальной семьи. Ночью голосует за устранение жителя города. Знает своих напарников в лицо.",
        "commissar" => "Главный детектив города. Ночью может проверить досье любого игрока ИЛИ произвести выстрел на поражение.",
        "doctor" => "Врач скорой помощи. Ночью выезжает на вызов и спасает выбранного игрока от смертельного покушения.",
        "mistress" => "Ночная гостья. Блокирует ночную способность выбранного игрока и даёт ему алиби на дневном голосовании.",
        "maniac" => "Хладнокровный нейтрал. Играет сам за себя и каждую ночь устраняет одну цель. Побеждает, если остаётся один.",
        _ => "Честный горожанин. Ваше оружие — логика, интуиция и дневное голосование за изгнание подозреваемых."
    };

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartMafiaGameAsync(
        SocketGuild? guild,
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var count = players.Count;
        var rolePool = new List<string>();

        if (count <= 5)
        {
            rolePool.AddRange(["mafia", "commissar", "doctor"]);
        }
        else if (count <= 7)
        {
            rolePool.AddRange(["don", "mafia", "commissar", "doctor"]);
        }
        else if (count <= 9)
        {
            rolePool.AddRange(["don", "mafia", "commissar", "doctor", "mistress", "maniac"]);
        }
        else
        {
            rolePool.AddRange(["don", "mafia", "mafia", "commissar", "doctor", "mistress", "maniac"]);
        }

        while (rolePool.Count < count)
            rolePool.Add("citizen");

        Shuffle(rolePool);

        var updatedPlayers = new List<PartyGamePlayerEntity>(count);
        for (var i = 0; i < count; i++)
        {
            var r = rolePool[i];
            var team = r is "don" or "mafia" ? "mafia" : r == "maniac" ? "maniac" : "town";
            updatedPlayers.Add(players[i] with
            {
                SeatIndex = i + 1,
                RoleCode = r,
                TeamCode = team,
                IsAlive = true
            });
        }

        var state = new MafiaGameStateDto
        {
            LastEventBanner = "Ночь 1: Город засыпает. Роли розданы! Нажмите «Моя роль и ход», чтобы сделать ночной выбор."
        };
        state.ChronicleLogs.Add($"Партия началась ({count} уч.). Наступила Ночь 1.");

        var mutedIds = await SyncMafiaVoiceMutesAsync(guild, session.VoiceChannelId, "night", updatedPlayers, session.MutedUserIds);
        var stateJson = JsonSerializer.Serialize(state);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "night",
            1,
            session.HostId,
            stateJson,
            mutedIds,
            string.Empty,
            expiresAt,
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    public static bool HaveAllMafiaNightRolesActed(MafiaGameStateDto state, IReadOnlyList<PartyGamePlayerEntity> players)
    {
        foreach (var p in players.Where(x => x.IsAlive))
        {
            switch (p.RoleCode)
            {
                case "mafia":
                case "don":
                    if (!state.NightKillVotes.ContainsKey(p.UserId))
                        return false;
                    break;
                case "commissar":
                    if (!state.CommissarTargetId.HasValue)
                        return false;
                    break;
                case "doctor":
                    if (!state.DoctorTargetId.HasValue)
                        return false;
                    break;
                case "mistress":
                    if (!state.MistressTargetId.HasValue)
                        return false;
                    break;
                case "maniac":
                    if (!state.ManiacTargetId.HasValue)
                        return false;
                    break;
            }
        }

        return true;
    }

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players, bool Finished, List<ulong> Winners)> ResolveMafiaNightAsync(
        SocketGuild? guild,
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var aliveMap = players.Where(x => x.IsAlive).ToDictionary(x => x.UserId);
        var blockedUserId = state.MistressTargetId;

        var events = new List<string>();
        var killedIds = new HashSet<ulong>();

        if (blockedUserId.HasValue && aliveMap.TryGetValue(blockedUserId.Value, out var visitedPlayer))
        {
            events.Add($"💋 Любовница провела ночь у **{visitedPlayer.DisplayName}**, заблокировав его действия.");
        }

        // Doctor heal
        ulong? healedUserId = null;
        var doctor = players.FirstOrDefault(x => x.IsAlive && x.RoleCode == "doctor");
        if (doctor is not null && doctor.UserId != blockedUserId && state.DoctorTargetId.HasValue)
        {
            healedUserId = state.DoctorTargetId.Value;
        }

        // Mafia kill target
        var activeMafiaVotes = state.NightKillVotes
            .Where(kv => aliveMap.TryGetValue(kv.Key, out var m) && m.TeamCode == "mafia" && kv.Key != blockedUserId)
            .ToList();

        if (activeMafiaVotes.Count > 0)
        {
            var don = players.FirstOrDefault(x => x.IsAlive && x.RoleCode == "don" && x.UserId != blockedUserId);
            ulong mafiaTarget;
            if (don is not null && state.NightKillVotes.TryGetValue(don.UserId, out var donVote))
            {
                mafiaTarget = donVote;
            }
            else
            {
                mafiaTarget = activeMafiaVotes
                    .GroupBy(x => x.Value)
                    .OrderByDescending(g => g.Count())
                    .First().Key;
            }

            if (aliveMap.TryGetValue(mafiaTarget, out var victim))
            {
                if (healedUserId == mafiaTarget)
                    events.Add($"🩺 Мафия покушалась на **{victim.DisplayName}**, но Доктор вовремя спас жизнь!");
                else
                {
                    killedIds.Add(mafiaTarget);
                    events.Add($"🔪 Выстрелом Мафии был устранен **{victim.DisplayName}** ({FormatMafiaRoleName(victim.RoleCode)}).");
                }
            }
        }

        // Commissar shoot
        var commissar = players.FirstOrDefault(x => x.IsAlive && x.RoleCode == "commissar");
        if (commissar is not null && commissar.UserId != blockedUserId && state.CommissarTargetId.HasValue)
        {
            var targetId = state.CommissarTargetId.Value;
            if (state.CommissarMode == "shoot" && aliveMap.TryGetValue(targetId, out var shotVictim))
            {
                if (healedUserId == targetId)
                    events.Add($"🩺 Комиссар стрелял в **{shotVictim.DisplayName}**, но Доктор перевязал рану!");
                else
                {
                    killedIds.Add(targetId);
                    events.Add($"🕵️ Комиссар застрелил подозреваемого **{shotVictim.DisplayName}** ({FormatMafiaRoleName(shotVictim.RoleCode)}).");
                }
            }
            else
            {
                events.Add("🕵️ Комиссар изучил секретное досье одного из жителей.");
            }
        }

        // Maniac attack
        var maniac = players.FirstOrDefault(x => x.IsAlive && x.RoleCode == "maniac");
        if (maniac is not null && maniac.UserId != blockedUserId && state.ManiacTargetId.HasValue)
        {
            var mTarget = state.ManiacTargetId.Value;
            if (aliveMap.TryGetValue(mTarget, out var mVictim))
            {
                if (healedUserId == mTarget)
                    events.Add($"🩺 Маньяк напал на **{mVictim.DisplayName}**, но Доктор спас жертву!");
                else
                {
                    killedIds.Add(mTarget);
                    events.Add($"🪓 От руки Маньяка пал **{mVictim.DisplayName}** ({FormatMafiaRoleName(mVictim.RoleCode)}).");
                }
            }
        }

        if (events.Count == 0)
            events.Add("🌅 Ночь прошла удивительно тихо — никто не пострадал.");

        var updatedPlayers = players
            .Select(p => killedIds.Contains(p.UserId) ? p with { IsAlive = false } : p)
            .ToList();

        state.NightKillVotes.Clear();
        state.DonCheckTargetId = null;
        state.CommissarTargetId = null;
        state.CommissarMode = "check";
        state.DoctorTargetId = null;
        state.ManiacTargetId = null;
        state.DidVotesClear(state);
        state.LastEventBanner = string.Join(" ", events);
        state.ChronicleLogs.Add($"Итоги ночи {session.RoundNum}: {state.LastEventBanner}");

        var (isOver, winnerTeam, winnerTitle, winners) = CheckMafiaVictory(updatedPlayers);
        if (isOver)
        {
            await UnmuteSessionUsersAsync(guild, session.VoiceChannelId, session.MutedUserIds, updatedPlayers.Select(x => x.UserId));
            var finalJson = JsonSerializer.Serialize(state);
            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id,
                winners,
                baseBonusPerWinner: 350,
                finalJson,
                winnerTitle,
                updatedPlayers,
                ct);

            return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), true, winners);
        }

        var mutedIds = await SyncMafiaVoiceMutesAsync(guild, session.VoiceChannelId, "day_vote", updatedPlayers, session.MutedUserIds);
        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "day_vote",
            session.RoundNum,
            session.HostId,
            JsonSerializer.Serialize(state),
            mutedIds,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(25),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), false, []);
    }

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players, bool Finished, List<ulong> Winners)> ResolveMafiaDayVoteAsync(
        SocketGuild? guild,
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var aliveIds = players.Where(x => x.IsAlive).Select(x => x.UserId).ToHashSet();

        var validVotes = state.DayVotes
            .Where(kv => aliveIds.Contains(kv.Key) && (kv.Value == 0 || aliveIds.Contains(kv.Value)))
            .ToList();

        var updatedPlayers = players.ToList();
        string voteSummary;

        var candidateGroups = validVotes
            .Where(kv => kv.Value != 0)
            .GroupBy(kv => kv.Value)
            .Select(g => new { TargetId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var skipCount = validVotes.Count(kv => kv.Value == 0);

        if (candidateGroups.Count == 0 || (candidateGroups.Count > 1 && candidateGroups[0].Count == candidateGroups[1].Count) || candidateGroups[0].Count <= skipCount)
        {
            voteSummary = "⚖️ Город не пришёл к единому мнению на голосовании. Сегодня никто не казнён.";
        }
        else
        {
            var exiledId = candidateGroups[0].TargetId;
            var idx = updatedPlayers.FindIndex(x => x.UserId == exiledId);
            if (idx >= 0)
            {
                var target = updatedPlayers[idx];
                if (state.MistressTargetId == exiledId)
                {
                    voteSummary = $"💋 Город голосовал против **{target.DisplayName}**, но Любовница предоставила ему железное алиби!";
                }
                else
                {
                    updatedPlayers[idx] = target with { IsAlive = false };
                    voteSummary = $"⚖️ По решению большинства ({candidateGroups[0].Count} гол.) изгнан **{target.DisplayName}** ({FormatMafiaRoleName(target.RoleCode)}).";
                }
            }
            else
            {
                voteSummary = "⚖️ Голосование завершено без изгнания.";
            }
        }

        state.DayVotes.Clear();
        state.MistressTargetId = null;
        state.LastEventBanner = voteSummary;
        state.ChronicleLogs.Add($"День {session.RoundNum}: {voteSummary}");

        var (isOver, _, winnerTitle, winners) = CheckMafiaVictory(updatedPlayers);
        if (isOver)
        {
            await UnmuteSessionUsersAsync(guild, session.VoiceChannelId, session.MutedUserIds, updatedPlayers.Select(x => x.UserId));
            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id,
                winners,
                baseBonusPerWinner: 350,
                JsonSerializer.Serialize(state),
                winnerTitle,
                updatedPlayers,
                ct);

            return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), true, winners);
        }

        var nextRound = session.RoundNum + 1;
        var mutedIds = await SyncMafiaVoiceMutesAsync(guild, session.VoiceChannelId, "night", updatedPlayers, session.MutedUserIds);

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "night",
            nextRound,
            session.HostId,
            JsonSerializer.Serialize(state),
            mutedIds,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(25),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), false, []);
    }

    public static (bool IsOver, string WinnerTeam, string Summary, List<ulong> WinnerUserIds) CheckMafiaVictory(
        IReadOnlyList<PartyGamePlayerEntity> players)
    {
        var alive = players.Where(x => x.IsAlive).ToList();
        var mafiaAlive = alive.Count(x => x.TeamCode == "mafia");
        var maniacAlive = alive.Count(x => x.TeamCode == "maniac");
        var townAlive = alive.Count(x => x.TeamCode == "town");

        if (mafiaAlive == 0 && maniacAlive == 0)
        {
            var winners = players.Where(x => x.TeamCode == "town").Select(x => x.UserId).ToList();
            return (true, "town", "🏆 ПОБЕДА МИРНЫХ ЖИТЕЛЕЙ! Вся преступность в городе ликвидирована!", winners);
        }

        if (maniacAlive > 0 && mafiaAlive == 0 && townAlive <= 1)
        {
            var winners = players.Where(x => x.TeamCode == "maniac").Select(x => x.UserId).ToList();
            return (true, "maniac", "🪓 ПОБЕДА МАНЬЯКА! Город погрузился во тьму одиночного охотника!", winners);
        }

        if (maniacAlive == 0 && mafiaAlive >= townAlive)
        {
            var winners = players.Where(x => x.TeamCode == "mafia").Select(x => x.UserId).ToList();
            return (true, "mafia", "🎩 ПОБЕДА МАФИИ! Синдикат полностью захватил власть над городом KRLZ!", winners);
        }

        return (false, string.Empty, string.Empty, []);
    }

    // =========================================================================
    // 2. BUNKER ENGINE (/bunker)
    // =========================================================================

    private static readonly (string Title, string Desc, string BunkerDesc)[] DefaultDisasters =
    [
        ("Ядерная зима «Пепел-2032»", "Температура на планете упала до -55°C, атмосфера насыщена радиоактивными осадками на 12 лет.", "Бункер №7 (240 м²): есть гидропонная ферма и ядерный реактор, но повреждена система очистки воздуха и не хватает медикаментов."),
        ("Пандемия нейро-грибка «Кордицепс-Х»", "94% населения заражено спорами, превращающими людей в агрессивных мутантов.", "Правительственный бункер в горах Алматы: есть био-лаборатория и оружейная, но запасы провизии рассчитаны только на 2 года без агронома."),
        ("Падение астероида «Апофис-9»", "Ударная волна вызвала мега-цунами и тектонические разломы по всей Евразии.", "Подземный комплекс метрополитена: просторные залы и склад консервов, но затоплен генераторный отсек и нарушена герметичность шлюза."),
        ("Восстание ИИ «Сингулярность»", "Автономные боевые дроны контролируют поверхность и отслеживают любые радиосигналы.", "Экранированный бункер времён Холодной войны: полностью аналоговая техника, кузница и скважина с водой, но нет хирургического блока."),
        ("Супервулкан Йеллоустоун", "Небо закрыто плотным слоем вулканического пепла, идут кислотные дожди.", "Арктическое хранилище семян: богатейший генофонд растений и солнечные батареи, но критический износ теплообменников.")
    ];

    private static readonly (string Value, int Score)[] ProfessionsPool =
    [
        ("Врач-хирург высшей категории", 35), ("Инженер-ядерщик", 34), ("Агроном-генетик", 32),
        ("Военный сапёр-инженер", 28), ("Биохимик-вирусолог", 33), ("Электрик-ремонтник", 27),
        ("Полевой медик-фельдшер", 26), ("Шеф-повар полевой кухни", 22), ("Офицер спецназа ГРУ", 25),
        ("Психотерапевт-кризисолог", 20), ("Архитектор подземных сооружений", 24), ("Слесарь-сантехник 6 разряда", 23),
        ("Программист нейросетей", 16), ("Учитель химии и биологии", 19), ("Ветеринар", 18),
        ("Фермер-животновод", 24), ("Стример онлайн-казино", -8), ("Профессиональный таролог", -12),
        ("Крипто-инвестор в мемкоины", -10), ("Блогер-пранкер", -14), ("Депутат горсовета", -6),
        ("Диджей ночного клуба", -4), ("Киберспортсмен по Dota 2", 2), ("Адвокат по разводам", 0)
    ];

    private static readonly (string Value, int Score)[] HealthPool =
    [
        ("Идеально здоров (космонавт)", 25), ("Приобретённый иммунитет к радиации", 28),
        ("Отличное здоровье, донор 1-й группы", 20), ("Лёгкая близорукость (-1.5)", 6),
        ("Аللергия на пыльцу и цитрусовые", 4), ("Хронический гастрит в ремиссии", 0),
        ("Бронхиальная астма (нужен ингалятор)", -12), ("Инсулинозависимый диабет", -16),
        ("Тяжёлая мигрень при стрессе", -8), ("Кибернетический протез правой руки", 12),
        ("Лунатизм и разговоры во сне", -6), ("Пневмония средней тяжести (излечима)", -14),
        ("Бесплодие, но высокий иммунитет", -4), ("Повышенная регенерация тканей", 22)
    ];

    private static readonly (string Value, int Score)[] HobbyPool =
    [
        ("Выращивание овощей на гидропонике", 22), ("Радиолюбитель и пайка микросхем", 20),
        ("Охота и стрельба из арбалета", 18), ("Домашнее консервирование и соленья", 16),
        ("Изготовление лекарств из трав", 19), ("Взлом сейфов и замков", 14),
        ("Ремонт дизельных двигателей", 21), ("Игра на акустической гитаре", 10),
        ("Стендап и подъём боевого духа", 12), ("Коллекционирование крышек от бутылок", -2),
        ("Оригами и лепка из глины", 2), ("Азартные игры на раздевание", -4),
        ("Боевое самбо и ножевой бой", 15), ("Пошив тактической одежды", 14)
    ];

    private static readonly (string Value, int Score)[] BaggagePool =
    [
        ("Хирургический набор и антибиотики", 25), ("Чемодан элитных семян и удобрений", 24),
        ("Счётчик Гейгера, 4 рации и аккумуляторы", 22), ("Ящик армейских сухпайков (60 шт.)", 23),
        ("Складная солнечная панель на 800 Вт", 21), ("Охотничий карабин Сайга + 300 патронов", 20),
        ("Фильтр обратного осмоса для воды", 24), ("Ящик инструментов и сварочный аппарат", 22),
        ("Энциклопедия выживания и чертежи", 15), ("Рыжий кот в переноске (ловит крыс)", 10),
        ("Настольная игра «Мафия» и 3 бутылки рома", 6), ("Надувной розовый фламинго", -5),
        ("Костюм динозавра в полный рост", -4), ("Гиря на 32 кг", 2)
    ];

    private static readonly (string Value, int Score)[] PhobiaPool =
    [
        ("Отсутствует (хладнокровный лидер)", 18), ("Лёгкая арахнофобия (боязнь пауков)", 4),
        ("Боязнь высоты (в бункере не мешает)", 8), ("Перфекционист и чистюля", 10),
        ("Клаустрофобия (паника в тесноте)", -15), ("Никтофобия (страх темноты)", -10),
        ("Пирофобия (страх открытого огня)", -6), ("Клептомания (прячет чужие вещи)", -14),
        ("Вспыльчивость в спорах", -8), ("Альтруист — готов делиться пайком", 14),
        ("Мизантроп, но гениальный исполнитель", 2), ("Душа компании, гасит конфликты", 16)
    ];

    private static readonly (string Code, string Title)[] SpecialCardsPool =
    [
        ("heal_self", "💉 Панацея: Полностью излечить своё заболевание до «Идеально здоров»"),
        ("reroll_job", "🎓 Переквалификация: Заменить свою профессию на элитную специальность бункера"),
        ("upgrade_bag", "🧰 Военный склад: Заменить свой багаж на «Армейский кейс выживания (+25)»"),
        ("spy_reveal", "🔍 Детектор правды: Мгновенно рассекретить Здоровье у случайного соперника"),
        ("calm_mind", "🧠 Психотренинг: Убрать свою фобию и заменить на «Хладнокровный лидер (+18)»")
    ];

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartBunkerGameAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var customDisasters = await partyRepo.GetCustomContentAsync(session.GuildId, "bunker_disaster", ct);
        var combinedDisasters = new List<(string Title, string Desc, string BunkerDesc)>(DefaultDisasters);
        foreach (var cd in customDisasters)
        {
            var bDesc = string.IsNullOrWhiteSpace(cd.PayloadJson) || cd.PayloadJson == "{}"
                ? "Герметичный подземный бункер KRLZ с автономным генератором и закрытым медотсеком."
                : cd.PayloadJson;
            if (!combinedDisasters.Any(x => string.Equals(x.Title, cd.Title, StringComparison.OrdinalIgnoreCase)))
                combinedDisasters.Add((cd.Title, cd.Subtitle, bDesc));
        }

        var chosenDisaster = combinedDisasters[NextInt(0, combinedDisasters.Count)];
        var disTitle = chosenDisaster.Title;
        var disDesc = chosenDisaster.Desc;
        var bunkerDesc = chosenDisaster.BunkerDesc;

        var capacity = Math.Max(1, (players.Count + 1) / 2);
        var state = new BunkerGameStateDto
        {
            DisasterTitle = disTitle,
            DisasterDescription = disDesc,
            BunkerDescription = bunkerDesc,
            BunkerCapacity = capacity,
            EventLogs = [$"Сирена тревоги! В бункере всего {capacity} мест из {players.Count}. Профессии рассекречены!"]
        };

        var jobs = ProfessionsPool.ToList();
        var healths = HealthPool.ToList();
        var hobbies = HobbyPool.ToList();
        var bags = BaggagePool.ToList();
        var phobias = PhobiaPool.ToList();
        Shuffle(jobs);
        Shuffle(healths);
        Shuffle(hobbies);
        Shuffle(bags);
        Shuffle(phobias);

        var updatedPlayers = new List<PartyGamePlayerEntity>(players.Count);
        for (var i = 0; i < players.Count; i++)
        {
            var gender = NextInt(0, 2) == 0 ? "Мужчина" : "Женщина";
            var age = NextInt(19, 64);
            var bioScore = age is >= 21 and <= 42 ? 15 : age <= 52 ? 8 : 0;
            var bioText = $"{gender}, {age} лет ({(age <= 40 ? "репродуктивен" : "опытный")})";

            var job = jobs[i % jobs.Count];
            var hlth = healths[i % healths.Count];
            var hob = hobbies[i % hobbies.Count];
            var bag = bags[i % bags.Count];
            var phob = phobias[i % phobias.Count];
            var spec = SpecialCardsPool[NextInt(0, SpecialCardsPool.Length)];

            var pState = new BunkerPlayerStateDto
            {
                SpecialCardCode = spec.Code,
                SpecialCardTitle = spec.Title,
                SpecialCardUsed = false,
                RevealedThisRound = false,
                Traits =
                [
                    new() { Category = "job", Label = "Профессия", Value = job.Value, SurvivalScore = job.Score, Revealed = true },
                    new() { Category = "bio", Label = "Биология", Value = bioText, SurvivalScore = bioScore, Revealed = false },
                    new() { Category = "health", Label = "Здоровье", Value = hlth.Value, SurvivalScore = hlth.Score, Revealed = false },
                    new() { Category = "hobby", Label = "Хобби", Value = hob.Value, SurvivalScore = hob.Score, Revealed = false },
                    new() { Category = "baggage", Label = "Багаж", Value = bag.Value, SurvivalScore = bag.Score, Revealed = false },
                    new() { Category = "phobia", Label = "Фобия / Черта", Value = phob.Value, SurvivalScore = phob.Score, Revealed = false }
                ]
            };

            updatedPlayers.Add(players[i] with
            {
                SeatIndex = i + 1,
                RoleCode = job.Value,
                TeamCode = "survivor",
                IsAlive = true,
                Score = pState.Traits.Sum(t => t.SurvivalScore),
                PlayerDataJson = JsonSerializer.Serialize(pState)
            });
        }

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "reveal",
            1,
            session.HostId,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(30),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players, bool Finished, List<ulong> Winners)> ResolveBunkerExileVoteAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
        var alive = players.Where(x => x.IsAlive).ToList();
        var aliveIds = alive.Select(x => x.UserId).ToHashSet();

        var validVotes = state.ExileVotes
            .Where(kv => aliveIds.Contains(kv.Key) && aliveIds.Contains(kv.Value))
            .ToList();

        var updatedPlayers = players.ToList();

        if (validVotes.Count > 0)
        {
            var topTargetId = validVotes
                .GroupBy(kv => kv.Value)
                .OrderByDescending(g => g.Count())
                .First().Key;

            var idx = updatedPlayers.FindIndex(x => x.UserId == topTargetId);
            if (idx >= 0)
            {
                var exiled = updatedPlayers[idx];
                updatedPlayers[idx] = exiled with { IsAlive = false };
                state.EventLogs.Add($"🚪 Раунд {session.RoundNum}: Голосованием изгнан **{exiled.DisplayName}** ({exiled.RoleCode}).");
            }
        }
        else
        {
            // If nobody voted, exile lowest score or random if forced
            var randomExile = alive.OrderBy(_ => NextInt(0, 100)).First();
            var idx = updatedPlayers.FindIndex(x => x.UserId == randomExile.UserId);
            updatedPlayers[idx] = randomExile with { IsAlive = false };
            state.EventLogs.Add($"🚪 Раунд {session.RoundNum}: Изгнан **{randomExile.DisplayName}**.");
        }

        state.ExileVotes.Clear();

        // Reset RevealedThisRound for remaining players
        for (var i = 0; i < updatedPlayers.Count; i++)
        {
            var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(updatedPlayers[i].PlayerDataJson) ?? new BunkerPlayerStateDto();
            pState.RevealedThisRound = false;
            updatedPlayers[i] = updatedPlayers[i] with { PlayerDataJson = JsonSerializer.Serialize(pState) };
        }

        var remainingAlive = updatedPlayers.Where(x => x.IsAlive).ToList();
        if (remainingAlive.Count <= state.BunkerCapacity)
        {
            // Reveal all traits of survivors and simulate survival epilogue!
            var totalScore = 0;
            for (var i = 0; i < updatedPlayers.Count; i++)
            {
                var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(updatedPlayers[i].PlayerDataJson) ?? new BunkerPlayerStateDto();
                foreach (var t in pState.Traits)
                    t.Revealed = true;
                var sum = pState.Traits.Sum(t => t.SurvivalScore);
                if (updatedPlayers[i].IsAlive)
                    totalScore += sum;
                updatedPlayers[i] = updatedPlayers[i] with
                {
                    Score = sum,
                    PlayerDataJson = JsonSerializer.Serialize(pState)
                };
            }

            var avgScore = remainingAlive.Count > 0 ? totalScore / remainingAlive.Count : 0;
            var colonySurvived = avgScore >= 35;

            var epilogue = colonySurvived
                ? $"🏆 КОЛОНИЯ ВЫЖИЛА! Индекс выживаемости бункера составил {totalScore} ед. (средний: {avgScore}). Навыки выживших позволили починить системы жизнеобеспечения, вырастить урожай и возродить цивилизацию KRLZ!"
                : $"☢️ КРИТИЧЕСКИЙ СБОЙ В БУНКЕРЕ! Индекс группы составил всего {totalScore} ед. (требовалось от {remainingAlive.Count * 35}). Из-за нехватки профильных специалистов в бункере произошла авария, зато изгнанные нашли соседний военный склад и разделили половину наград!";

            state.EpilogueReport = epilogue;
            state.EventLogs.Add(epilogue);

            var winners = colonySurvived
                ? remainingAlive.Select(x => x.UserId).ToList()
                : updatedPlayers.Select(x => x.UserId).ToList();

            await partyRepo.FinishSessionAndDistributePrizeAsync(
                session.Id,
                winners,
                baseBonusPerWinner: 400,
                JsonSerializer.Serialize(state),
                epilogue,
                updatedPlayers,
                ct);

            return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), true, winners);
        }

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "reveal",
            session.RoundNum + 1,
            session.HostId,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(25),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct), false, []);
    }

    // =========================================================================
    // 3. SPYFALL ENGINE (/spyfall)
    // =========================================================================

    public static readonly (string Location, string[] Roles)[] SpyfallLocations =
    [
        ("Казино KRLZ", ["Крупье рулетки", "Охранник VIP-зала", "Хайроллер", "Бармен", "Директор казино", "Шулер", "Инкассатор"]),
        ("Орбитальная станция", ["Командир экипажа", "Бортинженер", "Астробиолог", "Космический турист", "Оператор связи", "Врач-физиолог", "Пилот шаттла"]),
        ("Подводная лодка", ["Капитан подлодки", "Акустик-сонарист", "Торпедист", "Кок", "Штурман", "Механик реактора", "Радист"]),
        ("Военная база", ["Полковник", "Часовой на КПП", "Снайпер", "Инструктор", "Военный врач", "Связист", "Оружейный мастер"]),
        ("Полярная станция", ["Метеоролог", "Гляциолог", "Полярный лётчик", "Кинолог упряжки", "Геофизик", "Повар станции", "Дизелист"]),
        ("Банк KRLZ", ["Управляющий банком", "Кассир-операционист", "Начальник охраны", "Кредитный эксперт", "Инкассатор", "Клиент хранилища", "Аудитор"]),
        ("Киберпанк-бар", ["Нетраннер", "Рипер-хирург", "Корпорат", "Бармен-киборг", "Вышибала", "Контрабандист", "Уличный самурай"]),
        ("Круизный лайнер", ["Капитан судна", "Аниматор", "Богатый пассажир", "Стюард", "Шеф-повар", "Музыкант палубы", "Радист"]),
        ("Киностудия", ["Режиссёр", "Каскадёр", "Оператор крана", "Гримёр", "Главный актёр", "Звукорежиссёр", "Сценарист"]),
        ("Больница скорой помощи", ["Главврач", "Хирург", "Анестезиолог", "Медсестра приёмного", "Пациент в гипсе", "Фельдшер скорой", "Рентгенолог"]),
        ("Пиратский галеон", ["Капитан пиратов", "Боцман", "Канонир", "Юнга", "Пленник в трюме", "Кормчий", "Корабельный плотник"]),
        ("Лаборатория биотех", ["Ведущий генетик", "Лаборант вивария", "Инспектор биозащиты", "Подопытный доброволец", "Вирусолог", "Курьер образцов", "Охранник сектора"]),
        ("Пассажирский экспресс", ["Машинист", "Проводник вагона", "Ревизор билетов", "Повар вагона-ресторана", "Пассажир СВ", "Попутчик с гитарой", "Начальник поезда"]),
        ("Средневековый замок", ["Король", "Придворный шут", "Рыцарь стражи", "Кузнец", "Алхимик", "Летописец", "Конюх"]),
        ("Полицейский участок", ["Комиссар", "Следователь", "Криминалист", "Дежурный сержант", "Адвокат", "Задержанный", "Патрульный"]),
        ("Ночной клуб", ["DJ резидент", "Фейс-контроль", "Бармен-миксолог", "Танцор", "VIP-гость", "Промоутер", "Светотехник"])
    ];

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartSpyfallGameAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var customLocs = await partyRepo.GetCustomContentAsync(session.GuildId, "spyfall_location", ct);
        var pool = new List<(string Location, string[] Roles)>(SpyfallLocations);

        foreach (var cl in customLocs)
        {
            if (pool.Any(x => string.Equals(x.Location, cl.Title, StringComparison.OrdinalIgnoreCase)))
                continue;

            string[] parsedRoles = [];
            try
            {
                if (!string.IsNullOrWhiteSpace(cl.PayloadJson) && cl.PayloadJson.TrimStart().StartsWith('['))
                    parsedRoles = JsonSerializer.Deserialize<string[]>(cl.PayloadJson) ?? [];
            }
            catch
            {
            }

            if (parsedRoles.Length == 0 && !string.IsNullOrWhiteSpace(cl.Subtitle))
            {
                parsedRoles = cl.Subtitle.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (parsedRoles.Length == 0)
            {
                parsedRoles = ["Руководитель объекта", "Охранник", "Специалист", "Стажёр", "Посетитель", "Инженер", "Курьер"];
            }

            pool.Add((cl.Title, parsedRoles));
        }

        Shuffle(pool);
        var matchBoardLocations = pool.Take(16).ToList();
        var locEntry = matchBoardLocations[NextInt(0, matchBoardLocations.Count)];
        var spyIdx = NextInt(0, players.Count);
        var roles = locEntry.Roles.ToList();
        Shuffle(roles);

        var updatedPlayers = new List<PartyGamePlayerEntity>(players.Count);
        var roleMap = new Dictionary<ulong, string>();

        for (var i = 0; i < players.Count; i++)
        {
            var isSpy = i == spyIdx;
            var rName = isSpy ? "ШПИОН (Локация неизвестна)" : roles[i % roles.Count];
            roleMap[players[i].UserId] = rName;

            updatedPlayers.Add(players[i] with
            {
                SeatIndex = i + 1,
                RoleCode = isSpy ? "spy" : "agent",
                TeamCode = isSpy ? "spy" : "agents",
                IsAlive = true,
                PlayerDataJson = JsonSerializer.Serialize(new { RoleTitle = rName, Location = isSpy ? "???" : locEntry.Location })
            });
        }

        var state = new SpyfallGameStateDto
        {
            SecretLocation = locEntry.Location,
            AllLocations = matchBoardLocations.Select(x => x.Location).ToList(),
            SpyUserId = players[spyIdx].UserId,
            PlayerRoles = roleMap,
            LastLog = "Локация засекречена! Задавайте друг другу наводящие вопросы. Шпион может угадать локацию в любой момент!"
        };

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "round",
            1,
            session.HostId,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(20),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    // =========================================================================
    // 4. LIAR'S BAR ENGINE (/liarsbar)
    // =========================================================================

    private static readonly string[] LiarsTableRanks = ["Q", "K", "A"];

    public static string FormatCardLabel(string rank) => rank switch
    {
        "Q" => "Дама (Q)",
        "K" => "Король (K)",
        "A" => "Туз (A)",
        "JOKER" => "Джокер (★)",
        _ => rank
    };

    public static List<string> CreateLiarsDeck()
    {
        var deck = new List<string>(20);
        for (var i = 0; i < 6; i++)
        {
            deck.Add("Q");
            deck.Add("K");
            deck.Add("A");
        }
        deck.Add("JOKER");
        deck.Add("JOKER");
        Shuffle(deck);
        return deck;
    }

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartLiarsBarGameAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var deck = CreateLiarsDeck();
        var tableRank = LiarsTableRanks[NextInt(0, LiarsTableRanks.Length)];

        var updatedPlayers = new List<PartyGamePlayerEntity>(players.Count);
        for (var i = 0; i < players.Count; i++)
        {
            var hand = deck.Skip(i * 5).Take(5).ToList();
            var pDto = new LiarsBarPlayerDto
            {
                Hand = hand,
                RevolverBulletSlot = NextInt(1, 7),
                RevolverShotsTaken = 0
            };

            updatedPlayers.Add(players[i] with
            {
                SeatIndex = i + 1,
                RoleCode = "gambler",
                TeamCode = "solo",
                IsAlive = true,
                PlayerDataJson = JsonSerializer.Serialize(pDto)
            });
        }

        var state = new LiarsBarGameStateDto
        {
            TableRank = tableRank,
            LastPlayerId = null,
            LastPlayedCards = [],
            PileCount = 0,
            LastActionLog = $"Карта стола: {FormatCardLabel(tableRank)}! Первым ходит {updatedPlayers[0].DisplayName}."
        };

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "round",
            1,
            updatedPlayers[0].UserId,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(20),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    public static void DealNewLiarsSubround(LiarsBarGameStateDto state, List<PartyGamePlayerEntity> updatedPlayers, ulong nextStarterId)
    {
        var deck = CreateLiarsDeck();
        state.TableRank = LiarsTableRanks[NextInt(0, LiarsTableRanks.Length)];
        state.LastPlayerId = null;
        state.LastPlayedCards.Clear();
        state.PileCount = 0;

        var dealIdx = 0;
        for (var i = 0; i < updatedPlayers.Count; i++)
        {
            if (!updatedPlayers[i].IsAlive)
                continue;

            var pDto = JsonSerializer.Deserialize<LiarsBarPlayerDto>(updatedPlayers[i].PlayerDataJson) ?? new LiarsBarPlayerDto();
            pDto.Hand = deck.Skip((dealIdx % 4) * 5).Take(5).ToList();
            dealIdx++;
            updatedPlayers[i] = updatedPlayers[i] with { PlayerDataJson = JsonSerializer.Serialize(pDto) };
        }
    }

    // =========================================================================
    // 5. QUIZ / BRAIN-RING ENGINE (/quiz)
    // =========================================================================

    private static readonly (string Topic, (int Points, string Q, string A)[] Questions)[] DefaultQuizPacks =
    [
        ("Игры и Киберспорт", [
            (100, "В какой игре главная карта называется Summoner's Rift (Ущелье Призывателей)?", "league of legends|лол|lol"),
            (200, "Как называется предмет в Dota 2, дающий невосприимчивость к эффектам (аватар)?", "black king bar|бкб|bkb"),
            (300, "В каком году вышла оригинальная игра Minecraft (первая публичная альфа/релиз)?", "2009|2011"),
            (400, "Как зовут главного героя серии игр The Witcher (Ведьмак)?", "геральт|geralt"),
            (500, "Какая студия разработала серию игр Dark Souls и Elden Ring?", "fromsoftware|фромсофтвер")
        ]),
        ("Наука и Космос", [
            (100, "Какая планета Солнечной системы находится ближе всего к Солнцу?", "меркурий|mercury"),
            (200, "Какой химический элемент обозначается символом Au?", "золото|aurum"),
            (300, "Как называется самая глубокая впадина Мирового океана?", "марианская|марианский"),
            (400, "Сколько хромосом в норме у здорового человека?", "46|сорок шесть"),
            (500, "Как называется граница вокруг чёрной дыры, которую не может покинуть даже свет?", "горизонт событий")
        ]),
        ("Кино и Аниме", [
            (100, "Как зовут владельца Тетради Смерти в одноимённом аниме?", "лайт|ягами|light"),
            (200, "Какую таблетку выбрал Нео в фильме «Матрица» (цвет)?", "красную|красная|red"),
            (300, "Как называется вымышленный металл из Ваканды во вселенной Marvel?", "вибраниум|vibranium"),
            (400, "Кто срежиссировал фильмы «Интерстеллар», «Начало» и «Оппенгеймер»?", "нолан|кристофер нолан|nolan"),
            (500, "Как называется корабль Джека Воробья в «Пиратах Карибского моря»?", "черная жемчужина|чёрная жемчужина")
        ]),
        ("Технологии и KRLZ", [
            (100, "Какой язык программирования лежит в основе платформы .NET и бота KRLZ?", "c#|си шарп|csharp"),
            (200, "Как расшифровывается аббревиатура CPU в компьютере (по-русски или по-английски)?", "процессор|central processing unit|цпу"),
            (300, "Какой порт по умолчанию использует протокол HTTPS?", "443"),
            (400, "Как называется база данных, хранящая все таблицы в одном локальном файле с режимом WAL?", "sqlite|скюлайт"),
            (500, "Как называется алгоритм 64-битного визуального хэширования картинок в Агенте 5 KRLZ?", "dhash|дхэш")
        ])
    ];

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartQuizGameAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var topicMap = new Dictionary<string, List<(int Points, string Q, string A)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in DefaultQuizPacks)
        {
            topicMap[pack.Topic] = pack.Questions.ToList();
        }

        var customQuestions = await partyRepo.GetCustomContentAsync(session.GuildId, "quiz_question", ct);
        foreach (var cq in customQuestions)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(cq.PayloadJson) && cq.PayloadJson.TrimStart().StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(cq.PayloadJson);
                    var r = doc.RootElement;
                    var top = r.TryGetProperty("topic", out var tEl) ? tEl.GetString() ?? "Кастомная тема" : "Кастомная тема";
                    var pts = r.TryGetProperty("points", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : 300;
                    var qText = r.TryGetProperty("question", out var qEl) ? qEl.GetString() ?? cq.Title : cq.Title;
                    var aText = r.TryGetProperty("answer", out var aEl) ? aEl.GetString() ?? cq.Subtitle : cq.Subtitle;

                    if (!topicMap.TryGetValue(top, out var list))
                    {
                        list = [];
                        topicMap[top] = list;
                    }
                    if (!list.Any(x => string.Equals(x.Q, qText, StringComparison.OrdinalIgnoreCase)))
                        list.Add((pts, qText, aText));
                }
            }
            catch
            {
            }
        }

        var completeTopics = topicMap
            .Where(kv => kv.Value.Count >= 5)
            .Select(kv => (Topic: kv.Key, Questions: kv.Value.OrderBy(x => x.Points).Take(5).ToArray()))
            .ToList();

        Shuffle(completeTopics);
        var chosenPacks = completeTopics.Take(4).ToList();

        var cells = new List<QuizQuestionCellDto>();
        var topics = new List<string>();

        for (var t = 0; t < chosenPacks.Count; t++)
        {
            var pack = chosenPacks[t];
            topics.Add(pack.Topic);
            for (var p = 0; p < pack.Questions.Length; p++)
            {
                var q = pack.Questions[p];
                cells.Add(new QuizQuestionCellDto
                {
                    TopicIndex = t,
                    PriceIndex = p,
                    Topic = pack.Topic,
                    Points = (p + 1) * 100,
                    Question = q.Q,
                    Answer = q.A,
                    Answered = false
                });
            }
        }

        var state = new QuizGameStateDto
        {
            Topics = topics,
            Cells = cells,
            ActiveCellIndex = null,
            BuzzerUserId = null,
            LastLog = $"Интеллектуальный матч начался! {players[0].DisplayName} выбирает первый вопрос на табло."
        };

        var updatedPlayers = players.Select((p, idx) => p with
        {
            SeatIndex = idx + 1,
            RoleCode = "scholar",
            TeamCode = "solo",
            IsAlive = true,
            Score = 0
        }).ToList();

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "select_cell",
            1,
            updatedPlayers[0].UserId,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(25),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    // =========================================================================
    // 6. CODENAMES ENGINE (/codenames)
    // =========================================================================

    private static readonly string[] CodenamesWordsPool =
    [
        "АГЕНТ", "АЛМАЗ", "АНТЕННА", "АТЛАНТИДА", "БАНК", "БАШНЯ", "БЕРМУДЫ", "БИРЖА", "БОМБА", "БУНКЕР",
        "ВАМПИР", "ВИРУС", "ВОЛНА", "ВУЛКАН", "ГАЛАКТИКА", "ГИТАРА", "ГОРИЗОНТ", "ДРАКОН", "ЗАМОК", "ЗВЕЗДА",
        "ИМПЕРИЯ", "КАЗИНО", "КАПКАН", "КВАНТ", "КЕНТАВР", "КИБОРГ", "КОД", "КОЛЬЦО", "КОМЕТА", "КОРОНА",
        "КРИСТАЛЛ", "ЛАБИРИНТ", "ЛАЗЕР", "ЛЕДНИК", "МАГНИТ", "МАЯК", "МЕТЕОР", "МИРАЖ", "МОЛНИЯ", "МУТАНТ",
        "НИНДЗЯ", "ОАЗИС", "ОКЕАН", "ОРБИТА", "ОСТРОВ", "ПАРОЛЬ", "ПИРАМИДА", "ПИРАТ", "ПЛАЗМА", "ПОРТАЛ",
        "ПРИЗРАК", "РАДАР", "РАКЕТА", "РЕАКТОР", "РОБОТ", "РУЛЕТКА", "РЫЦАРЬ", "САМУРАЙ", "СЕЙФ", "СИГНАЛ",
        "СКАФАНДР", "СНАЙПЕР", "СПУТНИК", "СФИНКС", "ТАЙФУН", "ТИТАН", "ТОРНАДО", "ТУННЕЛЬ", "ФЕНИКС", "ШИФР"
    ];

    public async Task<(PartyGameSessionEntity Session, IReadOnlyList<PartyGamePlayerEntity> Players)> StartCodenamesGameAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        CancellationToken ct = default)
    {
        var customWords = await partyRepo.GetCustomContentAsync(session.GuildId, "codenames_word", ct);
        var words = CodenamesWordsPool
            .Concat(customWords.Select(x => x.Title.Trim().ToUpperInvariant()))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Shuffle(words);
        var selected25 = words.Take(25).ToList();

        var colors = new List<string>(25);
        for (var i = 0; i < 9; i++) colors.Add("red");
        for (var i = 0; i < 8; i++) colors.Add("blue");
        for (var i = 0; i < 7; i++) colors.Add("neutral");
        colors.Add("assassin");
        Shuffle(colors);

        var grid = new List<CodenamesCellDto>(25);
        for (var i = 0; i < 25; i++)
        {
            grid.Add(new CodenamesCellDto
            {
                Word = selected25[i],
                Color = colors[i],
                Revealed = false
            });
        }

        var updatedPlayers = new List<PartyGamePlayerEntity>(players.Count);
        ulong redCap = 0, blueCap = 0;

        for (var i = 0; i < players.Count; i++)
        {
            var team = i % 2 == 0 ? "red" : "blue";
            string role;
            if (team == "red" && redCap == 0)
            {
                redCap = players[i].UserId;
                role = "captain";
            }
            else if (team == "blue" && blueCap == 0)
            {
                blueCap = players[i].UserId;
                role = "captain";
            }
            else
            {
                role = "operative";
            }

            updatedPlayers.Add(players[i] with
            {
                SeatIndex = i + 1,
                RoleCode = role,
                TeamCode = team,
                IsAlive = true
            });
        }

        var state = new CodenamesGameStateDto
        {
            Grid = grid,
            CurrentTeam = "red",
            RedCaptainId = redCap,
            BlueCaptainId = blueCap,
            CurrentClue = string.Empty,
            GuessesRemaining = 0,
            LastLog = "Игра началась! Капитаны могут открыть «Карту капитана». Ход КРАСНОЙ команды."
        };

        await partyRepo.SaveFullGameStateAsync(
            session.Id,
            "active",
            "clue",
            1,
            redCap,
            JsonSerializer.Serialize(state),
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow.AddMinutes(30),
            updatedPlayers,
            ct);

        return ((await partyRepo.GetSessionByIdAsync(session.Id, ct))!, await partyRepo.GetPlayersAsync(session.Id, ct));
    }

    // =========================================================================
    // 7. RAID BOSS ENGINE (/raid)
    // =========================================================================

    public async Task<(bool Success, string Error, bool Defeated, long Damage, string LogLine, Dictionary<ulong, (long Coins, int Crystals)> Rewards)> ExecuteRaidAttackAsync(
        SocketGuild? guild,
        long raidSessionId,
        ulong guildId,
        ulong userId,
        string displayName,
        string heroClass,
        ISocketMessageChannel? channel = null,
        CancellationToken ct = default)
    {
        var profile = await userRepo.GetOrCreateAsync(guildId, userId, ct);
        var pet = await socialExpRepo.GetUserPetAsync(guildId, userId, ct);
        var inv = await itemRepo.GetUserInventoryAsync(guildId, userId, categoryFilter: null, ct);

        long baseDmg = heroClass switch
        {
            "mage" => NextInt(260, 420),
            "assassin" => NextInt(190, 310),
            _ => NextInt(230, 350)
        };

        var isCrit = false;
        var critChance = heroClass == "assassin" ? 38 : heroClass == "mage" ? 24 : 16;
        if (NextInt(1, 101) <= critChance)
        {
            isCrit = true;
            baseDmg = (long)Math.Round(baseDmg * (heroClass == "assassin" ? 2.35 : 1.85));
        }

        // Level & Pet & Item bonuses
        var levelBonus = profile.Level * 12L;
        var petBonus = pet is not null && pet.IsActive ? pet.Level * 28L : 0L;
        var itemBonus = inv.Sum(x => x.Inventory.Quantity > 0 ? 18L : 0L);

        var totalDamage = baseDmg + levelBonus + petBonus + itemBonus;
        var classIcon = heroClass switch
        {
            "mage" => "🔮 Маг",
            "assassin" => "🗡️ Ассасин",
            _ => "⚔️ Воин"
        };

        var critTag = isCrit ? " 💥 КРИТИЧЕСКИЙ УДАР!" : string.Empty;
        var petTag = petBonus > 0 && pet is not null ? $" (+питомец {pet.Name})" : string.Empty;
        var logLine = $"{classIcon} {displayName} нанёс {totalDamage:N0} ед. урона{critTag}{petTag}";

        var (ok, err, defeated, newHp, rewards) = await partyRepo.RegisterRaidHitAsync(
            raidSessionId,
            userId,
            displayName,
            heroClass,
            totalDamage,
            logLine,
            cooldownSeconds: 5,
            ct: ct);

        if (!ok)
            return (false, err, false, 0, string.Empty, rewards);

        await progressionService.TrackActivityAsync(guildId, userId, "raid_hits", 1, 25, ct);
        await clanRepo.AddActivityPointsAsync(guildId, userId, 3, ct);

        if (defeated && guild is not null)
        {
            foreach (var uid in rewards.Keys)
            {
                await achievementService.TrackEventAsync(guild, uid, "raid_boss_kills", 1, false, channel, ct);
            }
        }

        return (true, string.Empty, defeated, totalDamage, logLine, rewards);
    }

    // =========================================================================
    // SKIASHARP GRAPHICAL RENDERING PIPELINE (ALL 7 GAMES + LOBBY)
    // =========================================================================

    public async Task<byte[]> RenderLobbyBannerAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var (minP, maxP, title, subtitle) = GetGameMeta(session.GameType);

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 420, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(BgDark);

            DrawCyberGridBackground(canvas, CanvasWidth, 420, IndigoAccent);

            var panel = new SKRoundRect(new SKRect(24, 24, CanvasWidth - 24, 396), 22, 22);
            using var pFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var pStroke = new SKPaint { Color = GlassStroke, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(panel, pFill);
            canvas.DrawRoundRect(panel, pStroke);

            using var badgePaint = new SKPaint { Typeface = FontBold, TextSize = 13f, Color = CyanAccent, IsAntialias = true };
            using var titlePaint = new SKPaint { Typeface = FontBold, TextSize = 28f, Color = SKColors.White, IsAntialias = true };
            using var subPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = MutedText, IsAntialias = true };

            canvas.DrawText($"ОТКРЫТОЕ ЛОББИ #{session.Id}  •  ИГРОКОВ: {players.Count}/{maxP} (МИН. {minP})", 48f, 60f, badgePaint);
            canvas.DrawText(title, 48f, 94f, titlePaint);
            canvas.DrawText(subtitle, 48f, 118f, subPaint);

            // Prize pool badge on top right
            var poolRect = new SKRoundRect(new SKRect(CanvasWidth - 290, 44, CanvasWidth - 48, 108), 16, 16);
            using var poolFill = new SKPaint { Color = new SKColor(245, 158, 11, 32), IsAntialias = true };
            using var poolBorder = new SKPaint { Color = GoldAccent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(poolRect, poolFill);
            canvas.DrawRoundRect(poolRect, poolBorder);

            using var poolLbl = new SKPaint { Typeface = FontBold, TextSize = 11f, Color = GoldAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var poolVal = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            var centerPoolX = CanvasWidth - 169f;
            canvas.DrawText(session.Bet > 0 ? $"ВЗНОС: {session.Bet:N0} {currencySymbol}" : "БЕСПЛАТНЫЙ СТОЛ", centerPoolX, 66f, poolLbl);
            canvas.DrawText($"БАНК: {session.PrizePool:N0} {currencySymbol}", centerPoolX, 94f, poolVal);

            // Grid of player slots (up to 12)
            var cols = 4;
            var cardW = (CanvasWidth - 96f - (cols - 1) * 14f) / cols;
            var cardH = 66f;

            for (var i = 0; i < Math.Min(12, maxP); i++)
            {
                var row = i / cols;
                var col = i % cols;
                var x = 48f + col * (cardW + 14f);
                var y = 142f + row * (cardH + 12f);

                var hasPlayer = i < players.Count;
                var slotRect = new SKRoundRect(new SKRect(x, y, x + cardW, y + cardH), 14, 14);

                using var slotFill = new SKPaint
                {
                    Color = hasPlayer ? new SKColor(56, 189, 248, 28) : new SKColor(255, 255, 255, 9),
                    IsAntialias = true
                };
                using var slotBorder = new SKPaint
                {
                    Color = hasPlayer ? CyanAccent.WithAlpha(160) : new SKColor(255, 255, 255, 22),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.2f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(slotRect, slotFill);
                canvas.DrawRoundRect(slotRect, slotBorder);

                using var numPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 14f,
                    Color = hasPlayer ? CyanAccent : MutedText,
                    IsAntialias = true
                };
                canvas.DrawText($"#{i + 1}", x + 14f, y + 26f, numPaint);

                using var pNamePaint = new SKPaint
                {
                    Typeface = hasPlayer ? FontBold : FontRegular,
                    TextSize = 15f,
                    Color = hasPlayer ? SKColors.White : MutedText,
                    IsAntialias = true
                };
                var label = hasPlayer ? ClipText(players[i].DisplayName, pNamePaint, cardW - 28f) : "Свободное место";
                canvas.DrawText(label, x + 14f, y + 48f, pNamePaint);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderMafiaTableAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<MafiaGameStateDto>(session.StateJson) ?? new MafiaGameStateDto();
        var isNight = session.Phase == "night";
        var isFinished = session.Status == "finished";
        var themeAccent = isNight ? IndigoAccent : isFinished ? GoldAccent : CrimsonAccent;

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, CanvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(isNight ? new SKColor(9, 12, 24) : new SKColor(18, 16, 28));

            DrawCyberGridBackground(canvas, CanvasWidth, CanvasHeight, themeAccent);

            // Header bar
            var headerRect = new SKRoundRect(new SKRect(24, 20, CanvasWidth - 24, 98), 18, 18);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hStroke = new SKPaint { Color = themeAccent.WithAlpha(160), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(headerRect, hFill);
            canvas.DrawRoundRect(headerRect, hStroke);

            var phaseTitle = isFinished
                ? "ФИНАЛ ПАРТИИ • ВСЕ РОЛИ РАСКРЫТЫ"
                : isNight
                    ? $"НОЧЬ {session.RoundNum} • ГОРОД СПИТ (ТАЙНЫЕ ХОДЫ)"
                    : $"ДЕНЬ {session.RoundNum} • ОБСУЖДЕНИЕ И ГОЛОСОВАНИЕ СУДА";

            using var pTitlePaint = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = SKColors.White, IsAntialias = true };
            using var bannerPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = CyanAccent, IsAntialias = true };
            canvas.DrawText($"МАФИЯ KRLZ #{session.Id}  —  {phaseTitle}", 44f, 52f, pTitlePaint);
            canvas.DrawText(ClipText(state.LastEventBanner, bannerPaint, CanvasWidth - 96f), 44f, 78f, bannerPaint);

            // Center oval table
            var cx = CanvasWidth / 2f;
            var cy = 345f;
            var rx = 210f;
            var ry = 105f;

            using var tableFill = new SKPaint
            {
                Color = isNight ? new SKColor(22, 28, 56, 220) : new SKColor(38, 24, 42, 220),
                IsAntialias = true
            };
            using var tableStroke = new SKPaint
            {
                Color = themeAccent,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
                IsAntialias = true
            };
            canvas.DrawOval(cx, cy, rx, ry, tableFill);
            canvas.DrawOval(cx, cy, rx, ry, tableStroke);

            using var centerTitle = new SKPaint { Typeface = FontBold, TextSize = 20f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var centerSub = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = GoldAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
            var aliveCount = players.Count(x => x.IsAlive);
            canvas.DrawText(isFinished ? "ИГРА ОКОНЧЕНА" : $"В ЖИВЫХ: {aliveCount} / {players.Count}", cx, cy - 8f, centerTitle);
            canvas.DrawText($"ПРИЗОВОЙ БАНК: {session.PrizePool:N0} {currencySymbol}", cx, cy + 20f, centerSub);

            // Players positioned around the table
            var n = Math.Max(1, players.Count);
            var orbitRx = 370f;
            var orbitRy = 185f;

            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var angle = -MathF.PI / 2f + i * (2f * MathF.PI / n);
                var px = cx + MathF.Cos(angle) * orbitRx;
                var py = cy + MathF.Sin(angle) * orbitRy;

                var cardW = 168f;
                var cardH = 68f;
                var rect = new SKRoundRect(new SKRect(px - cardW / 2f, py - cardH / 2f, px + cardW / 2f, py + cardH / 2f), 14, 14);

                var borderColor = !p.IsAlive
                    ? CrimsonAccent
                    : isFinished && p.TeamCode == "mafia"
                        ? CrimsonAccent
                        : EmeraldAccent;

                using var cFill = new SKPaint
                {
                    Color = p.IsAlive ? new SKColor(18, 25, 42, 235) : new SKColor(45, 16, 26, 220),
                    IsAntialias = true
                };
                using var cStroke = new SKPaint
                {
                    Color = borderColor,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = p.IsAlive ? 2f : 1.5f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(rect, cFill);
                canvas.DrawRoundRect(rect, cStroke);

                using var nameP = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 14f,
                    Color = p.IsAlive ? SKColors.White : new SKColor(200, 120, 130),
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText(ClipText($"#{p.SeatIndex} {p.DisplayName}", nameP, cardW - 16f), px, py - 6f, nameP);

                var statusLabel = !p.IsAlive || isFinished
                    ? FormatMafiaRoleName(p.RoleCode)[2..].Trim()
                    : state.DayVotes.TryGetValue(p.UserId, out var vTarget) && vTarget != 0
                        ? $"Голос -> #{players.FirstOrDefault(x => x.UserId == vTarget)?.SeatIndex ?? 0}"
                        : "Житель города";

                using var roleP = new SKPaint
                {
                    Typeface = FontRegular,
                    TextSize = 12f,
                    Color = p.IsAlive ? CyanAccent : CrimsonAccent,
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText(ClipText(statusLabel, roleP, cardW - 14f), px, py + 16f, roleP);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderBunkerBoardAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<BunkerGameStateDto>(session.StateJson) ?? new BunkerGameStateDto();
        var h = Math.Max(540, 175 + players.Count * 62);

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(BgDark);

            DrawCyberGridBackground(canvas, CanvasWidth, h, GoldAccent);

            // Disaster header
            var headerRect = new SKRoundRect(new SKRect(20, 18, CanvasWidth - 20, 136), 18, 18);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hBorder = new SKPaint { Color = GoldAccent.WithAlpha(180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true };
            canvas.DrawRoundRect(headerRect, hFill);
            canvas.DrawRoundRect(headerRect, hBorder);

            using var titleP = new SKPaint { Typeface = FontBold, TextSize = 21f, Color = GoldAccent, IsAntialias = true };
            using var descP = new SKPaint { Typeface = FontRegular, TextSize = 13.5f, Color = SKColors.White, IsAntialias = true };
            using var subP = new SKPaint { Typeface = FontRegular, TextSize = 13f, Color = CyanAccent, IsAntialias = true };

            var aliveCount = players.Count(x => x.IsAlive);
            canvas.DrawText($"☢️ {state.DisasterTitle.ToUpperInvariant()}  •  МЕСТ В БУНКЕРЕ: {state.BunkerCapacity} (ОСТАЛОСЬ: {aliveCount})", 38f, 48f, titleP);
            canvas.DrawText(ClipText(state.DisasterDescription, descP, CanvasWidth - 76f), 38f, 74f, descP);
            canvas.DrawText(ClipText(state.BunkerDescription, subP, CanvasWidth - 76f), 38f, 98f, subP);

            var lastLog = state.EventLogs.LastOrDefault() ?? "";
            using var logP = new SKPaint { Typeface = FontBold, TextSize = 12.5f, Color = EmeraldAccent, IsAntialias = true };
            canvas.DrawText(ClipText(lastLog, logP, CanvasWidth - 76f), 38f, 122f, logP);

            // Player rows with 6 trait columns
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var pState = JsonSerializer.Deserialize<BunkerPlayerStateDto>(p.PlayerDataJson) ?? new BunkerPlayerStateDto();
                var y = 152f + i * 58f;

                var rowRect = new SKRoundRect(new SKRect(20f, y, CanvasWidth - 20f, y + 50f), 12, 12);
                using var rFill = new SKPaint
                {
                    Color = p.IsAlive ? new SKColor(22, 30, 50, 225) : new SKColor(52, 18, 26, 200),
                    IsAntialias = true
                };
                using var rStroke = new SKPaint
                {
                    Color = p.IsAlive ? CyanAccent.WithAlpha(90) : CrimsonAccent.WithAlpha(140),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.2f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(rowRect, rFill);
                canvas.DrawRoundRect(rowRect, rStroke);

                using var pName = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 14f,
                    Color = p.IsAlive ? SKColors.White : CrimsonAccent,
                    IsAntialias = true
                };
                canvas.DrawText(ClipText($"#{p.SeatIndex} {p.DisplayName}", pName, 145f), 32f, y + 22f, pName);

                using var statusP = new SKPaint
                {
                    Typeface = FontRegular,
                    TextSize = 11.5f,
                    Color = p.IsAlive ? EmeraldAccent : CrimsonAccent,
                    IsAntialias = true
                };
                canvas.DrawText(p.IsAlive ? "В очереди в бункер" : "ИЗГНАН ИЗ БУНКЕРА", 32f, y + 40f, statusP);

                // 6 traits pills
                var startX = 186f;
                var cellW = (CanvasWidth - 206f) / 6f;

                for (var tIdx = 0; tIdx < Math.Min(6, pState.Traits.Count); tIdx++)
                {
                    var tr = pState.Traits[tIdx];
                    var tx = startX + tIdx * cellW;
                    var pill = new SKRoundRect(new SKRect(tx + 3f, y + 6f, tx + cellW - 3f, y + 44f), 8, 8);

                    var show = tr.Revealed || session.Status == "finished" || !p.IsAlive;
                    using var pillBg = new SKPaint
                    {
                        Color = show ? new SKColor(56, 189, 248, 26) : new SKColor(255, 255, 255, 10),
                        IsAntialias = true
                    };
                    canvas.DrawRoundRect(pill, pillBg);

                    using var lblP = new SKPaint { Typeface = FontBold, TextSize = 10f, Color = MutedText, IsAntialias = true };
                    using var valP = new SKPaint
                    {
                        Typeface = show ? FontBold : FontRegular,
                        TextSize = 11.5f,
                        Color = show ? SKColors.White : new SKColor(148, 163, 184),
                        IsAntialias = true
                    };

                    canvas.DrawText(tr.Label, tx + 9f, y + 20f, lblP);
                    var valStr = show ? tr.Value : "🔒 Скрыто";
                    canvas.DrawText(ClipText(valStr, valP, cellW - 16f), tx + 9f, y + 36f, valP);
                }
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderSpyfallBoardAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<SpyfallGameStateDto>(session.StateJson) ?? new SpyfallGameStateDto();
        var isFinished = session.Status == "finished";

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 560, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(BgDark);

            DrawCyberGridBackground(canvas, CanvasWidth, 560, CyanAccent);

            var header = new SKRoundRect(new SKRect(24, 20, CanvasWidth - 24, 96), 18, 18);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hStroke = new SKPaint { Color = CyanAccent.WithAlpha(150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawRoundRect(header, hFill);
            canvas.DrawRoundRect(header, hStroke);

            using var tPaint = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = SKColors.White, IsAntialias = true };
            using var sPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = CyanAccent, IsAntialias = true };

            var locBanner = isFinished ? $"ЛОКАЦИЯ РАСКРЫТА: {state.SecretLocation.ToUpperInvariant()}" : "СЕКРЕТНАЯ ОПЕРАЦИЯ • СЕТКА ВОЗМОЖНЫХ ЛОКАЦИЙ";
            canvas.DrawText($"🕵️ ШПИОН KRLZ #{session.Id}  —  {locBanner}", 44f, 52f, tPaint);
            canvas.DrawText(ClipText(state.LastLog, sPaint, CanvasWidth - 96f), 44f, 78f, sPaint);

            // Grid of 16 locations (4x4)
            var locs = state.AllLocations.Take(16).ToList();
            var cols = 4;
            var cellW = (CanvasWidth - 48f - 36f) / cols;
            var cellH = 54f;

            for (var i = 0; i < locs.Count; i++)
            {
                var r = i / cols;
                var c = i % cols;
                var x = 24f + c * (cellW + 12f);
                var y = 114f + r * (cellH + 10f);

                var isSecret = isFinished && locs[i] == state.SecretLocation;
                var rect = new SKRoundRect(new SKRect(x, y, x + cellW, y + cellH), 12, 12);

                using var cFill = new SKPaint
                {
                    Color = isSecret ? new SKColor(16, 185, 129, 55) : new SKColor(22, 31, 52, 220),
                    IsAntialias = true
                };
                using var cStroke = new SKPaint
                {
                    Color = isSecret ? EmeraldAccent : GlassStroke,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = isSecret ? 2f : 1.2f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(rect, cFill);
                canvas.DrawRoundRect(rect, cStroke);

                using var lText = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 14.5f,
                    Color = isSecret ? EmeraldAccent : SKColors.White,
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText(ClipText(locs[i], lText, cellW - 16f), x + cellW / 2f, y + 33f, lText);
            }

            // Players strip at bottom
            var botRect = new SKRoundRect(new SKRect(24, 386, CanvasWidth - 24, 536), 18, 18);
            canvas.DrawRoundRect(botRect, hFill);

            using var pHead = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = GoldAccent, IsAntialias = true };
            canvas.DrawText($"ПОДОЗРЕВАЕМЫЕ УЧАСТНИКИ ({players.Count})  •  БАНК: {session.PrizePool:N0} {currencySymbol}", 42f, 414f, pHead);

            var pCols = Math.Min(5, Math.Max(1, players.Count));
            var pW = (CanvasWidth - 84f - (pCols - 1) * 10f) / pCols;
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var col = i % pCols;
                var row = i / pCols;
                var px = 42f + col * (pW + 10f);
                var py = 428f + row * 48f;

                var isSpyReveal = isFinished && p.UserId == state.SpyUserId;
                using var pName = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 13.5f,
                    Color = isSpyReveal ? CrimsonAccent : SKColors.White,
                    IsAntialias = true
                };
                var tag = isSpyReveal ? $"[ШПИОН] {p.DisplayName}" : $"#{p.SeatIndex} {p.DisplayName}";
                canvas.DrawText(ClipText(tag, pName, pW - 8f), px, py + 20f, pName);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderLiarsBarTableAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<LiarsBarGameStateDto>(session.StateJson) ?? new LiarsBarGameStateDto();

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 520, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(18, 13, 22));

            DrawCyberGridBackground(canvas, CanvasWidth, 520, CrimsonAccent);

            var header = new SKRoundRect(new SKRect(24, 20, CanvasWidth - 24, 100), 18, 18);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hStroke = new SKPaint { Color = CrimsonAccent.WithAlpha(170), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true };
            canvas.DrawRoundRect(header, hFill);
            canvas.DrawRoundRect(header, hStroke);

            using var tPaint = new SKPaint { Typeface = FontBold, TextSize = 23f, Color = SKColors.White, IsAntialias = true };
            using var sPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = GoldAccent, IsAntialias = true };

            canvas.DrawText($"🔫 БАР ЛЖЕЦОВ #{session.Id}  •  СТОЛ: {FormatCardLabel(state.TableRank).ToUpperInvariant()}", 44f, 54f, tPaint);
            canvas.DrawText(ClipText(state.LastActionLog, sPaint, CanvasWidth - 96f), 44f, 80f, sPaint);

            // Center card pile
            var centerRect = new SKRoundRect(new SKRect(330, 120, CanvasWidth - 330, 240), 18, 18);
            using var cFill = new SKPaint { Color = new SKColor(38, 22, 36, 230), IsAntialias = true };
            canvas.DrawRoundRect(centerRect, cFill);
            canvas.DrawRoundRect(centerRect, hStroke);

            using var cTitle = new SKPaint { Typeface = FontBold, TextSize = 18f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            using var cSub = new SKPaint { Typeface = FontBold, TextSize = 15f, Color = CyanAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText($"КАРТ НА СТОЛЕ: {state.PileCount}", CanvasWidth / 2f, 165f, cTitle);
            canvas.DrawText($"Последний сброс: {state.LastPlayedCards.Count} карт(ы)", CanvasWidth / 2f, 195f, cSub);
            canvas.DrawText($"БАНК: {session.PrizePool:N0} {currencySymbol}", CanvasWidth / 2f, 222f, sPaint);

            // 2-4 Player cards
            var count = Math.Max(1, players.Count);
            var cardW = (CanvasWidth - 48f - (count - 1) * 16f) / count;
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                var pDto = JsonSerializer.Deserialize<LiarsBarPlayerDto>(p.PlayerDataJson) ?? new LiarsBarPlayerDto();
                var x = 24f + i * (cardW + 16f);
                var y = 266f;

                var isTurn = session.Status == "active" && session.TurnUserId == p.UserId;
                var pRect = new SKRoundRect(new SKRect(x, y, x + cardW, 492f), 18, 18);
                using var pFill = new SKPaint
                {
                    Color = p.IsAlive ? new SKColor(22, 28, 46, 235) : new SKColor(48, 16, 24, 210),
                    IsAntialias = true
                };
                using var pBorder = new SKPaint
                {
                    Color = !p.IsAlive ? CrimsonAccent : isTurn ? GoldAccent : CyanAccent.WithAlpha(100),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = isTurn ? 2.6f : 1.4f,
                    IsAntialias = true
                };
                canvas.DrawRoundRect(pRect, pFill);
                canvas.DrawRoundRect(pRect, pBorder);

                using var nameP = new SKPaint { Typeface = FontBold, TextSize = 16f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
                canvas.DrawText(ClipText(p.DisplayName, nameP, cardW - 20f), x + cardW / 2f, y + 34f, nameP);

                using var handP = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = CyanAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
                canvas.DrawText(p.IsAlive ? $"Карт в руке: {pDto.Hand.Count}" : "ВЫБЫЛ (ВЫСТРЕЛ)", x + cardW / 2f, y + 62f, handP);

                // Draw 6-chamber revolver cylinder
                var revCx = x + cardW / 2f;
                var revCy = y + 135f;
                using var cylFill = new SKPaint { Color = new SKColor(15, 20, 34), IsAntialias = true };
                using var cylStroke = new SKPaint { Color = isTurn ? GoldAccent : MutedText, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
                canvas.DrawCircle(revCx, revCy, 42f, cylFill);
                canvas.DrawCircle(revCx, revCy, 42f, cylStroke);

                for (var ch = 0; ch < 6; ch++)
                {
                    var ang = ch * MathF.PI / 3f - MathF.PI / 2f;
                    var chX = revCx + MathF.Cos(ang) * 24f;
                    var chY = revCy + MathF.Sin(ang) * 24f;
                    var fired = ch < pDto.RevolverShotsTaken;

                    using var chPaint = new SKPaint
                    {
                        Color = !p.IsAlive && ch == pDto.RevolverShotsTaken - 1
                            ? CrimsonAccent
                            : fired
                                ? GoldAccent
                                : new SKColor(255, 255, 255, 35),
                        IsAntialias = true
                    };
                    canvas.DrawCircle(chX, chY, 8.5f, chPaint);
                }

                using var revText = new SKPaint { Typeface = FontBold, TextSize = 13f, Color = GoldAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
                canvas.DrawText($"Курок: {pDto.RevolverShotsTaken} / 6", revCx, y + 204f, revText);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderRaidBossArenaAsync(
        RaidBossSessionEntity boss,
        IReadOnlyList<RaidBossHitEntity> hits,
        string currencySymbol,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 540, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(14, 12, 24));

            DrawCyberGridBackground(canvas, CanvasWidth, 540, CrimsonAccent);

            var panel = new SKRoundRect(new SKRect(24, 20, CanvasWidth - 24, 520), 22, 22);
            using var pFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var pStroke = new SKPaint { Color = CrimsonAccent.WithAlpha(180), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
            canvas.DrawRoundRect(panel, pFill);
            canvas.DrawRoundRect(panel, pStroke);

            using var tagP = new SKPaint { Typeface = FontBold, TextSize = 13f, Color = GoldAccent, IsAntialias = true };
            using var nameP = new SKPaint { Typeface = FontBold, TextSize = 30f, Color = SKColors.White, IsAntialias = true };
            using var logP = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = CyanAccent, IsAntialias = true };

            var statusLabel = boss.Status == "defeated" ? "ПОВЕРЖЕН ГЕРОЯМИ СЕРВЕРА!" : $"АКТИВНЫЙ РЕЙД • УРОВЕНЬ {boss.BossLevel}";
            canvas.DrawText($"🐉 МИРОВОЙ РЕЙД-БОСС KRLZ #{boss.Id}  •  {statusLabel}", 48f, 56f, tagP);
            canvas.DrawText(boss.BossName, 48f, 92f, nameP);
            canvas.DrawText($"Награда пула: {boss.RewardPoolCoins:N0} {currencySymbol} + {boss.RewardCrystals} 💎 кристаллов", 48f, 118f, logP);

            // Giant HP Bar
            var hpBarLeft = 48f;
            var hpBarTop = 136f;
            var hpBarW = CanvasWidth - 96f;
            var hpBarH = 38f;
            var ratio = Math.Clamp((float)boss.CurrentHp / Math.Max(1L, boss.MaxHp), 0f, 1f);

            var trackRect = new SKRoundRect(new SKRect(hpBarLeft, hpBarTop, hpBarLeft + hpBarW, hpBarTop + hpBarH), 14, 14);
            using var trackPaint = new SKPaint { Color = new SKColor(255, 255, 255, 18), IsAntialias = true };
            canvas.DrawRoundRect(trackRect, trackPaint);

            if (ratio > 0)
            {
                var fillW = Math.Max(28f, hpBarW * ratio);
                var fillRect = new SKRoundRect(new SKRect(hpBarLeft, hpBarTop, hpBarLeft + fillW, hpBarTop + hpBarH), 14, 14);
                using var fillPaint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(
                        new SKPoint(hpBarLeft, hpBarTop),
                        new SKPoint(hpBarLeft + fillW, hpBarTop),
                        [CrimsonAccent, GoldAccent],
                        (float[]?)null,
                        SKShaderTileMode.Clamp)
                };
                canvas.DrawRoundRect(fillRect, fillPaint);
            }

            using var hpText = new SKPaint { Typeface = FontBold, TextSize = 16f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
            canvas.DrawText($"HP БОССА: {boss.CurrentHp:N0} / {boss.MaxHp:N0} ({ratio * 100:F1}%)", CanvasWidth / 2f, hpBarTop + 25f, hpText);

            using var actionP = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = EmeraldAccent, IsAntialias = true };
            canvas.DrawText(ClipText($"Последнее событие: {boss.LastLog}", actionP, CanvasWidth - 96f), 48f, 202f, actionP);

            // Top attackers leaderboard
            var totalDmg = Math.Max(1L, hits.Sum(x => x.TotalDamage));
            var topHits = hits.Take(6).ToList();

            for (var i = 0; i < topHits.Count; i++)
            {
                var h = topHits[i];
                var y = 222f + i * 46f;
                var rowRect = new SKRoundRect(new SKRect(48f, y, CanvasWidth - 48f, y + 38f), 10, 10);
                using var rBg = new SKPaint { Color = new SKColor(255, 255, 255, i % 2 == 0 ? (byte)14 : (byte)8), IsAntialias = true };
                canvas.DrawRoundRect(rowRect, rBg);

                var share = (double)h.TotalDamage / totalDmg * 100.0;
                using var rLeft = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = SKColors.White, IsAntialias = true };
                using var rRight = new SKPaint { Typeface = FontBold, TextSize = 14f, Color = GoldAccent, IsAntialias = true, TextAlign = SKTextAlign.Right };

                canvas.DrawText($"#{i + 1}  {h.DisplayName} ({h.HeroClass.ToUpperInvariant()} • ударов: {h.HitsCount})", 64f, y + 24f, rLeft);
                canvas.DrawText($"{h.TotalDamage:N0} урона ({share:F1}%)", CanvasWidth - 64f, y + 24f, rRight);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderQuizBoardAsync(
        PartyGameSessionEntity session,
        IReadOnlyList<PartyGamePlayerEntity> players,
        string currencySymbol,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<QuizGameStateDto>(session.StateJson) ?? new QuizGameStateDto();

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 560, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(10, 18, 42));

            DrawCyberGridBackground(canvas, CanvasWidth, 560, CyanAccent);

            var header = new SKRoundRect(new SKRect(24, 18, CanvasWidth - 24, 96), 16, 16);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hStroke = new SKPaint { Color = GoldAccent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true };
            canvas.DrawRoundRect(header, hFill);
            canvas.DrawRoundRect(header, hStroke);

            using var tPaint = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = GoldAccent, IsAntialias = true };
            using var sPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = SKColors.White, IsAntialias = true };

            canvas.DrawText($"🧠 СВОЯ ИГРА: БРЕЙН-РИНГ #{session.Id}  •  БАНК: {session.PrizePool:N0} {currencySymbol}", 42f, 50f, tPaint);
            canvas.DrawText(ClipText(state.LastLog, sPaint, CanvasWidth - 84f), 42f, 78f, sPaint);

            // 4 Topics x 5 Point Columns
            var topicW = 270f;
            var cellW = (CanvasWidth - 48f - topicW - 50f) / 5f;
            var rowH = 60f;

            for (var t = 0; t < state.Topics.Count; t++)
            {
                var y = 114f + t * (rowH + 10f);
                var tRect = new SKRoundRect(new SKRect(24f, y, 24f + topicW, y + rowH), 12, 12);
                using var tFill = new SKPaint { Color = new SKColor(22, 36, 78, 235), IsAntialias = true };
                canvas.DrawRoundRect(tRect, tFill);
                canvas.DrawRoundRect(tRect, hStroke);

                using var topText = new SKPaint { Typeface = FontBold, TextSize = 15f, Color = SKColors.White, IsAntialias = true };
                canvas.DrawText(ClipText(state.Topics[t], topText, topicW - 24f), 38f, y + 36f, topText);

                for (var p = 0; p < 5; p++)
                {
                    var idx = t * 5 + p;
                    if (idx >= state.Cells.Count)
                        continue;

                    var cell = state.Cells[idx];
                    var cx = 24f + topicW + 10f + p * (cellW + 10f);
                    var cRect = new SKRoundRect(new SKRect(cx, y, cx + cellW, y + rowH), 12, 12);

                    var isActive = state.ActiveCellIndex == idx;
                    using var cFill = new SKPaint
                    {
                        Color = isActive
                            ? new SKColor(245, 158, 11, 80)
                            : cell.Answered
                                ? new SKColor(255, 255, 255, 8)
                                : new SKColor(26, 44, 96, 230),
                        IsAntialias = true
                    };
                    canvas.DrawRoundRect(cRect, cFill);
                    canvas.DrawRoundRect(cRect, hStroke);

                    if (!cell.Answered || isActive)
                    {
                        using var ptsPaint = new SKPaint
                        {
                            Typeface = FontBold,
                            TextSize = 22f,
                            Color = isActive ? SKColors.White : GoldAccent,
                            IsAntialias = true,
                            TextAlign = SKTextAlign.Center
                        };
                        canvas.DrawText($"{cell.Points}", cx + cellW / 2f, y + 38f, ptsPaint);
                    }
                }
            }

            // Scoreboard strip at bottom
            var botRect = new SKRoundRect(new SKRect(24, 404, CanvasWidth - 24, 540), 16, 16);
            canvas.DrawRoundRect(botRect, hFill);

            var pCount = Math.Max(1, players.Count);
            var pCardW = (CanvasWidth - 84f - (pCount - 1) * 12f) / pCount;
            for (var i = 0; i < players.Count; i++)
            {
                var pl = players[i];
                var px = 42f + i * (pCardW + 12f);
                using var pName = new SKPaint { Typeface = FontBold, TextSize = 15f, Color = SKColors.White, IsAntialias = true, TextAlign = SKTextAlign.Center };
                using var pScore = new SKPaint { Typeface = FontBold, TextSize = 22f, Color = CyanAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };

                canvas.DrawText(ClipText(pl.DisplayName, pName, pCardW - 10f), px + pCardW / 2f, 452f, pName);
                canvas.DrawText($"{pl.Score} очк.", px + pCardW / 2f, 492f, pScore);
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    public async Task<byte[]> RenderCodenamesBoardAsync(
        PartyGameSessionEntity session,
        bool showCaptainKeymap = false,
        CancellationToken ct = default)
    {
        var state = JsonSerializer.Deserialize<CodenamesGameStateDto>(session.StateJson) ?? new CodenamesGameStateDto();
        var isFinished = session.Status == "finished" || showCaptainKeymap;

        return await Task.Run(() =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(CanvasWidth, 580, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(BgDark);

            var turnAccent = state.CurrentTeam == "red" ? CrimsonAccent : CyanAccent;
            DrawCyberGridBackground(canvas, CanvasWidth, 580, turnAccent);

            var header = new SKRoundRect(new SKRect(24, 18, CanvasWidth - 24, 92), 16, 16);
            using var hFill = new SKPaint { Color = GlassPanel, IsAntialias = true };
            using var hStroke = new SKPaint { Color = turnAccent, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true };
            canvas.DrawRoundRect(header, hFill);
            canvas.DrawRoundRect(header, hStroke);

            var redLeft = state.Grid.Count(x => x.Color == "red" && !x.Revealed);
            var blueLeft = state.Grid.Count(x => x.Color == "blue" && !x.Revealed);

            using var tPaint = new SKPaint { Typeface = FontBold, TextSize = 21f, Color = SKColors.White, IsAntialias = true };
            using var sPaint = new SKPaint { Typeface = FontRegular, TextSize = 14f, Color = GoldAccent, IsAntialias = true };

            var modeTag = showCaptainKeymap ? "СЕКРЕТНАЯ КАРТА КАПИТАНОВ" : $"ОСТАЛОСЬ АГЕНТОВ: 🔴 {redLeft}  |  🔵 {blueLeft}";
            canvas.DrawText($"🗺️ КОДОВЫЕ ИМЕНА #{session.Id}  •  {modeTag}", 42f, 48f, tPaint);
            canvas.DrawText(ClipText(state.LastLog, sPaint, CanvasWidth - 84f), 42f, 74f, sPaint);

            var cellW = (CanvasWidth - 48f - 48f) / 5f;
            var cellH = 80f;

            for (var i = 0; i < Math.Min(25, state.Grid.Count); i++)
            {
                var cell = state.Grid[i];
                var r = i / 5;
                var c = i % 5;
                var x = 24f + c * (cellW + 12f);
                var y = 110f + r * (cellH + 10f);

                var revealColor = cell.Revealed || isFinished;
                var bgColor = !revealColor
                    ? new SKColor(28, 36, 56, 235)
                    : cell.Color switch
                    {
                        "red" => new SKColor(225, 29, 72, cell.Revealed ? (byte)225 : (byte)115),
                        "blue" => new SKColor(37, 99, 235, cell.Revealed ? (byte)225 : (byte)115),
                        "assassin" => new SKColor(15, 15, 20, 245),
                        _ => new SKColor(120, 113, 108, cell.Revealed ? (byte)210 : (byte)95)
                    };

                var borderColor = !revealColor
                    ? GlassStroke
                    : cell.Color switch
                    {
                        "red" => CrimsonAccent,
                        "blue" => CyanAccent,
                        "assassin" => GoldAccent,
                        _ => MutedText
                    };

                var rect = new SKRoundRect(new SKRect(x, y, x + cellW, y + cellH), 14, 14);
                using var cFill = new SKPaint { Color = bgColor, IsAntialias = true };
                using var cBorder = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = cell.Revealed ? 2.4f : 1.4f, IsAntialias = true };
                canvas.DrawRoundRect(rect, cFill);
                canvas.DrawRoundRect(rect, cBorder);

                using var numP = new SKPaint { Typeface = FontRegular, TextSize = 11f, Color = new SKColor(255, 255, 255, 130), IsAntialias = true };
                canvas.DrawText($"#{i + 1}", x + 10f, y + 20f, numP);

                using var wPaint = new SKPaint
                {
                    Typeface = FontBold,
                    TextSize = 15.5f,
                    Color = SKColors.White,
                    IsAntialias = true,
                    TextAlign = SKTextAlign.Center
                };
                canvas.DrawText(ClipText(cell.Word, wPaint, cellW - 16f), x + cellW / 2f, y + 48f, wPaint);

                if (cell.Revealed)
                {
                    using var openP = new SKPaint { Typeface = FontBold, TextSize = 10.5f, Color = GoldAccent, IsAntialias = true, TextAlign = SKTextAlign.Center };
                    canvas.DrawText("ОТКРЫТО", x + cellW / 2f, y + 68f, openP);
                }
            }

            using var img = surface.Snapshot();
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }, ct);
    }

    private static void DrawCyberGridBackground(SKCanvas canvas, int width, int height, SKColor glowColor)
    {
        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(width * 0.2f, height * 0.15f),
                width * 0.65f,
                [glowColor.WithAlpha(42), SKColors.Transparent],
                (float[]?)null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), glowPaint);

        using var gridPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 7),
            StrokeWidth = 1f,
            IsAntialias = false
        };

        for (var x = 0; x < width; x += 48)
            canvas.DrawLine(x, 0, x, height, gridPaint);
        for (var y = 0; y < height; y += 48)
            canvas.DrawLine(0, y, width, y, gridPaint);
    }

    private static string ClipText(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (paint.MeasureText(text) <= maxWidth)
            return text;

        var span = text.AsSpan();
        for (var len = span.Length - 1; len > 0; len--)
        {
            var candidate = string.Concat(span[..len], "...");
            if (paint.MeasureText(candidate) <= maxWidth)
                return candidate;
        }

        return "...";
    }
}

internal static class MafiaStateExtensions
{
    public static void DidVotesClear(this MafiaGameStateDto state, MafiaGameStateDto _)
    {
        state.DayVotes.Clear();
    }
}
