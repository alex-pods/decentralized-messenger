PRAGMA foreign_keys = ON;


CREATE TABLE users(
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    tag           TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name          TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    bio           TEXT,
    created_at    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at  TEXT,
    CONSTRAINT tag_length  CHECK (length(tag) BETWEEN 3 AND 16),
    CONSTRAINT name_filled CHECK (length(name) > 0)
);

CREATE TABLE sessions(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT    NOT NULL UNIQUE,
    created_at TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TEXT    NOT NULL
);
CREATE INDEX idx_sessions_user    ON sessions(user_id);
CREATE INDEX idx_sessions_expires ON sessions(expires_at);

CREATE TABLE avatars(
    user_id      INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    path         TEXT NOT NULL,
    content_type TEXT NOT NULL,
    updated_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT avatar_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp'))
);


CREATE TABLE chats(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    type       TEXT NOT NULL,
    title      TEXT,
    user_a_id  INTEGER REFERENCES users(id) ON DELETE CASCADE,
    user_b_id  INTEGER REFERENCES users(id) ON DELETE CASCADE,
    created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT chat_shape CHECK (
        (type = 'direct'
            AND user_a_id IS NOT NULL
            AND user_b_id IS NOT NULL
            AND title IS NULL
            AND user_a_id < user_b_id)
        OR
        (type = 'group'
            AND user_a_id IS NULL
            AND user_b_id IS NULL
            AND title IS NOT NULL)
    )
);
CREATE UNIQUE INDEX ux_chats_direct_pair
    ON chats(user_a_id, user_b_id)
    WHERE type = 'direct';

CREATE TABLE users_chats(
    user_id              INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    chat_id              INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    role                 TEXT    NOT NULL DEFAULT 'member',
    joined_at            TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    hidden_at            TEXT,
    last_read_message_id INTEGER NOT NULL DEFAULT 0,
    last_read_at         TEXT,
    PRIMARY KEY (user_id, chat_id),
    CONSTRAINT member_role CHECK (role IN ('member', 'admin', 'owner'))
);
CREATE INDEX idx_users_chats_chat ON users_chats(chat_id);
CREATE UNIQUE INDEX ux_users_chats_one_owner
    ON users_chats(chat_id)
    WHERE role = 'owner';

CREATE TRIGGER trg_direct_insert_members
AFTER INSERT ON chats
WHEN NEW.type = 'direct'
BEGIN
    INSERT INTO users_chats(user_id, chat_id) VALUES (NEW.user_a_id, NEW.id);
    INSERT INTO users_chats(user_id, chat_id) VALUES (NEW.user_b_id, NEW.id);
END;

CREATE TRIGGER trg_direct_reject_extra_member
BEFORE INSERT ON users_chats
WHEN (SELECT type FROM chats WHERE id = NEW.chat_id) = 'direct'
 AND NEW.user_id NOT IN (
        SELECT user_a_id FROM chats WHERE id = NEW.chat_id
        UNION ALL
        SELECT user_b_id FROM chats WHERE id = NEW.chat_id
     )
BEGIN
    SELECT RAISE(ABORT, 'direct chat allows only its two members');
END;

CREATE TRIGGER trg_direct_reject_member_delete
BEFORE DELETE ON users_chats
WHEN (SELECT type FROM chats WHERE id = OLD.chat_id) = 'direct'
BEGIN
    SELECT RAISE(ABORT, 'delete the direct chat itself, not its members');
END;

CREATE TRIGGER trg_direct_reject_roles
BEFORE UPDATE OF role ON users_chats
WHEN NEW.role <> 'member'
 AND (SELECT type FROM chats WHERE id = NEW.chat_id) = 'direct'
BEGIN
    SELECT RAISE(ABORT, 'direct chat has no roles');
END;


