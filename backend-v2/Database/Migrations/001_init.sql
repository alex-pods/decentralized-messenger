CREATE TABLE users(
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    tag  TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
);
CREATE TABLE avatars(
    user_id INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    url     TEXT NOT NULL
);
CREATE TABLE chats(
    id    INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT
);
CREATE TABLE messages(
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id   INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL REFERENCES users(id),
    text      TEXT NOT NULL,
    sent_at   TEXT NOT NULL
);
CREATE INDEX idx_messages_chat   ON messages(chat_id, id);
CREATE INDEX idx_messages_sender ON messages(sender_id, id);
