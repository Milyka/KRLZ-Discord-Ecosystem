PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS guild_settings (
    guild_id INTEGER PRIMARY KEY,
    embed_color_hex TEXT NOT NULL DEFAULT '#5865F2',
    currency_name TEXT NOT NULL DEFAULT 'KRLZ Coin',
    currency_symbol TEXT NOT NULL DEFAULT 'KR',
    custom_role_price INTEGER NOT NULL DEFAULT 5000,
    custom_role_anchor_id INTEGER NOT NULL DEFAULT 0,
    mod_log_channel_id INTEGER NOT NULL DEFAULT 0,
    ticket_log_channel_id INTEGER NOT NULL DEFAULT 0,
    ticket_panel_channel_id INTEGER NOT NULL DEFAULT 0,
    ticket_category_id INTEGER NOT NULL DEFAULT 0,
    welcome_channel_id INTEGER NOT NULL DEFAULT 0,
    security_log_channel_id INTEGER NOT NULL DEFAULT 0,
    admin_role_id INTEGER NOT NULL DEFAULT 0,
    moderator_role_id INTEGER NOT NULL DEFAULT 0,
    muted_role_id INTEGER NOT NULL DEFAULT 0,
    verified_role_id INTEGER NOT NULL DEFAULT 0,
    quarantine_role_id INTEGER NOT NULL DEFAULT 0,
    warn_threshold_mute INTEGER NOT NULL DEFAULT 3,
    warn_mute_minutes INTEGER NOT NULL DEFAULT 120,
    warn_threshold_ban INTEGER NOT NULL DEFAULT 5,
    max_open_tickets INTEGER NOT NULL DEFAULT 2,
    antispam_enabled INTEGER NOT NULL DEFAULT 1,
    antispam_max_messages INTEGER NOT NULL DEFAULT 6,
    antispam_window_seconds INTEGER NOT NULL DEFAULT 5,
    antispam_block_invites INTEGER NOT NULL DEFAULT 1,
    antispam_block_links INTEGER NOT NULL DEFAULT 1,
    antinuke_enabled INTEGER NOT NULL DEFAULT 1,
    antiraid_enabled INTEGER NOT NULL DEFAULT 1,
    captcha_enabled INTEGER NOT NULL DEFAULT 1,
    lockdown_active INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    balance INTEGER NOT NULL DEFAULT 0 CHECK (balance >= 0),
    xp INTEGER NOT NULL DEFAULT 0 CHECK (xp >= 0),
    level INTEGER NOT NULL DEFAULT 1 CHECK (level >= 1),
    messages INTEGER NOT NULL DEFAULT 0,
    voice_seconds INTEGER NOT NULL DEFAULT 0,
    bio TEXT NOT NULL DEFAULT 'Участник сервера KRLZ',
    title TEXT NOT NULL DEFAULT 'Новичок KRLZ',
    birthday TEXT NULL,
    selected_background TEXT NOT NULL DEFAULT 'default.png',
    last_daily_at TEXT NULL,
    last_work_at TEXT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_users_guild_balance ON users (guild_id, balance DESC);
CREATE INDEX IF NOT EXISTS idx_users_guild_xp ON users (guild_id, xp DESC);
CREATE INDEX IF NOT EXISTS idx_users_guild_messages ON users (guild_id, messages DESC);
CREATE INDEX IF NOT EXISTS idx_users_guild_voice ON users (guild_id, voice_seconds DESC);

CREATE TABLE IF NOT EXISTS warns (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    moderator_id INTEGER NOT NULL,
    reason TEXT NOT NULL,
    active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_warns_guild_user ON warns (guild_id, user_id, active);

CREATE TABLE IF NOT EXISTS punishments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    moderator_id INTEGER NOT NULL,
    type TEXT NOT NULL,
    reason TEXT NOT NULL,
    expires_at TEXT NULL,
    active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_punishments_guild_user ON punishments (guild_id, user_id);
CREATE INDEX IF NOT EXISTS idx_punishments_active_expires ON punishments (active, expires_at);

CREATE TABLE IF NOT EXISTS tickets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    ticket_number INTEGER NOT NULL,
    channel_id INTEGER NOT NULL UNIQUE,
    owner_id INTEGER NOT NULL,
    claimed_by_id INTEGER NULL,
    category TEXT NOT NULL,
    subject TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',
    close_reason TEXT NULL,
    rating INTEGER NULL,
    created_at TEXT NOT NULL,
    closed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS idx_tickets_guild_owner ON tickets (guild_id, owner_id, status);

CREATE TABLE IF NOT EXISTS ticket_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ticket_id INTEGER NOT NULL,
    author_id INTEGER NOT NULL,
    author_name TEXT NOT NULL,
    content TEXT NOT NULL,
    attachments TEXT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY (ticket_id) REFERENCES tickets (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_ticket_messages_ticket ON ticket_messages (ticket_id);

CREATE TABLE IF NOT EXISTS custom_roles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    owner_id INTEGER NOT NULL,
    role_id INTEGER NOT NULL UNIQUE,
    name TEXT NOT NULL,
    color_hex TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_custom_roles_guild_owner ON custom_roles (guild_id, owner_id);

CREATE TABLE IF NOT EXISTS shop_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    item_type TEXT NOT NULL,
    price INTEGER NOT NULL CHECK (price >= 0),
    role_id INTEGER NULL,
    payload TEXT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_shop_items_guild ON shop_items (guild_id, enabled);

CREATE TABLE IF NOT EXISTS purchases (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    price_paid INTEGER NOT NULL,
    purchased_at TEXT NOT NULL,
    FOREIGN KEY (item_id) REFERENCES shop_items (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_purchases_guild_user ON purchases (guild_id, user_id);

CREATE TABLE IF NOT EXISTS transactions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    from_user_id INTEGER NULL,
    to_user_id INTEGER NULL,
    amount INTEGER NOT NULL,
    tx_type TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_transactions_guild_from ON transactions (guild_id, from_user_id);
CREATE INDEX IF NOT EXISTS idx_transactions_guild_to ON transactions (guild_id, to_user_id);
CREATE INDEX IF NOT EXISTS idx_transactions_created ON transactions (guild_id, created_at);

CREATE TABLE IF NOT EXISTS voice_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT NULL,
    duration_seconds INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_voice_sessions_guild_user ON voice_sessions (guild_id, user_id, started_at);

CREATE TABLE IF NOT EXISTS voice_bans (
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    moderator_id INTEGER NOT NULL,
    reason TEXT NOT NULL,
    expires_at TEXT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, user_id)
);

CREATE TABLE IF NOT EXISTS marriages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user1_id INTEGER NOT NULL,
    user2_id INTEGER NOT NULL,
    married_at TEXT NOT NULL,
    UNIQUE (guild_id, user1_id),
    UNIQUE (guild_id, user2_id)
);

CREATE INDEX IF NOT EXISTS idx_marriages_guild ON marriages (guild_id, user1_id, user2_id);

CREATE TABLE IF NOT EXISTS reminders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    user_id INTEGER NOT NULL,
    channel_id INTEGER NOT NULL,
    message TEXT NOT NULL,
    trigger_at TEXT NOT NULL,
    completed INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_reminders_trigger ON reminders (completed, trigger_at);

CREATE TABLE IF NOT EXISTS security_whitelist (
    guild_id INTEGER NOT NULL,
    target_id INTEGER NOT NULL,
    target_type TEXT NOT NULL,
    added_by_id INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (guild_id, target_id)
);

CREATE TABLE IF NOT EXISTS security_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    offender_id INTEGER NOT NULL,
    event_type TEXT NOT NULL,
    details TEXT NOT NULL,
    action_taken TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_security_events_guild ON security_events (guild_id, created_at DESC);

CREATE TABLE IF NOT EXISTS guild_backups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    guild_id INTEGER NOT NULL,
    snapshot_name TEXT NOT NULL,
    created_by_id INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_guild_backups_guild ON guild_backups (guild_id, created_at DESC);
