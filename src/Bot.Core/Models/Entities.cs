namespace Bot.Models;

public sealed record GuildSettingsEntity(
    ulong GuildId,
    string EmbedColorHex,
    string CurrencyName,
    string CurrencySymbol,
    long CustomRolePrice,
    ulong CustomRoleAnchorId,
    ulong ModLogChannelId,
    ulong TicketLogChannelId,
    ulong TicketPanelChannelId,
    ulong TicketCategoryId,
    ulong WelcomeChannelId,
    ulong SecurityLogChannelId,
    ulong AdminRoleId,
    ulong ModeratorRoleId,
    ulong MutedRoleId,
    ulong VerifiedRoleId,
    ulong QuarantineRoleId,
    int WarnThresholdMute,
    int WarnMuteMinutes,
    int WarnThresholdBan,
    int MaxOpenTickets,
    bool AntispamEnabled,
    int AntispamMaxMessages,
    int AntispamWindowSeconds,
    bool AntispamBlockInvites,
    bool AntispamBlockLinks,
    bool AntinukeEnabled,
    bool AntiraidEnabled,
    bool CaptchaEnabled,
    bool LockdownActive,
    DateTimeOffset UpdatedAt
)
{
    public GuildSettingsEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record UserProfileEntity(
    ulong GuildId,
    ulong UserId,
    long Balance,
    long Xp,
    int Level,
    long Messages,
    long VoiceSeconds,
    string Bio,
    string Title,
    string? Birthday,
    string SelectedBackground,
    DateTimeOffset? LastDailyAt,
    DateTimeOffset? LastWorkAt,
    DateTimeOffset CreatedAt
)
{
    public UserProfileEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record WarnEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ModeratorId,
    string Reason,
    bool Active,
    DateTimeOffset CreatedAt
)
{
    public WarnEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PunishmentEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ModeratorId,
    string Type,
    string Reason,
    DateTimeOffset? ExpiresAt,
    bool Active,
    DateTimeOffset CreatedAt
)
{
    public PunishmentEntity() : this(0, 0, 0, 0, string.Empty, string.Empty, null, false, default) { }
};

public sealed record TicketEntity(
    long Id,
    ulong GuildId,
    int TicketNumber,
    ulong ChannelId,
    ulong OwnerId,
    ulong? ClaimedById,
    string Category,
    string Subject,
    string Status,
    string? CloseReason,
    int? Rating,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt
)
{
    public TicketEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record TicketMessageEntity(
    long Id,
    long TicketId,
    ulong AuthorId,
    string AuthorName,
    string Content,
    string? Attachments,
    DateTimeOffset CreatedAt
)
{
    public TicketMessageEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record CustomRoleEntity(
    long Id,
    ulong GuildId,
    ulong OwnerId,
    ulong RoleId,
    string Name,
    string ColorHex,
    DateTimeOffset CreatedAt
)
{
    public CustomRoleEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record ShopItemEntity(
    long Id,
    ulong GuildId,
    string Name,
    string Description,
    string ItemType,
    long Price,
    ulong? RoleId,
    string? Payload,
    bool Enabled,
    DateTimeOffset CreatedAt
)
{
    public ShopItemEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record PurchaseEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    long ItemId,
    long PricePaid,
    DateTimeOffset PurchasedAt
)
{
    public PurchaseEntity() : this(default!, default!, default!, default!, default!, default!) { }
};

public sealed record TransactionEntity(
    long Id,
    ulong GuildId,
    ulong? FromUserId,
    ulong? ToUserId,
    long Amount,
    string TxType,
    string Description,
    DateTimeOffset CreatedAt
)
{
    public TransactionEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record VoiceSessionEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ChannelId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long DurationSeconds
)
{
    public VoiceSessionEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record VoiceBanEntity(
    ulong GuildId,
    ulong UserId,
    ulong ModeratorId,
    string Reason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt
)
{
    public VoiceBanEntity() : this(default!, default!, default!, default!, default!, default!) { }
};

public sealed record MarriageEntity(
    long Id,
    ulong GuildId,
    ulong User1Id,
    ulong User2Id,
    DateTimeOffset MarriedAt
)
{
    public MarriageEntity() : this(default!, default!, default!, default!, default!) { }
};

public sealed record ReminderEntity(
    long Id,
    ulong GuildId,
    ulong UserId,
    ulong ChannelId,
    string Message,
    DateTimeOffset TriggerAt,
    bool Completed,
    DateTimeOffset CreatedAt
)
{
    public ReminderEntity() : this(default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record SecurityWhitelistEntity(
    ulong GuildId,
    ulong TargetId,
    string TargetType,
    ulong AddedById,
    DateTimeOffset CreatedAt
)
{
    public SecurityWhitelistEntity() : this(default!, default!, default!, default!, default!) { }
};

public sealed record SecurityEventEntity(
    long Id,
    ulong GuildId,
    ulong OffenderId,
    string EventType,
    string Details,
    string ActionTaken,
    DateTimeOffset CreatedAt
)
{
    public SecurityEventEntity() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record GuildBackupEntity(
    long Id,
    ulong GuildId,
    string SnapshotName,
    ulong CreatedById,
    string PayloadJson,
    DateTimeOffset CreatedAt
)
{
    public GuildBackupEntity() : this(default!, default!, default!, default!, default!, default!) { }
};

public sealed record BackupRoleDto(
    ulong OriginalId,
    string Name,
    uint ColorRaw,
    bool IsHoisted,
    bool IsMentionable,
    ulong PermissionsRaw,
    int Position
)
{
    public BackupRoleDto() : this(default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record BackupPermissionOverwriteDto(
    ulong TargetId,
    int TargetType,
    ulong AllowValue,
    ulong DenyValue
)
{
    public BackupPermissionOverwriteDto() : this(default!, default!, default!, default!) { }
};

public sealed record BackupChannelDto(
    ulong OriginalId,
    string Name,
    string ChannelType,
    int Position,
    string? CategoryName,
    string? Topic,
    int? SlowModeInterval,
    int? Bitrate,
    int? UserLimit,
    List<BackupPermissionOverwriteDto> Overwrites
)
{
    public BackupChannelDto() : this(default!, default!, default!, default!, default!, default!, default!, default!, default!, default!) { }
};

public sealed record BackupSnapshotDto(
    ulong GuildId,
    string GuildName,
    DateTimeOffset CreatedAt,
    List<BackupRoleDto> Roles,
    List<BackupChannelDto> Channels
)
{
    public BackupSnapshotDto() : this(default!, default!, default!, default!, default!) { }
};

public sealed record LeaderboardEntryDto(
    ulong UserId,
    long Value,
    int Level
)
{
    public LeaderboardEntryDto() : this(default!, default!, default!) { }
};
