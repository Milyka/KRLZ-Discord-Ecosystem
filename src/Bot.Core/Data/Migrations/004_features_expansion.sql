PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS user_reputation (
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    reputation INTEGER NOT NULL DEFAULT 0,
    positive_count INTEGER NOT NULL DEFAULT 0,
    negative_count INTEGER NOT NULL DEFAULT 0,
    accent_color_hex TEXT NULL,
    last_given_at TEXT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_user_reputation_score ON user_reputation (guild_id, reputation DESC);

CREATE TABLE IF NOT EXISTS reputation_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    from_user_id INTEGER NOT NULL,
    to_user_id INTEGER NOT NULL,
    delta INTEGER NOT NULL,
    comment TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reputation_logs_to ON reputation_logs (guild_id, to_user_id, id DESC);
CREATE INDEX IF NOT EXISTS idx_reputation_logs_pair ON reputation_logs (guild_id, from_user_id, to_user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS private_voice_presets (
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    room_name TEXT NOT NULL,
    user_limit INTEGER NOT NULL DEFAULT 0,
    is_locked INTEGER NOT NULL DEFAULT 0,
    is_hidden INTEGER NOT NULL DEFAULT 0,
    trusted_user_ids TEXT NOT NULL DEFAULT '',
    banned_user_ids TEXT NOT NULL DEFAULT '',
    updated_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);

CREATE TABLE IF NOT EXISTS family_profiles (
    marriage_id INTEGER PRIMARY KEY,
    guild_id INTEGER NOT NULL,
    family_name TEXT NOT NULL DEFAULT 'Семья KRLZ',
    family_balance INTEGER NOT NULL DEFAULT 0 CHECK (family_balance >= 0),
    love_xp INTEGER NOT NULL DEFAULT 0 CHECK (love_xp >= 0),
    love_level INTEGER NOT NULL DEFAULT 1 CHECK (love_level >= 1),
    updated_at TEXT NOT NULL,
    FOREIGN KEY (marriage_id) REFERENCES marriages (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_family_profiles_guild ON family_profiles (guild_id, love_xp DESC);

CREATE TABLE IF NOT EXISTS family_members (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    marriage_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    child_user_id INTEGER NOT NULL,
    role_label TEXT NOT NULL DEFAULT 'Ребёнок',
    adopted_at TEXT NOT NULL,
    UNIQUE (guild_id, child_user_id),
    FOREIGN KEY (marriage_id) REFERENCES marriages (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_family_members_marriage ON family_members (marriage_id);

CREATE TABLE IF NOT EXISTS bank_accounts (
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    deposit_balance INTEGER NOT NULL DEFAULT 0 CHECK (deposit_balance >= 0),
    crystals INTEGER NOT NULL DEFAULT 0 CHECK (crystals >= 0),
    total_interest_earned INTEGER NOT NULL DEFAULT 0,
    last_interest_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_bank_accounts_deposit ON bank_accounts (guild_id, deposit_balance DESC);

CREATE TABLE IF NOT EXISTS user_pets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    pet_code TEXT NOT NULL,
    name TEXT NOT NULL,
    rarity TEXT NOT NULL DEFAULT 'rare',
    level INTEGER NOT NULL DEFAULT 1,
    xp INTEGER NOT NULL DEFAULT 0,
    satiety INTEGER NOT NULL DEFAULT 100,
    is_active INTEGER NOT NULL DEFAULT 1,
    expedition_ends_at TEXT NULL,
    last_fed_at TEXT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (guild_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_user_pets_guild ON user_pets (guild_id, level DESC, xp DESC);

CREATE TABLE IF NOT EXISTS duel_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    challenger_id INTEGER NOT NULL,
    opponent_id INTEGER NOT NULL,
    bet INTEGER NOT NULL CHECK (bet >= 0),
    challenger_hp INTEGER NOT NULL DEFAULT 100,
    opponent_hp INTEGER NOT NULL DEFAULT 100,
    turn_user_id INTEGER NOT NULL,
    round_num INTEGER NOT NULL DEFAULT 1,
    last_log TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'pending',
    winner_id INTEGER NULL,
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_duel_sessions_status ON duel_sessions (guild_id, status, expires_at);

CREATE TABLE IF NOT EXISTS mines_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    bet INTEGER NOT NULL CHECK (bet > 0),
    bomb_count INTEGER NOT NULL CHECK (bomb_count >= 1 AND bomb_count <= 19),
    bombs_json TEXT NOT NULL,
    revealed_json TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'playing',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_mines_sessions_user ON mines_sessions (guild_id, user_id, status);
