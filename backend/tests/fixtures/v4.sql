
            CREATE TABLE IF NOT EXISTS users(
              id                   INTEGER PRIMARY KEY AUTOINCREMENT,
              tag                  VARCHAR(16)  NOT NULL UNIQUE,
              password_hash        VARCHAR(128) NOT NULL,
              display_name         VARCHAR(32)  NOT NULL,
              path_to_avatar_file  TEXT,
              bio                  TEXT,
              join_date            TEXT NOT NULL,
              last_seen_at         TEXT
            );
            CREATE TABLE IF NOT EXISTS user_sessions(
              id          INTEGER PRIMARY KEY AUTOINCREMENT,
              user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              token_hash  VARCHAR(128) NOT NULL UNIQUE,
              created_at  TEXT NOT NULL,
              expires_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chats(
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              type       VARCHAR(16) NOT NULL CHECK(type IN ('direct','group')),
              title      VARCHAR(64),
              created_at TEXT NOT NULL,
              created_by INTEGER REFERENCES users(id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS chat_members(
              chat_id   INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              user_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              role      VARCHAR(16) NOT NULL DEFAULT 'member' CHECK(role IN ('member','admin','owner')),
              joined_at TEXT NOT NULL,
              PRIMARY KEY(chat_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS direct_chats(
              chat_id  INTEGER PRIMARY KEY REFERENCES chats(id) ON DELETE CASCADE,
              user1_id INTEGER NOT NULL REFERENCES users(id),
              user2_id INTEGER NOT NULL REFERENCES users(id),
              UNIQUE(user1_id, user2_id),
              CHECK(user1_id < user2_id)
            );
            CREATE TABLE IF NOT EXISTS messages(
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              chat_id      INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              sender_id    INTEGER NOT NULL REFERENCES users(id),
              reply_to_id INTEGER REFERENCES messages(id),
              text         TEXT NOT NULL,
              sent_at      TEXT NOT NULL,
              delivered_at TEXT,
              edited_at    TEXT,
              read_at      TEXT,
              deleted_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_messages_chat ON messages(chat_id, id);
            CREATE TABLE IF NOT EXISTS attachments(
              id              INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              file_name       TEXT NOT NULL,
              path_to_file    TEXT NOT NULL,
              file_type       VARCHAR(32) NOT NULL,
              file_size_bytes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS message_reactions(
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              emoji      VARCHAR(32) NOT NULL,
              UNIQUE(message_id, user_id, emoji)
            );
            CREATE TABLE IF NOT EXISTS message_hidden(
              message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
              user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              PRIMARY KEY(message_id, user_id)
            );
            CREATE TABLE IF NOT EXISTS chat_hidden(
              chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
              user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
              PRIMARY KEY(chat_id, user_id)
            );

ALTER TABLE chat_members ADD COLUMN last_read_message_id INTEGER;
ALTER TABLE chat_members ADD COLUMN read_at TEXT;
ALTER TABLE messages ADD COLUMN client_ref TEXT;
ALTER TABLE messages ADD COLUMN client_ref_payload TEXT;
