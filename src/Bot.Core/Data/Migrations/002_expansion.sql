PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS clans (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    tag TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT 'Клан сервера KRLZ',
    leader_id INTEGER NOT NULL,
    role_id INTEGER NOT NULL DEFAULT 0,
    channel_id INTEGER NOT NULL DEFAULT 0,
    treasury INTEGER NOT NULL DEFAULT 0 CHECK (treasury >= 0),
    points INTEGER NOT NULL DEFAULT 0 CHECK (points >= 0),
    member_limit INTEGER NOT NULL DEFAULT 15,
    officer_slots INTEGER NOT NULL DEFAULT 2,
    earn_bonus_percent INTEGER NOT NULL DEFAULT 0,
    color_hex TEXT NOT NULL DEFAULT '#5865F2',
    badge_code TEXT NOT NULL DEFAULT 'SHIELD',
    created_at TEXT NOT NULL,
    UNIQUE (guild_id, name),
    UNIQUE (guild_id, tag)
);

CREATE INDEX IF NOT EXISTS idx_clans_guild_points ON clans (guild_id, points DESC);
CREATE INDEX IF NOT EXISTS idx_clans_guild_treasury ON clans (guild_id, treasury DESC);

CREATE TABLE IF NOT EXISTS clan_members (
    guild_id INTEGER NOT NULL,
    clan_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    rank TEXT NOT NULL DEFAULT 'member',
    contributed_coins INTEGER NOT NULL DEFAULT 0,
    contributed_points INTEGER NOT NULL DEFAULT 0,
    joined_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id),
    FOREIGN KEY (clan_id) REFERENCES clans (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_clan_members_clan ON clan_members (clan_id, rank);

CREATE TABLE IF NOT EXISTS clan_wars (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    challenger_clan_id INTEGER NOT NULL,
    defender_clan_id INTEGER NOT NULL,
    challenger_score INTEGER NOT NULL DEFAULT 0,
    defender_score INTEGER NOT NULL DEFAULT 0,
    wager_percent INTEGER NOT NULL DEFAULT 15,
    duration_days INTEGER NOT NULL DEFAULT 3,
    status TEXT NOT NULL DEFAULT 'pending',
    winner_clan_id INTEGER NULL,
    started_at TEXT NULL,
    ends_at TEXT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (challenger_clan_id) REFERENCES clans (id) ON DELETE CASCADE,
    FOREIGN KEY (defender_clan_id) REFERENCES clans (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_clan_wars_guild_status ON clan_wars (guild_id, status, ends_at);

CREATE TABLE IF NOT EXISTS clan_upgrades (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    clan_id INTEGER NOT NULL,
    upgrade_type TEXT NOT NULL,
    level INTEGER NOT NULL DEFAULT 1,
    purchased_by_id INTEGER NOT NULL,
    price_paid INTEGER NOT NULL,
    purchased_at TEXT NOT NULL,
    FOREIGN KEY (clan_id) REFERENCES clans (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_clan_upgrades_clan ON clan_upgrades (clan_id, upgrade_type);

CREATE TABLE IF NOT EXISTS item_definitions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    category TEXT NOT NULL,
    rarity TEXT NOT NULL DEFAULT 'common',
    max_stack INTEGER NOT NULL DEFAULT 99 CHECK (max_stack >= 1),
    base_price INTEGER NOT NULL DEFAULT 50 CHECK (base_price >= 0),
    sell_price INTEGER NOT NULL DEFAULT 25 CHECK (sell_price >= 0),
    tradeable INTEGER NOT NULL DEFAULT 1,
    usable INTEGER NOT NULL DEFAULT 0,
    effect_json TEXT NULL,
    recipe_json TEXT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (guild_id, code)
);

CREATE INDEX IF NOT EXISTS idx_item_definitions_guild_cat ON item_definitions (guild_id, category, rarity);

CREATE TABLE IF NOT EXISTS inventory_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    item_def_id INTEGER NOT NULL,
    quantity INTEGER NOT NULL DEFAULT 1 CHECK (quantity >= 0),
    metadata_json TEXT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (guild_id, user_id, item_def_id),
    FOREIGN KEY (item_def_id) REFERENCES item_definitions (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_inventory_items_user ON inventory_items (guild_id, user_id);

CREATE TABLE IF NOT EXISTS auctions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    seller_id INTEGER NOT NULL,
    lot_type TEXT NOT NULL,
    item_def_id INTEGER NULL,
    item_quantity INTEGER NOT NULL DEFAULT 1,
    custom_role_id INTEGER NULL,
    lot_title TEXT NOT NULL,
    start_price INTEGER NOT NULL CHECK (start_price > 0),
    min_step INTEGER NOT NULL CHECK (min_step > 0),
    buyout_price INTEGER NULL,
    current_bid INTEGER NOT NULL DEFAULT 0,
    highest_bidder_id INTEGER NULL,
    status TEXT NOT NULL DEFAULT 'active',
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_auctions_guild_status ON auctions (guild_id, status, ends_at);

CREATE TABLE IF NOT EXISTS auction_bids (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    auction_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    bidder_id INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK (amount > 0),
    refunded INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    FOREIGN KEY (auction_id) REFERENCES auctions (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_auction_bids_auction ON auction_bids (auction_id, id DESC);

CREATE TABLE IF NOT EXISTS achievements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    event_type TEXT NOT NULL,
    target_value INTEGER NOT NULL CHECK (target_value > 0),
    rarity TEXT NOT NULL DEFAULT 'common',
    badge_icon TEXT NOT NULL DEFAULT 'STAR',
    reward_coins INTEGER NOT NULL DEFAULT 100,
    reward_xp INTEGER NOT NULL DEFAULT 50,
    created_at TEXT NOT NULL,
    UNIQUE (guild_id, code)
);

CREATE INDEX IF NOT EXISTS idx_achievements_guild_event ON achievements (guild_id, event_type);

CREATE TABLE IF NOT EXISTS user_achievements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    achievement_id INTEGER NOT NULL,
    current_value INTEGER NOT NULL DEFAULT 0,
    unlocked INTEGER NOT NULL DEFAULT 0,
    equipped_slot INTEGER NULL,
    unlocked_at TEXT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (guild_id, user_id, achievement_id),
    FOREIGN KEY (achievement_id) REFERENCES achievements (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_user_achievements_user ON user_achievements (guild_id, user_id, unlocked);

CREATE TABLE IF NOT EXISTS quests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    quest_period TEXT NOT NULL,
    objective_type TEXT NOT NULL,
    target_value INTEGER NOT NULL CHECK (target_value > 0),
    reward_coins INTEGER NOT NULL DEFAULT 150,
    reward_xp INTEGER NOT NULL DEFAULT 100,
    reward_bp_xp INTEGER NOT NULL DEFAULT 100,
    reward_item_def_id INTEGER NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    UNIQUE (guild_id, code)
);

CREATE INDEX IF NOT EXISTS idx_quests_guild_period ON quests (guild_id, quest_period, enabled);

CREATE TABLE IF NOT EXISTS user_quests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    progress INTEGER NOT NULL DEFAULT 0,
    completed INTEGER NOT NULL DEFAULT 0,
    claimed INTEGER NOT NULL DEFAULT 0,
    assigned_date TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY (quest_id) REFERENCES quests (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_user_quests_user ON user_quests (guild_id, user_id, expires_at);

CREATE TABLE IF NOT EXISTS battlepass_seasons (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    max_level INTEGER NOT NULL DEFAULT 50,
    xp_per_level INTEGER NOT NULL DEFAULT 500,
    premium_price INTEGER NOT NULL DEFAULT 10000,
    active INTEGER NOT NULL DEFAULT 1,
    starts_at TEXT NOT NULL,
    ends_at TEXT NOT NULL,
    rewards_json TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_battlepass_seasons_guild ON battlepass_seasons (guild_id, active);

CREATE TABLE IF NOT EXISTS battlepass_progress (
    guild_id INTEGER NOT NULL,
    season_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    xp INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 1,
    is_premium INTEGER NOT NULL DEFAULT 0,
    claimed_free_levels TEXT NOT NULL DEFAULT '',
    claimed_premium_levels TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, season_id, user_id),
    FOREIGN KEY (season_id) REFERENCES battlepass_seasons (id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS private_voice_rooms (
    channel_id INTEGER PRIMARY KEY,
    guild_id INTEGER NOT NULL,
    owner_id INTEGER NOT NULL,
    room_name TEXT NOT NULL,
    user_limit INTEGER NOT NULL DEFAULT 0,
    is_locked INTEGER NOT NULL DEFAULT 0,
    is_hidden INTEGER NOT NULL DEFAULT 0,
    banned_user_ids TEXT NOT NULL DEFAULT '',
    empty_since TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_private_voice_guild_owner ON private_voice_rooms (guild_id, owner_id);

CREATE TABLE IF NOT EXISTS voice_hub_config (
    guild_id INTEGER PRIMARY KEY,
    hub_channel_id INTEGER NOT NULL DEFAULT 0,
    category_id INTEGER NOT NULL DEFAULT 0,
    control_channel_id INTEGER NOT NULL DEFAULT 0,
    name_template TEXT NOT NULL DEFAULT 'Комната {user}',
    default_limit INTEGER NOT NULL DEFAULT 0,
    empty_timeout_seconds INTEGER NOT NULL DEFAULT 30,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS message_logs (
    message_id INTEGER PRIMARY KEY,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    author_id INTEGER NOT NULL,
    author_name TEXT NOT NULL,
    content TEXT NOT NULL,
    attachments TEXT NULL,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    edited_content TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_message_logs_guild_created ON message_logs (guild_id, created_at);
CREATE INDEX IF NOT EXISTS idx_message_logs_author ON message_logs (guild_id, author_id);

CREATE TABLE IF NOT EXISTS nickname_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    old_nickname TEXT NULL,
    new_nickname TEXT NULL,
    changed_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_nickname_history_user ON nickname_history (guild_id, user_id, id DESC);

CREATE TABLE IF NOT EXISTS verification_applications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    answers_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    reviewer_id INTEGER NULL,
    review_reason TEXT NULL,
    created_at TEXT NOT NULL,
    reviewed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_verification_apps_guild ON verification_applications (guild_id, status);

CREATE TABLE IF NOT EXISTS reaction_roles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL,
    panel_title TEXT NOT NULL,
    mode TEXT NOT NULL DEFAULT 'toggle',
    component_type TEXT NOT NULL DEFAULT 'buttons',
    mappings_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reaction_roles_msg ON reaction_roles (guild_id, message_id);

CREATE TABLE IF NOT EXISTS giveaways (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    host_id INTEGER NOT NULL,
    prize TEXT NOT NULL,
    winner_count INTEGER NOT NULL DEFAULT 1 CHECK (winner_count >= 1),
    required_role_id INTEGER NULL,
    min_level INTEGER NOT NULL DEFAULT 1,
    min_balance INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'active',
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_giveaways_status_ends ON giveaways (status, ends_at);

CREATE TABLE IF NOT EXISTS giveaway_entries (
    giveaway_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    entered_at TEXT NOT NULL,
    PRIMARY KEY (giveaway_id, user_id),
    FOREIGN KEY (giveaway_id) REFERENCES giveaways (id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS giveaway_winners (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    giveaway_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    is_reroll INTEGER NOT NULL DEFAULT 0,
    selected_at TEXT NOT NULL,
    FOREIGN KEY (giveaway_id) REFERENCES giveaways (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_giveaway_winners_gid ON giveaway_winners (giveaway_id);

CREATE TABLE IF NOT EXISTS db_backups_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    file_name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    triggered_by_id INTEGER NOT NULL,
    sent_to_channel INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_db_backups_guild ON db_backups_log (guild_id, id DESC);

CREATE TABLE IF NOT EXISTS blackjack_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    bet INTEGER NOT NULL CHECK (bet > 0),
    insurance_bet INTEGER NOT NULL DEFAULT 0,
    deck_json TEXT NOT NULL,
    player_hands_json TEXT NOT NULL,
    dealer_hand_json TEXT NOT NULL,
    active_hand_index INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'playing',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_blackjack_sessions_user ON blackjack_sessions (guild_id, user_id, status);