CREATE TABLE messages(
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    chat_id       INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    sender_id     INTEGER NOT NULL REFERENCES users(id),
    text          TEXT,
    sent_at       TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    edited_at     TEXT,
    delivered_at  TEXT,
    deleted_at    TEXT,
    reply_to_id   INTEGER REFERENCES messages(id) ON DELETE SET NULL,
    client_ref    TEXT,
    content_state TEXT    NOT NULL DEFAULT 'stored',
    CONSTRAINT state_values CHECK (content_state IN ('stored', 'deleted', 'not_stored')),
    CONSTRAINT text_matches_state CHECK (
        (content_state = 'stored' AND text IS NOT NULL AND length(text) BETWEEN 1 AND 22000)
        OR
        (content_state <> 'stored' AND text IS NULL)
    ),
    CONSTRAINT client_ref_length CHECK (length(client_ref) <= 128)
);
CREATE INDEX idx_messages_chat   ON messages(chat_id, id);
CREATE INDEX idx_messages_sender ON messages(sender_id, id);
CREATE INDEX idx_messages_reply  ON messages(reply_to_id);
CREATE UNIQUE INDEX ux_messages_client_ref
    ON messages(sender_id, client_ref)
    WHERE client_ref IS NOT NULL;

CREATE TABLE message_hidden(
    user_id    INTEGER NOT NULL REFERENCES users(id)    ON DELETE CASCADE,
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    hidden_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_id, message_id)
);
CREATE INDEX idx_message_hidden_message ON message_hidden(message_id);

CREATE TABLE attachments(
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    file_name    TEXT    NOT NULL,
    content_type TEXT,
    size_bytes   INTEGER NOT NULL,
    storage_path TEXT    NOT NULL,
    created_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT attachment_size CHECK (size_bytes BETWEEN 0 AND 10485760)
);
CREATE INDEX idx_attachments_message ON attachments(message_id);

CREATE TABLE message_reactions(
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    user_id    INTEGER NOT NULL REFERENCES users(id)    ON DELETE CASCADE,
    emoji      TEXT    NOT NULL,
    created_at TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (message_id, user_id, emoji),
    CONSTRAINT emoji_length CHECK (length(emoji) BETWEEN 1 AND 220)
);
CREATE INDEX idx_message_reactions_user ON message_reactions(user_id);

CREATE TRIGGER trg_message_sender_is_member
BEFORE INSERT ON messages
WHEN NOT EXISTS (
    SELECT 1 FROM users_chats
    WHERE chat_id = NEW.chat_id AND user_id = NEW.sender_id
)
BEGIN
    SELECT RAISE(ABORT, 'sender is not a chat member');
END;

CREATE TRIGGER trg_message_reply_same_chat
BEFORE INSERT ON messages
WHEN NEW.reply_to_id IS NOT NULL
 AND (SELECT chat_id FROM messages WHERE id = NEW.reply_to_id) IS NOT NEW.chat_id
BEGIN
    SELECT RAISE(ABORT, 'reply target must be in the same chat');
END;

CREATE TRIGGER trg_message_deleted_cleanup
AFTER UPDATE OF content_state ON messages
WHEN NEW.content_state = 'deleted' AND OLD.content_state <> 'deleted'
BEGIN
    DELETE FROM attachments       WHERE message_id = NEW.id;
    DELETE FROM message_reactions WHERE message_id = NEW.id;
END;


CREATE VIEW message_read_at AS
SELECT m.id AS message_id,
       CASE WHEN COUNT(uc.user_id) > 0
             AND MIN(uc.last_read_message_id) >= m.id
            THEN MAX(uc.last_read_at)
       END AS read_at
FROM messages m
LEFT JOIN users_chats uc
       ON uc.chat_id = m.chat_id
      AND uc.user_id <> m.sender_id
GROUP BY m.id;

CREATE VIEW user_chat_last_message AS
SELECT uc.user_id,
       uc.chat_id,
       (SELECT MAX(m.id)
          FROM messages m
         WHERE m.chat_id = uc.chat_id
           AND NOT EXISTS (
               SELECT 1 FROM message_hidden h
               WHERE h.message_id = m.id AND h.user_id = uc.user_id
           )) AS message_id
FROM users_chats uc
WHERE uc.hidden_at IS NULL;
