namespace Bot.Models;

public sealed class BotSettings
{
    public string Token { get; set; } = "PUT_YOUR_DISCORD_BOT_TOKEN_HERE";
    public ulong GuildId { get; set; } = 123456789012345678;
    public string DatabaseConnectionString { get; set; } = "Data Source=krlz.db;Cache=Shared";
    public string EmbedColorHex { get; set; } = "#5865F2";
    public string FooterText { get; set; } = "KRLZ Server System";
    public string CurrencyName { get; set; } = "KRLZ Coin";
    public string CurrencySymbol { get; set; } = "KR";
    public long CustomRolePrice { get; set; } = 5000;
    public ulong CustomRoleAnchorRoleId { get; set; }
    public ChannelSettings Channels { get; set; } = new();
    public RoleSettings Roles { get; set; } = new();
    public EconomySettings Economy { get; set; } = new();
    public ModerationSettings Moderation { get; set; } = new();
    public AntispamSettings Antispam { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public ClanSettings Clans { get; set; } = new();
    public AuctionSettings Auction { get; set; } = new();
    public CasinoSettings Casino { get; set; } = new();
    public FishingSettings Fishing { get; set; } = new();
    public BattlePassSettings BattlePass { get; set; } = new();
    public PrivateVoiceSettings PrivateVoice { get; set; } = new();
    public VerificationSettings Verification { get; set; } = new();
    public AutomodSettings Automod { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public DatabaseBackupSettings DatabaseBackup { get; set; } = new();
    public SocialExpansionSettings Social { get; set; } = new();
    public BankSettings Bank { get; set; } = new();
    public PetSettings Pets { get; set; } = new();
    public WebPanelSettings WebPanel { get; set; } = new();
    public bool EnableMessageContentIntent { get; set; } = false;
}

public sealed class ChannelSettings
{
    public ulong ModLogChannelId { get; set; }
    public ulong TicketLogChannelId { get; set; }
    public ulong TicketPanelChannelId { get; set; }
    public ulong TicketCategoryId { get; set; }
    public ulong WelcomeChannelId { get; set; }
    public ulong SecurityLogChannelId { get; set; }
}

public sealed class RoleSettings
{
    public ulong AdminRoleId { get; set; }
    public ulong ModeratorRoleId { get; set; }
    public ulong MutedRoleId { get; set; }
    public ulong VerifiedRoleId { get; set; }
    public ulong QuarantineRoleId { get; set; }
}

public sealed class EconomySettings
{
    public long DailyAmount { get; set; } = 350;
    public int DailyCooldownSeconds { get; set; } = 86400;
    public long WorkMinAmount { get; set; } = 80;
    public long WorkMaxAmount { get; set; } = 250;
    public int WorkCooldownSeconds { get; set; } = 3600;
    public int SlotsCooldownSeconds { get; set; } = 10;
    public long MessageRewardAmount { get; set; } = 5;
    public int MessageCooldownSeconds { get; set; } = 45;
    public long VoiceRewardPerMinute { get; set; } = 3;
    public long XpPerMessage { get; set; } = 15;
    public long XpPerVoiceMinute { get; set; } = 10;
}

public sealed class ModerationSettings
{
    public int WarnThresholdMute { get; set; } = 3;
    public int WarnMuteDurationMinutes { get; set; } = 120;
    public int WarnThresholdBan { get; set; } = 5;
    public int MaxOpenTicketsPerUser { get; set; } = 2;
}

public sealed class AntispamSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxMessagesInWindow { get; set; } = 6;
    public int WindowSeconds { get; set; } = 5;
    public double MaxSimilarityScore { get; set; } = 0.85;
    public double MaxCapsRatio { get; set; } = 0.75;
    public int MaxEmojisPerMessage { get; set; } = 8;
    public int MaxMentionsPerMessage { get; set; } = 5;
    public bool BlockDiscordInvites { get; set; } = true;
    public bool BlockExternalLinks { get; set; } = true;
    public List<string> AllowedDomains { get; set; } =
    [
        "discord.com",
        "discordapp.com",
        "tenor.com",
        "giphy.com",
        "youtube.com",
        "youtu.be",
        "github.com"
    ];
    public List<ulong> WhitelistedRoleIds { get; set; } = [];
    public List<ulong> WhitelistedChannelIds { get; set; } = [];
}

public sealed class SecuritySettings
{
    public bool AntiNukeEnabled { get; set; } = true;
    public bool AntiRaidEnabled { get; set; } = true;
    public bool CaptchaEnabled { get; set; } = true;
    public int AutoBackupHours { get; set; } = 12;
    public int MaxChannelOpsPerMinute { get; set; } = 3;
    public int MaxRoleOpsPerMinute { get; set; } = 3;
    public int MaxBanKickOpsPerMinute { get; set; } = 4;
    public int MaxWebhookOpsPerMinute { get; set; } = 2;
    public int RaidJoinThreshold { get; set; } = 8;
    public int RaidWindowSeconds { get; set; } = 15;
    public int MinAccountAgeDays { get; set; } = 7;
    public bool BlockDefaultAvatars { get; set; }
}

public sealed class ClanSettings
{
    public long CreationPrice { get; set; } = 15000;
    public ulong ClanCategoryId { get; set; }
    public int BaseMemberLimit { get; set; } = 15;
    public int BaseOfficerSlots { get; set; } = 2;
    public long UpgradeMemberLimitPrice { get; set; } = 5000;
    public long UpgradeEarnBonusPrice { get; set; } = 8000;
    public long UpgradeColorPrice { get; set; } = 3000;
    public long UpgradeOfficerSlotPrice { get; set; } = 4500;
    public int WarTreasuryStealPercent { get; set; } = 20;
    public int PointsPerMessage { get; set; } = 2;
    public int PointsPerVoiceMinute { get; set; } = 3;
    public int PointsPerWarWin { get; set; } = 500;
}

public sealed class AuctionSettings
{
    public ulong ChannelId { get; set; } = 1566933458171994242;
    public int CommissionPercent { get; set; } = 5;
    public int AntiSnipeWindowSeconds { get; set; } = 120;
    public int AntiSnipeExtendSeconds { get; set; } = 180;
    public int MaxActiveLotsPerUser { get; set; } = 5;
}

public sealed class CasinoSettings
{
    public long MinBet { get; set; } = 25;
    public long MaxBet { get; set; } = 50000;
    public long DailyLossLimit { get; set; } = 100000;
    public int CooldownSeconds { get; set; } = 8;
    public int RouletteRoundSeconds { get; set; } = 30;
    public int JackpotRoundSeconds { get; set; } = 45;
    public double CrashHouseEdge { get; set; } = 0.04;
    public int BlackjackAbandonedMinutes { get; set; } = 15;
}

public sealed class FishLootWeightConfig
{
    public string Code { get; set; } = "perch";
    public string Rarity { get; set; } = "common";
    public int BaseWeight { get; set; } = 60;
    public int RarityBonusWeightMultiplier { get; set; }
}

public sealed class FishingSettings
{
    public int CooldownSeconds { get; set; } = 30;
    public int HookReactionSeconds { get; set; } = 12;
    public double BaseCatchChance { get; set; } = 0.65;
    public double PearlEventChance { get; set; } = 0.18;
    public double OreEventChance { get; set; } = 0.35;
    public double TreasureEventChance { get; set; } = 0.45;
    public List<FishLootWeightConfig> LootTable { get; set; } =
    [
        new() { Code = "perch", Rarity = "common", BaseWeight = 550, RarityBonusWeightMultiplier = 0 },
        new() { Code = "pike", Rarity = "rare", BaseWeight = 280, RarityBonusWeightMultiplier = 400 },
        new() { Code = "electric_eel", Rarity = "epic", BaseWeight = 130, RarityBonusWeightMultiplier = 600 },
        new() { Code = "golden_carp", Rarity = "legendary", BaseWeight = 40, RarityBonusWeightMultiplier = 350 }
    ];
}

public sealed class BattlePassSettings
{
    public long PremiumPrice { get; set; } = 10000;
    public int DefaultSeasonDays { get; set; } = 45;
    public int MaxLevel { get; set; } = 50;
    public int XpPerLevel { get; set; } = 500;
    public int BpXpPerMessage { get; set; } = 10;
    public int BpXpPerVoiceMinute { get; set; } = 8;
}

public sealed class PrivateVoiceSettings
{
    public ulong HubVoiceChannelId { get; set; }
    public ulong PrivateRoomsCategoryId { get; set; }
    public ulong ControlTextChannelId { get; set; }
    public string RoomNameTemplate { get; set; } = "Комната {user}";
    public int EmptyRoomDeleteSeconds { get; set; } = 30;
}

public sealed class VerificationSettings
{
    public string Mode { get; set; } = "captcha";
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutMinutesOnFail { get; set; } = 30;
    public bool KickOnRepeatedFail { get; set; }
    public ulong ApplicationReviewChannelId { get; set; }
}

public sealed class AutomodSettings
{
    public bool Enabled { get; set; } = true;
    public int ToxicityWarnThreshold { get; set; } = 50;
    public int ToxicityMuteThreshold { get; set; } = 85;
    public int MuteDurationMinutes { get; set; } = 30;
    public List<string> BlockedWords { get; set; } = [];
    public List<string> BlockedRegexPatterns { get; set; } = [];
    public Dictionary<string, int> ToxicityLexicon { get; set; } = new()
    {
        ["клоун"] = 25,
        ["мусор"] = 30,
        ["бездарь"] = 40,
        ["скам"] = 45,
        ["дегенерат"] = 60
    };
}

public sealed class LoggingSettings
{
    public ulong MessageLogChannelId { get; set; }
    public ulong VoiceLogChannelId { get; set; }
    public ulong MemberLogChannelId { get; set; }
    public ulong ChannelEventsLogChannelId { get; set; }
    public int RetentionDays { get; set; } = 30;
}

public sealed class DatabaseBackupSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int KeepLocalCopies { get; set; } = 7;
    public ulong BackupChannelId { get; set; }
    public string BackupDirectory { get; set; } = "backups";
}

