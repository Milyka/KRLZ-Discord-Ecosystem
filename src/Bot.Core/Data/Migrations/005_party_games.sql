PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS party_game_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    voice_channel_id INTEGER NOT NULL DEFAULT 0,
    message_id INTEGER NOT NULL DEFAULT 0,
    host_id INTEGER NOT NULL,
    game_type TEXT NOT NULL,
    bet INTEGER NOT NULL DEFAULT 0 CHECK (bet >= 0),
    prize_pool INTEGER NOT NULL DEFAULT 0 CHECK (prize_pool >= 0),
    status TEXT NOT NULL DEFAULT 'lobby',
    phase TEXT NOT NULL DEFAULT 'lobby',
    round_num INTEGER NOT NULL DEFAULT 1,
    turn_user_id INTEGER NOT NULL DEFAULT 0,
    state_json TEXT NOT NULL DEFAULT '{}',
    muted_user_ids TEXT NOT NULL DEFAULT '',
    winner_summary TEXT NOT NULL DEFAULT '',
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_party_game_sessions_active ON party_game_sessions (guild_id, status, game_type);
CREATE INDEX IF NOT EXISTS idx_party_game_sessions_channel ON party_game_sessions (guild_id, channel_id, status);

CREATE TABLE IF NOT EXISTS party_game_players (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    seat_index INTEGER NOT NULL DEFAULT 0,
    role_code TEXT NOT NULL DEFAULT '',
    team_code TEXT NOT NULL DEFAULT '',
    is_alive INTEGER NOT NULL DEFAULT 1,
    bet_paid INTEGER NOT NULL DEFAULT 0,
    score INTEGER NOT NULL DEFAULT 0,
    player_data_json TEXT NOT NULL DEFAULT '{}',
    joined_at TEXT NOT NULL,
    UNIQUE (session_id, user_id),
    FOREIGN KEY (session_id) REFERENCES party_game_sessions (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_party_game_players_session ON party_game_players (session_id, seat_index ASC);
CREATE INDEX IF NOT EXISTS idx_party_game_players_user ON party_game_players (guild_id, user_id);

CREATE TABLE IF NOT EXISTS raid_boss_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    boss_code TEXT NOT NULL,
    boss_name TEXT NOT NULL,
    boss_level INTEGER NOT NULL DEFAULT 1,
    max_hp INTEGER NOT NULL CHECK (max_hp > 0),
    current_hp INTEGER NOT NULL CHECK (current_hp >= 0),
    reward_pool_coins INTEGER NOT NULL DEFAULT 10000,
    reward_crystals INTEGER NOT NULL DEFAULT 25,
    status TEXT NOT NULL DEFAULT 'active',
    last_log TEXT NOT NULL DEFAULT '',
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_raid_boss_sessions_active ON raid_boss_sessions (guild_id, status, ends_at);

CREATE TABLE IF NOT EXISTS raid_boss_hits (
    session_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    display_name TEXT NOT NULL,
    hero_class TEXT NOT NULL DEFAULT 'warrior',
    total_damage INTEGER NOT NULL DEFAULT 0,
    hits_count INTEGER NOT NULL DEFAULT 0,
    last_hit_at TEXT NOT NULL,
    PRIMARY KEY (session_id, user_id),
    FOREIGN KEY (session_id) REFERENCES raid_boss_sessions (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_raid_boss_hits_damage ON raid_boss_hits (session_id, total_damage DESC);

CREATE TABLE IF NOT EXISTS party_game_content (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    game_type TEXT NOT NULL,
    title TEXT NOT NULL,
    subtitle TEXT NOT NULL DEFAULT '',
    payload_json TEXT NOT NULL DEFAULT '{}',
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_party_game_content_type ON party_game_content (guild_id, game_type);
