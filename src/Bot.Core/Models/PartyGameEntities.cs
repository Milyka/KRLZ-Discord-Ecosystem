namespace Bot.Models;

public sealed record PartyGameSessionEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong VoiceChannelId,
    ulong MessageId,
    ulong HostId,
    string GameType,
    long Bet,
    long PrizePool,
    string Status,
    string Phase,
    int RoundNum,
    ulong TurnUserId,
    string StateJson,
    string MutedUserIds,
    string WinnerSummary,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public PartyGameSessionEntity() : this(
        default!, default!, default!, default!, default!, default!,
        string.Empty, default!, default!, string.Empty, string.Empty,
        default!, default!, string.Empty, string.Empty, string.Empty,
        default!, default!, default!)
    { }
};

public sealed record PartyGamePlayerEntity(
    long Id,
    long SessionId,
    ulong GuildId,
    ulong UserId,
    string DisplayName,
    int SeatIndex,
    string RoleCode,
    string TeamCode,
    bool IsAlive,
    long BetPaid,
    int Score,
    string PlayerDataJson,
    DateTimeOffset JoinedAt
)
{
    public PartyGamePlayerEntity() : this(
        default!, default!, default!, default!, string.Empty,
        default!, string.Empty, string.Empty, default!, default!,
        default!, string.Empty, default!)
    { }
};

public sealed record RaidBossSessionEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    string BossCode,
    string BossName,
    int BossLevel,
    long MaxHp,
    long CurrentHp,
    long RewardPoolCoins,
    int RewardCrystals,
    string Status,
    string LastLog,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public RaidBossSessionEntity() : this(
        default!, default!, default!, default!, string.Empty, string.Empty,
        default!, default!, default!, default!, default!, string.Empty,
        string.Empty, default!, default!, default!)
    { }
};

public sealed record RaidBossHitEntity(
    long SessionId,
    ulong GuildId,
    ulong UserId,
    string DisplayName,
    string HeroClass,
    long TotalDamage,
    int HitsCount,
    DateTimeOffset LastHitAt
)
{
    public RaidBossHitEntity() : this(
        default!, default!, default!, string.Empty, string.Empty,
        default!, default!, default!)
    { }
};

public sealed record PartyGameContentEntity(
    long Id,
    ulong GuildId,
    string GameType,
    string Title,
    string Subtitle,
    string PayloadJson,
    DateTimeOffset CreatedAt
)
{
    public PartyGameContentEntity() : this(
        default!, default!, string.Empty, string.Empty, string.Empty, string.Empty, default!)
    { }
};

// --- DTOs for JSON state inside party_game_sessions & party_game_players ---

public sealed class MafiaGameStateDto
{
    public Dictionary<ulong, ulong> NightKillVotes { get; set; } = new();
    public ulong? DonCheckTargetId { get; set; }
    public ulong? CommissarTargetId { get; set; }
    public string CommissarMode { get; set; } = "check"; // "check" or "shoot"
    public ulong? DoctorTargetId { get; set; }
    public ulong? ManiacTargetId { get; set; }
    public ulong? MistressTargetId { get; set; }
    public Dictionary<ulong, ulong> DayVotes { get; set; } = new();
    public List<string> MafiaChatNotes { get; set; } = [];
    public List<string> ChronicleLogs { get; set; } = [];
    public string LastEventBanner { get; set; } = "Город засыпает... Просыпается Мафия.";
}

public sealed class BunkerTraitDto
{
    public string Category { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int SurvivalScore { get; set; } = 10;
    public bool Revealed { get; set; }
}

public sealed class BunkerPlayerStateDto
{
    public List<BunkerTraitDto> Traits { get; set; } = [];
    public string SpecialCardTitle { get; set; } = string.Empty;
    public string SpecialCardCode { get; set; } = string.Empty;
    public bool SpecialCardUsed { get; set; }
    public bool RevealedThisRound { get; set; }
}

public sealed class BunkerGameStateDto
{
    public string DisasterTitle { get; set; } = string.Empty;
    public string DisasterDescription { get; set; } = string.Empty;
    public string BunkerDescription { get; set; } = string.Empty;
    public int BunkerCapacity { get; set; } = 2;
    public Dictionary<ulong, ulong> ExileVotes { get; set; } = new();
    public List<string> EventLogs { get; set; } = [];
    public string EpilogueReport { get; set; } = string.Empty;
}

public sealed class SpyfallGameStateDto
{
    public string SecretLocation { get; set; } = string.Empty;
    public List<string> AllLocations { get; set; } = [];
    public ulong SpyUserId { get; set; }
    public Dictionary<ulong, string> PlayerRoles { get; set; } = new();
    public Dictionary<ulong, ulong> AccuseVotes { get; set; } = new();
    public string LastLog { get; set; } = string.Empty;
}

public sealed class LiarsBarPlayerDto
{
    public List<string> Hand { get; set; } = [];
    public int RevolverBulletSlot { get; set; } = 1; // 1..6
    public int RevolverShotsTaken { get; set; } = 0; // 0..6
}

public sealed class LiarsBarGameStateDto
{
    public string TableRank { get; set; } = "Q"; // Q, K, A
    public ulong? LastPlayerId { get; set; }
    public List<string> LastPlayedCards { get; set; } = [];
    public int PileCount { get; set; }
    public string LastActionLog { get; set; } = "Новый раунд за столом Бара Лжецов!";
}

public sealed class QuizQuestionCellDto
{
    public int TopicIndex { get; set; }
    public int PriceIndex { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int Points { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public bool Answered { get; set; }
}

public sealed class QuizGameStateDto
{
    public List<string> Topics { get; set; } = [];
    public List<QuizQuestionCellDto> Cells { get; set; } = [];
    public int? ActiveCellIndex { get; set; }
    public ulong? BuzzerUserId { get; set; }
    public string LastLog { get; set; } = "Выберите категорию и стоимость вопроса!";
}

public sealed class CodenamesCellDto
{
    public string Word { get; set; } = string.Empty;
    public string Color { get; set; } = "neutral"; // "red", "blue", "neutral", "assassin"
    public bool Revealed { get; set; }
}

public sealed class CodenamesGameStateDto
{
    public List<CodenamesCellDto> Grid { get; set; } = [];
    public string CurrentTeam { get; set; } = "red"; // "red" or "blue"
    public ulong RedCaptainId { get; set; }
    public ulong BlueCaptainId { get; set; }
    public string CurrentClue { get; set; } = string.Empty;
    public int GuessesRemaining { get; set; }
    public string LastLog { get; set; } = "Капитан красной команды даёт первую шифровку!";
}
