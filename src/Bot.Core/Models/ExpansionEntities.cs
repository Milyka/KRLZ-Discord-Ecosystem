namespace Bot.Models;

public sealed record ClanEntity(
    long Id,
    ulong GuildId,
    string Name,
    string Tag,
    string Description,
    ulong LeaderId,
    ulong RoleId,
    ulong ChannelId,
    long Treasury,
    long Points,
    int MemberLimit,
    int OfficerSlots,
    int EarnBonusPercent,
    string ColorHex,
    string BadgeCode,
    DateTimeOffset CreatedAt
)
{
    public ClanEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ClanMemberEntity(
    ulong GuildId,
    long ClanId,
    ulong UserId,
    string Rank,
    long ContributedCoins,
    long ContributedPoints,
    DateTimeOffset JoinedAt
)
{
    public ClanMemberEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ClanWarEntity(
    long Id,
    ulong GuildId,
    long ChallengerClanId,
    long DefenderClanId,
    long ChallengerScore,
    long DefenderScore,
    int WagerPercent,
    int DurationDays,
    string Status,
    long? WinnerClanId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt
)
{
    public ClanWarEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ClanUpgradeEntity(
    long Id,
    ulong GuildId,
    long ClanId,
    string UpgradeType,
    int Level,
    ulong PurchasedById,
    long PricePaid,
    DateTimeOffset PurchasedAt
)
{
    public ClanUpgradeEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ItemDefinitionEntity(
    long Id,
    ulong GuildId,
    string Code,
    string Name,
    string Description,
    string Category,
    string Rarity,
    int MaxStack,
    long BasePrice,
    long SellPrice,
    bool Tradeable,
    bool Usable,
    string? EffectJson,
    string? RecipeJson,
    DateTimeOffset CreatedAt
)
{
    public ItemDefinitionEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record InventoryItemEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    long ItemDefId,
    int Quantity,
    string? MetadataJson,
    DateTimeOffset UpdatedAt
)
{
    public InventoryItemEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record InventoryStackView(
    InventoryItemEntity Inventory,
    ItemDefinitionEntity Definition
)
{
    public InventoryStackView() : this(default!, default!) { }
};

public sealed record CraftIngredientDto(
    string ItemCode,
    int Quantity
)
{
    public CraftIngredientDto() : this(default!, default!) { }
};

public sealed record ItemEffectDto(
    string EffectType,
    int MultiplierPercent,
    int DurationMinutes
)
{
    public ItemEffectDto() : this(default!, default!, default!) { }
};

public sealed record AuctionEntity(
    long Id,
    ulong GuildId,
    ulong SellerId,
    string LotType,
    long? ItemDefId,
    int ItemQuantity,
    ulong? CustomRoleId,
    string LotTitle,
    long StartPrice,
    long MinStep,
    long? BuyoutPrice,
    long CurrentBid,
    ulong? HighestBidderId,
    string Status,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt
)
{
    public AuctionEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record AuctionBidEntity(
    long Id,
    long AuctionId,
    ulong GuildId,
    ulong BidderId,
    long Amount,
    bool Refunded,
    DateTimeOffset CreatedAt
)
{
    public AuctionBidEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record AchievementEntity(
    long Id,
    ulong GuildId,
    string Code,
    string Name,
    string Description,
    string EventType,
    long TargetValue,
    string Rarity,
    string BadgeIcon,
    long RewardCoins,
    long RewardXp,
    DateTimeOffset CreatedAt
)
{
    public AchievementEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record UserAchievementEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    long AchievementId,
    long CurrentValue,
    bool Unlocked,
    int? EquippedSlot,
    DateTimeOffset? UnlockedAt,
    DateTimeOffset UpdatedAt
)
{
    public UserAchievementEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record UserAchievementView(
    AchievementEntity Achievement,
    UserAchievementEntity Progress
)
{
    public UserAchievementView() : this(default!, default!) { }
};

public sealed record QuestEntity(
    long Id,
    ulong GuildId,
    string Code,
    string Name,
    string Description,
    string QuestPeriod,
    string ObjectiveType,
    long TargetValue,
    long RewardCoins,
    long RewardXp,
    int RewardBpXp,
    long? RewardItemDefId,
    bool Enabled,
    DateTimeOffset CreatedAt
)
{
    public QuestEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record UserQuestEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    long QuestId,
    long Progress,
    bool Completed,
    bool Claimed,
    string AssignedDate,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt
)
{
    public UserQuestEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record BattlePassSeasonEntity(
    long Id,
    ulong GuildId,
    string Name,
    int MaxLevel,
    int XpPerLevel,
    long PremiumPrice,
    bool Active,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string RewardsJson
)
{
    public BattlePassSeasonEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record BattlePassProgressEntity(
    ulong GuildId,
    long SeasonId,
    ulong UserId,
    long Xp,
    int Level,
    bool IsPremium,
    string ClaimedFreeLevels,
    string ClaimedPremiumLevels,
    DateTimeOffset UpdatedAt
)
{
    public BattlePassProgressEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PrivateVoiceRoomEntity(
    ulong ChannelId,
    ulong GuildId,
    ulong OwnerId,
    string RoomName,
    int UserLimit,
    bool IsLocked,
    bool IsHidden,
    string BannedUserIds,
    DateTimeOffset? EmptySince,
    DateTimeOffset CreatedAt
)
{
    public PrivateVoiceRoomEntity() : this(0, 0, 0, string.Empty, 0, false, false, string.Empty, null, default) { }
};

public sealed record VoiceHubConfigEntity(
    ulong GuildId,
    ulong HubChannelId,
    ulong CategoryId,
    ulong ControlChannelId,
    string NameTemplate,
    int DefaultLimit,
    int EmptyTimeoutSeconds,
    DateTimeOffset UpdatedAt
)
{
    public VoiceHubConfigEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record MessageLogEntity(
    ulong MessageId,
    ulong GuildId,
    ulong ChannelId,
    ulong AuthorId,
    string AuthorName,
    string Content,
    string? Attachments,
    bool IsDeleted,
    string? EditedContent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public MessageLogEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record NicknameHistoryEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    string? OldNickname,
    string? NewNickname,
    DateTimeOffset ChangedAt
)
{
    public NicknameHistoryEntity() : this(default!, default!, default!, default!, default!, default!) { }
};

public sealed record VerificationApplicationEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    string AnswersJson,
    string Status,
    ulong? ReviewerId,
    string? ReviewReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt
)
{
    public VerificationApplicationEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ReactionRoleEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    string PanelTitle,
    string Mode,
    string ComponentType,
    string MappingsJson,
    DateTimeOffset CreatedAt
)
{
    public ReactionRoleEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ReactionRoleMappingDto(
    ulong RoleId,
    string Label,
    string? Description
)
{
    public ReactionRoleMappingDto() : this(default!, default!, default!) { }
};

public sealed record GiveawayEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong HostId,
    string Prize,
    int WinnerCount,
    ulong? RequiredRoleId,
    int MinLevel,
    long MinBalance,
    string Status,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt
)
{
    public GiveawayEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record GiveawayEntryEntity(
    long GiveawayId,
    ulong GuildId,
    ulong UserId,
    DateTimeOffset EnteredAt
)
{
    public GiveawayEntryEntity() : this(default!, default!, default!, default!) { }
};

public sealed record GiveawayWinnerEntity(
    long Id,
    long GiveawayId,
    ulong GuildId,
    ulong UserId,
    bool IsReroll,
    DateTimeOffset SelectedAt
)
{
    public GiveawayWinnerEntity() : this(default!, default!, default!, default!, default!, default!) { }
};

public sealed record DbBackupLogEntity(
    long Id,
    ulong GuildId,
    string FileName,
    string FilePath,
    long SizeBytes,
    ulong TriggeredById,
    bool SentToChannel,
    DateTimeOffset CreatedAt
)
{
    public DbBackupLogEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record BlackjackSessionEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ChannelId,
    ulong MessageId,
    long Bet,
    long InsuranceBet,
    string DeckJson,
    string PlayerHandsJson,
    string DealerHandJson,
    int ActiveHandIndex,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public BlackjackSessionEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record RouletteRoundEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    string Status,
    int? RolledNumber,
    string? RolledColor,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt
)
{
    public RouletteRoundEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record RouletteBetEntity(
    long Id,
    long RoundId,
    ulong GuildId,
    ulong UserId,
    string BetType,
    string BetValue,
    long Amount,
    string Status,
    DateTimeOffset CreatedAt
)
{
    public RouletteBetEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record JackpotRoundEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    string Status,
    ulong? WinnerId,
    long TotalPool,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt
)
{
    public JackpotRoundEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record JackpotBetEntity(
    long Id,
    long RoundId,
    ulong GuildId,
    ulong UserId,
    long Amount,
    string Status,
    DateTimeOffset CreatedAt
)
{
    public JackpotBetEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record CrashBetEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ChannelId,
    long Amount,
    double CrashPoint,
    double? CashedOutMultiplier,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public CrashBetEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PollV2Entity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong CreatorId,
    string Question,
    string OptionsJson,
    bool IsAnonymous,
    string Status,
    DateTimeOffset EndsAt,
    DateTimeOffset CreatedAt
)
{
    public PollV2Entity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PollV2VoteEntity(
    long PollId,
    ulong GuildId,
    ulong UserId,
    int OptionIndex,
    DateTimeOffset VotedAt
)
{
    public PollV2VoteEntity() : this(default!, default!, default!, default!, default!) { }
};

public sealed record UserReputationEntity(
    ulong GuildId,
    ulong UserId,
    long Reputation,
    int PositiveCount,
    int NegativeCount,
    string? AccentColorHex,
    DateTimeOffset? LastGivenAt,
    DateTimeOffset UpdatedAt
)
{
    public UserReputationEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ReputationLogEntity(
    long Id,
    ulong GuildId,
    ulong FromUserId,
    ulong ToUserId,
    int Delta,
    string Comment,
    DateTimeOffset CreatedAt
)
{
    public ReputationLogEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PrivateVoicePresetEntity(
    ulong GuildId,
    ulong UserId,
    string RoomName,
    int UserLimit,
    bool IsLocked,
    bool IsHidden,
    string TrustedUserIds,
    string BannedUserIds,
    DateTimeOffset UpdatedAt
)
{
    public PrivateVoicePresetEntity() : this(default!, default!, string.Empty, default!, default!, default!, string.Empty, string.Empty, default!) { }
};

public sealed record FamilyProfileEntity(
    long MarriageId,
    ulong GuildId,
    string FamilyName,
    long FamilyBalance,
    long LoveXp,
    int LoveLevel,
    DateTimeOffset UpdatedAt
)
{
    public FamilyProfileEntity() : this(default!, default!, string.Empty, default!, default!, default!, default!) { }
};

public sealed record FamilyMemberEntity(
    long Id,
    long MarriageId,
    ulong GuildId,
    ulong ChildUserId,
    string RoleLabel,
    DateTimeOffset AdoptedAt
)
{
    public FamilyMemberEntity() : this(default!, default!, default!, default!, string.Empty, default!) { }
};

public sealed record BankAccountEntity(
    ulong GuildId,
    ulong UserId,
    long DepositBalance,
    long Crystals,
    long TotalInterestEarned,
    DateTimeOffset LastInterestAt,
    DateTimeOffset UpdatedAt
)
{
    public BankAccountEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record UserPetEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    string PetCode,
    string Name,
    string Rarity,
    int Level,
    long Xp,
    int Satiety,
    bool IsActive,
    DateTimeOffset? ExpeditionEndsAt,
    DateTimeOffset? LastFedAt,
    DateTimeOffset CreatedAt
)
{
    public UserPetEntity() : this(default!, default!, default!, string.Empty, string.Empty, string.Empty, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record DuelSessionEntity(
    long Id,
    ulong GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong ChallengerId,
    ulong OpponentId,
    long Bet,
    int ChallengerHp,
    int OpponentHp,
    ulong TurnUserId,
    int RoundNum,
    string LastLog,
    string Status,
    ulong? WinnerId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt
)
{
    public DuelSessionEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, string.Empty, string.Empty, default!, default!, default!) { }
};

public sealed record MinesSessionEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ChannelId,
    ulong MessageId,
    long Bet,
    int BombCount,
    string BombsJson,
    string RevealedJson,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public MinesSessionEntity() : this(default!, default!, default!, default!, default!, default!, default!, string.Empty, string.Empty, string.Empty, default!, default!) { }
};
