CREATE TABLE shared_themes (
 id TEXT PRIMARY KEY,
 owner_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 version INTEGER NOT NULL DEFAULT 1,
 document TEXT NOT NULL,
 updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX shared_themes_owner ON shared_themes(owner_id);
