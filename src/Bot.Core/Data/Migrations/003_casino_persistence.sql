PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS roulette_rounds (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',
    rolled_number INTEGER NULL,
    rolled_color TEXT NULL,
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_roulette_rounds_guild_status ON roulette_rounds (guild_id, status);

CREATE TABLE IF NOT EXISTS roulette_bets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    round_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    bet_type TEXT NOT NULL,
    bet_value TEXT NOT NULL,
    amount INTEGER NOT NULL CHECK (amount > 0),
    status TEXT NOT NULL DEFAULT 'held',
    created_at TEXT NOT NULL,
    FOREIGN KEY (round_id) REFERENCES roulette_rounds (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_roulette_bets_round ON roulette_bets (round_id, status);

CREATE TABLE IF NOT EXISTS jackpot_rounds (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',
    winner_id INTEGER NULL,
    total_pool INTEGER NOT NULL DEFAULT 0,
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_jackpot_rounds_guild_status ON jackpot_rounds (guild_id, status);

CREATE TABLE IF NOT EXISTS jackpot_bets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    round_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK (amount > 0),
    status TEXT NOT NULL DEFAULT 'held',
    created_at TEXT NOT NULL,
    FOREIGN KEY (round_id) REFERENCES jackpot_rounds (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_jackpot_bets_round ON jackpot_bets (round_id, status);

CREATE TABLE IF NOT EXISTS crash_bets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    amount INTEGER NOT NULL CHECK (amount > 0),
    crash_point REAL NOT NULL,
    cashed_out_multiplier REAL NULL,
    status TEXT NOT NULL DEFAULT 'active',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_crash_bets_guild_status ON crash_bets (guild_id, user_id, status);

CREATE TABLE IF NOT EXISTS polls_v2 (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message_id INTEGER NOT NULL DEFAULT 0,
    creator_id INTEGER NOT NULL,
    question TEXT NOT NULL,
    options_json TEXT NOT NULL,
    is_anonymous INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'active',
    ends_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_polls_v2_status ON polls_v2 (status, ends_at);

CREATE TABLE IF NOT EXISTS poll_v2_votes (
    poll_id INTEGER NOT NULL,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    option_index INTEGER NOT NULL,
    voted_at TEXT NOT NULL,
    PRIMARY KEY (poll_id, user_id),
    FOREIGN KEY (poll_id) REFERENCES polls_v2 (id) ON DELETE CASCADE
);