public sealed class SocialExpansionSettings
{
    public int ReputationCooldownHours { get; set; } = 12;
    public int MinLevelForReputation { get; set; } = 2;
    public int MaxAdoptedChildren { get; set; } = 4;
    public long AdoptionFee { get; set; } = 1500;
    public int FamilyLoveXpPerVoiceMinute { get; set; } = 5;
    public long CardColorChangePrice { get; set; } = 1000;
}

public sealed class BankSettings
{
    public double DailyInterestPercent { get; set; } = 1.5;
    public long MaxInterestCapPerDay { get; set; } = 5000;
    public long MinDepositAmount { get; set; } = 100;
    public long CrystalBuyRate { get; set; } = 250;
    public long CrystalSellRate { get; set; } = 200;
}

public sealed class PetSettings
{
    public long AdoptPrice { get; set; } = 4500;
    public long FeedCost { get; set; } = 120;
    public int ExpeditionHours { get; set; } = 4;
    public long ExpeditionBaseCoins { get; set; } = 450;
    public long ExpeditionBaseXp { get; set; } = 220;
}

public sealed class WebPanelSettings
{
    public string ListenUrl { get; set; } = "http://0.0.0.0:5080";
    public string DiscordClientId { get; set; } = "PUT_DISCORD_CLIENT_ID";
    public string DiscordClientSecret { get; set; } = "PUT_DISCORD_CLIENT_SECRET";
    public string RedirectUri { get; set; } = "http://localhost:5080/auth/callback";
    public string AdminApiToken { get; set; } = "krlz-secret-panel-key";
}
