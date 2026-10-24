CREATE TABLE users(
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    tag           TEXT NOT NULL COLLATE NOCASE UNIQUE,
    name          TEXT NOT NULL,
    password_hash TEXT NOT NULL
);

CREATE TABLE avatars(
    user_id INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    url     TEXT NOT NULL
);

CREATE TABLE chats(
    id    INTEGER PRIMARY KEY AUTOINCREMENT,
    type  TEXT NOT NULL CHECK (type IN ('direct','group')), 
    title TEXT
);

CREATE TABLE chat_members(
    chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id),
    PRIMARY KEY (chat_id, user_id)
);
CREATE INDEX idx_chat_members_user ON chat_members(user_id);

CREATE TABLE messages(
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id   INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL REFERENCES users(id),
    text      TEXT NOT NULL,
    sent_at   TEXT NOT NULL
);
CREATE INDEX idx_messages_chat   ON messages(chat_id, id);
CREATE INDEX idx_messages_sender ON messages(sender_id, id);

CREATE TABLE user_session(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

