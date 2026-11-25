CREATE TABLE antispam_blocks (
    user_id INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    block_id TEXT NOT NULL,
    blocked_at INTEGER NOT NULL
);
CREATE TABLE antispam_attempts (
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    attempt_key TEXT NOT NULL,
    occurred_at INTEGER NOT NULL,
    PRIMARY KEY(user_id, attempt_key)
);
CREATE INDEX idx_antispam_attempts_time ON antispam_attempts(user_id, occurred_at);
