-- Replace user, chat and message identities in one transaction. The temporary
-- maps are only needed while copying rows; sequence and non-entity IDs survive.
CREATE TEMP TABLE id_users(old_id INTEGER PRIMARY KEY, new_id TEXT NOT NULL UNIQUE);
CREATE TEMP TABLE id_chats(old_id INTEGER PRIMARY KEY, new_id TEXT NOT NULL UNIQUE);
CREATE TEMP TABLE id_messages(old_id INTEGER PRIMARY KEY, new_id TEXT NOT NULL UNIQUE);
INSERT INTO id_users SELECT id, lower(hex(randomblob(16))) FROM users;
INSERT INTO id_chats SELECT id, lower(hex(randomblob(24))) FROM chats;
INSERT INTO id_messages
SELECT m.id, c.new_id || '.' || lower(hex(randomblob(16)))
FROM messages m JOIN id_chats c ON c.old_id = m.chat_id;

DROP INDEX idx_sessions_user;
DROP INDEX idx_sessions_expires;
DROP INDEX ux_chats_direct_pair;
DROP INDEX idx_users_chats_chat;
DROP INDEX ux_users_chats_one_owner;
DROP TRIGGER trg_direct_insert_members;
DROP TRIGGER trg_direct_reject_extra_member;
DROP TRIGGER trg_direct_reject_member_delete;
DROP TRIGGER trg_direct_reject_roles;
DROP INDEX idx_messages_chat;
DROP INDEX idx_messages_sender;
DROP INDEX idx_messages_reply;
DROP INDEX ux_messages_client_ref;
DROP INDEX idx_message_hidden_message;
DROP INDEX idx_attachments_message;
DROP INDEX idx_message_reactions_user;
DROP TRIGGER trg_message_sender_is_member;
DROP TRIGGER trg_message_reply_same_chat;
DROP TRIGGER trg_message_deleted_cleanup;
DROP TRIGGER trg_message_forward_cleanup;
DROP INDEX idx_pinned_messages_chat;
DROP TRIGGER trg_pin_same_chat;
DROP INDEX shared_themes_owner;
DROP INDEX idx_antispam_attempts_time;
DROP INDEX idx_group_activity_chat;
DROP INDEX ux_messages_chat_sequence;
DROP TRIGGER trg_message_sequence;
DROP TRIGGER trg_message_sequence_immutable;
DROP VIEW message_read_at;
DROP VIEW user_chat_last_message;

ALTER TABLE users RENAME TO old_users;
ALTER TABLE sessions RENAME TO old_sessions;
ALTER TABLE avatars RENAME TO old_avatars;
ALTER TABLE chats RENAME TO old_chats;
ALTER TABLE users_chats RENAME TO old_users_chats;
ALTER TABLE messages RENAME TO old_messages;
ALTER TABLE message_hidden RENAME TO old_message_hidden;
ALTER TABLE attachments RENAME TO old_attachments;
ALTER TABLE message_reactions RENAME TO old_message_reactions;
ALTER TABLE message_forwards RENAME TO old_message_forwards;
ALTER TABLE pinned_messages RENAME TO old_pinned_messages;
ALTER TABLE shared_themes RENAME TO old_shared_themes;
ALTER TABLE antispam_blocks RENAME TO old_antispam_blocks;
ALTER TABLE antispam_attempts RENAME TO old_antispam_attempts;
ALTER TABLE group_activity RENAME TO old_group_activity;

CREATE TABLE users(
    id            TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 32 AND id NOT GLOB '*[^0-9a-f]*'),
    tag           TEXT NOT NULL UNIQUE COLLATE NOCASE,
    name          TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    bio           TEXT,
    created_at    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen_at  TEXT, allow_tag_search INTEGER NOT NULL DEFAULT 1
CHECK (allow_tag_search IN (0, 1)),
    CONSTRAINT tag_length  CHECK (length(tag) BETWEEN 3 AND 16),
    CONSTRAINT name_filled CHECK (length(name) > 0)
);

CREATE TABLE sessions(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT    NOT NULL UNIQUE,
    created_at TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TEXT    NOT NULL
);

CREATE TABLE avatars(
    user_id      TEXT NOT NULL PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    path         TEXT NOT NULL,
    content_type TEXT NOT NULL,
    updated_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT avatar_type CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp'))
);

CREATE TABLE chats(
    id         TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 48 AND id NOT GLOB '*[^0-9a-f]*'),
    type       TEXT NOT NULL,
    title      TEXT,
    user_a_id  TEXT REFERENCES users(id) ON DELETE CASCADE,
    user_b_id  TEXT REFERENCES users(id) ON DELETE CASCADE,
    created_by TEXT REFERENCES users(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, last_message_sequence INTEGER NOT NULL DEFAULT 0,
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

CREATE TABLE users_chats(
    user_id              TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    chat_id              TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    role                 TEXT    NOT NULL DEFAULT 'member',
    joined_at            TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    hidden_at            TEXT,
    last_read_message_id TEXT,
    last_read_at         TEXT, last_read_sequence INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, chat_id),
    CONSTRAINT member_role CHECK (role IN ('member', 'admin', 'owner'))
);

CREATE TABLE messages(
    id            TEXT NOT NULL PRIMARY KEY CHECK (length(id) = 81 AND substr(id, 1, 49) = chat_id || '.' AND substr(id, 50) NOT GLOB '*[^0-9a-f]*'),
    chat_id       TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    sender_id     TEXT NOT NULL REFERENCES users(id),
    text          TEXT,
    sent_at       TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    edited_at     TEXT,
    delivered_at  TEXT,
    deleted_at    TEXT,
    reply_to_id   TEXT REFERENCES messages(id) ON DELETE SET NULL,
    client_ref    TEXT,
    content_state TEXT    NOT NULL DEFAULT 'stored', client_ref_payload TEXT, sequence INTEGER CHECK (sequence > 0),
    CONSTRAINT state_values CHECK (content_state IN ('stored', 'deleted', 'not_stored')),
    CONSTRAINT text_matches_state CHECK (
        (content_state = 'stored' AND text IS NOT NULL AND length(text) BETWEEN 1 AND 22000)
        OR
        (content_state <> 'stored' AND text IS NULL)
    ),
    CONSTRAINT client_ref_length CHECK (length(client_ref) <= 128)
);

CREATE TABLE message_hidden(
    user_id    TEXT NOT NULL REFERENCES users(id)    ON DELETE CASCADE,
    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    hidden_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (user_id, message_id)
);

CREATE TABLE attachments(
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id   TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    file_name    TEXT    NOT NULL,
    content_type TEXT,
    size_bytes   INTEGER NOT NULL,
    storage_path TEXT    NOT NULL,
    created_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT attachment_size CHECK (size_bytes BETWEEN 0 AND 10485760)
);

CREATE TABLE message_reactions(
    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    user_id    TEXT NOT NULL REFERENCES users(id)    ON DELETE CASCADE,
    emoji      TEXT    NOT NULL,
    created_at TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (message_id, user_id, emoji),
    CONSTRAINT emoji_length CHECK (length(emoji) BETWEEN 1 AND 220)
);

CREATE TABLE message_forwards(
    message_id       TEXT NOT NULL PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
    author_id        TEXT REFERENCES users(id) ON DELETE SET NULL,
    author_name      TEXT    NOT NULL,
    original_sent_at TEXT    NOT NULL
);

CREATE TABLE pinned_messages(
    chat_id    TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    pinned_by  TEXT NOT NULL REFERENCES users(id),
    pinned_at  TEXT    NOT NULL,
    PRIMARY KEY (chat_id, message_id)
);

CREATE TABLE shared_themes (
 id TEXT PRIMARY KEY,
 owner_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 version INTEGER NOT NULL DEFAULT 1,
 document TEXT NOT NULL,
 updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE antispam_blocks (
    user_id TEXT NOT NULL PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    block_id TEXT NOT NULL,
    blocked_at INTEGER NOT NULL
);

CREATE TABLE antispam_attempts (
    user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    attempt_key TEXT NOT NULL,
    occurred_at INTEGER NOT NULL,
    PRIMARY KEY(user_id, attempt_key)
);

CREATE TABLE group_activity (
    id INTEGER PRIMARY KEY,
    chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    user_id TEXT REFERENCES users(id) ON DELETE SET NULL,
    kind TEXT NOT NULL CHECK (kind IN ('joined', 'left', 'removed')),
    occurred_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

INSERT INTO users(id, tag, name, password_hash, bio, created_at, last_seen_at, allow_tag_search)
SELECT map_id.new_id, src.tag, src.name, src.password_hash, src.bio, src.created_at, src.last_seen_at, src.allow_tag_search
FROM old_users src
JOIN id_users map_id ON map_id.old_id = src.id;

INSERT INTO sessions(id, user_id, token_hash, created_at, expires_at)
SELECT src.id, map_user_id.new_id, src.token_hash, src.created_at, src.expires_at
FROM old_sessions src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id;

INSERT INTO avatars(user_id, path, content_type, updated_at)
SELECT map_user_id.new_id, src.path, src.content_type, src.updated_at
FROM old_avatars src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id;

INSERT INTO chats(id, type, title, user_a_id, user_b_id, created_by, created_at, last_message_sequence)
SELECT map_id.new_id, src.type, src.title,
       min(map_user_a_id.new_id, map_user_b_id.new_id), max(map_user_a_id.new_id, map_user_b_id.new_id),
       map_created_by.new_id, src.created_at, src.last_message_sequence
FROM old_chats src
JOIN id_chats map_id ON map_id.old_id = src.id
LEFT JOIN id_users map_user_a_id ON map_user_a_id.old_id = src.user_a_id
LEFT JOIN id_users map_user_b_id ON map_user_b_id.old_id = src.user_b_id
LEFT JOIN id_users map_created_by ON map_created_by.old_id = src.created_by;

INSERT INTO users_chats(user_id, chat_id, role, joined_at, hidden_at, last_read_message_id, last_read_at, last_read_sequence)
SELECT map_user_id.new_id, map_chat_id.new_id, src.role, src.joined_at, src.hidden_at,
       map_last_read_message_id.new_id, src.last_read_at, src.last_read_sequence
FROM old_users_chats src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id
JOIN id_chats map_chat_id ON map_chat_id.old_id = src.chat_id
LEFT JOIN id_messages map_last_read_message_id ON map_last_read_message_id.old_id = src.last_read_message_id;

INSERT INTO messages(id, chat_id, sender_id, text, sent_at, edited_at, delivered_at, deleted_at,
                     reply_to_id, client_ref, content_state, client_ref_payload, sequence)
SELECT map_id.new_id, map_chat_id.new_id, map_sender_id.new_id, src.text, src.sent_at,
       src.edited_at, src.delivered_at, src.deleted_at, map_reply_to_id.new_id, src.client_ref,
       src.content_state,
       -- These inputs verify historical requests; they are never used to resolve IDs.
       CASE WHEN src.client_ref_payload IS NOT NULL
            THEN 'legacy:' || src.chat_id || '|' || coalesce(src.reply_to_id, 0) || '|' || src.client_ref_payload END,
       src.sequence
FROM old_messages src
JOIN id_messages map_id ON map_id.old_id = src.id
JOIN id_chats map_chat_id ON map_chat_id.old_id = src.chat_id
JOIN id_users map_sender_id ON map_sender_id.old_id = src.sender_id
LEFT JOIN id_messages map_reply_to_id ON map_reply_to_id.old_id = src.reply_to_id;

INSERT INTO message_hidden(user_id, message_id, hidden_at)
SELECT map_user_id.new_id, map_message_id.new_id, src.hidden_at
FROM old_message_hidden src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id
JOIN id_messages map_message_id ON map_message_id.old_id = src.message_id;

INSERT INTO attachments(id, message_id, file_name, content_type, size_bytes, storage_path, created_at)
SELECT src.id, map_message_id.new_id, src.file_name, src.content_type, src.size_bytes, src.storage_path, src.created_at
FROM old_attachments src
JOIN id_messages map_message_id ON map_message_id.old_id = src.message_id;

INSERT INTO message_reactions(message_id, user_id, emoji, created_at)
SELECT map_message_id.new_id, map_user_id.new_id, src.emoji, src.created_at
FROM old_message_reactions src
JOIN id_messages map_message_id ON map_message_id.old_id = src.message_id
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id;

INSERT INTO message_forwards(message_id, author_id, author_name, original_sent_at)
SELECT map_message_id.new_id, map_author_id.new_id, src.author_name, src.original_sent_at
FROM old_message_forwards src
JOIN id_messages map_message_id ON map_message_id.old_id = src.message_id
LEFT JOIN id_users map_author_id ON map_author_id.old_id = src.author_id;

INSERT INTO pinned_messages(chat_id, message_id, pinned_by, pinned_at)
SELECT map_chat_id.new_id, map_message_id.new_id, map_pinned_by.new_id, src.pinned_at
FROM old_pinned_messages src
JOIN id_chats map_chat_id ON map_chat_id.old_id = src.chat_id
JOIN id_messages map_message_id ON map_message_id.old_id = src.message_id
JOIN id_users map_pinned_by ON map_pinned_by.old_id = src.pinned_by;

INSERT INTO shared_themes(id, owner_id, version, document, updated_at)
SELECT src.id, map_owner_id.new_id, src.version, src.document, src.updated_at
FROM old_shared_themes src
JOIN id_users map_owner_id ON map_owner_id.old_id = src.owner_id;

INSERT INTO antispam_blocks(user_id, block_id, blocked_at)
SELECT map_user_id.new_id, src.block_id, src.blocked_at
FROM old_antispam_blocks src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id;

INSERT INTO antispam_attempts(user_id, attempt_key, occurred_at)
SELECT map_user_id.new_id,
       CASE WHEN map_chat.new_id IS NULL THEN src.attempt_key
            ELSE 'send:' || map_chat.new_id || substr(src.attempt_key, 6 + length(map_chat.old_id)) END,
       src.occurred_at
FROM old_antispam_attempts src
JOIN id_users map_user_id ON map_user_id.old_id = src.user_id
LEFT JOIN id_chats map_chat
  ON substr(src.attempt_key, 1, 5) = 'send:'
 AND map_chat.old_id = CAST(substr(src.attempt_key, 6, instr(substr(src.attempt_key, 6), ':') - 1) AS INTEGER);

INSERT INTO group_activity(id, chat_id, user_id, kind, occurred_at)
SELECT src.id, map_chat_id.new_id, map_user_id.new_id, src.kind, src.occurred_at
FROM old_group_activity src
JOIN id_chats map_chat_id ON map_chat_id.old_id = src.chat_id
LEFT JOIN id_users map_user_id ON map_user_id.old_id = src.user_id;

-- The numeric counters for unrelated entities must also survive deleted rows.
DELETE FROM sqlite_sequence WHERE name IN ('sessions', 'attachments');
INSERT INTO sqlite_sequence(name, seq)
SELECT substr(name, 5), seq FROM sqlite_sequence WHERE name IN ('old_sessions', 'old_attachments');

DROP TABLE old_group_activity;
DROP TABLE old_antispam_attempts;
DROP TABLE old_antispam_blocks;
DROP TABLE old_shared_themes;
DROP TABLE old_pinned_messages;
DROP TABLE old_message_forwards;
DROP TABLE old_message_reactions;
DROP TABLE old_attachments;
DROP TABLE old_message_hidden;
DROP TABLE old_messages;
DROP TABLE old_users_chats;
DROP TABLE old_chats;
DROP TABLE old_avatars;
DROP TABLE old_sessions;
DROP TABLE old_users;
DROP TABLE id_messages;
DROP TABLE id_chats;
DROP TABLE id_users;

CREATE INDEX idx_sessions_user    ON sessions(user_id);

CREATE INDEX idx_sessions_expires ON sessions(expires_at);

CREATE UNIQUE INDEX ux_chats_direct_pair
    ON chats(user_a_id, user_b_id)
    WHERE type = 'direct';

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

CREATE INDEX idx_messages_sender ON messages(sender_id, id);

CREATE INDEX idx_messages_reply  ON messages(reply_to_id);

CREATE UNIQUE INDEX ux_messages_client_ref
    ON messages(sender_id, client_ref)
    WHERE client_ref IS NOT NULL;

CREATE INDEX idx_message_hidden_message ON message_hidden(message_id);

CREATE INDEX idx_attachments_message ON attachments(message_id);

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

CREATE TRIGGER trg_message_forward_cleanup
AFTER UPDATE OF content_state ON messages
WHEN NEW.content_state = 'deleted' AND OLD.content_state <> 'deleted'
BEGIN
    DELETE FROM message_forwards WHERE message_id = NEW.id;
END;

CREATE INDEX idx_pinned_messages_chat ON pinned_messages(chat_id, pinned_at DESC);

CREATE TRIGGER trg_pin_same_chat
BEFORE INSERT ON pinned_messages
WHEN (SELECT chat_id FROM messages WHERE id = NEW.message_id) IS NOT NEW.chat_id
BEGIN
    SELECT RAISE(ABORT, 'pinned message is from another chat');
END;

CREATE INDEX shared_themes_owner ON shared_themes(owner_id);

CREATE INDEX idx_antispam_attempts_time ON antispam_attempts(user_id, occurred_at);

CREATE INDEX idx_group_activity_chat ON group_activity(chat_id, id DESC);

CREATE UNIQUE INDEX ux_messages_chat_sequence ON messages(chat_id, sequence);

CREATE TRIGGER trg_message_sequence
AFTER INSERT ON messages
BEGIN
    UPDATE chats SET last_message_sequence = last_message_sequence + 1 WHERE id = NEW.chat_id;
    UPDATE messages SET sequence = (SELECT last_message_sequence FROM chats WHERE id = NEW.chat_id)
    WHERE id = NEW.id;
END;

CREATE TRIGGER trg_message_sequence_immutable
BEFORE UPDATE OF id, sequence, chat_id ON messages
WHEN NEW.id <> OLD.id OR (OLD.sequence IS NOT NULL AND
     (NEW.sequence IS NOT OLD.sequence OR NEW.chat_id <> OLD.chat_id))
BEGIN
    SELECT RAISE(ABORT, 'message identity and order are immutable');
END;

CREATE VIEW message_read_at AS
SELECT m.id AS message_id,
       CASE WHEN COUNT(uc.user_id) > 0 AND MIN(uc.last_read_sequence) >= m.sequence
            THEN MAX(uc.last_read_at) END AS read_at
FROM messages m
LEFT JOIN users_chats uc ON uc.chat_id = m.chat_id AND uc.user_id <> m.sender_id
GROUP BY m.id;

CREATE VIEW user_chat_last_message AS
SELECT uc.user_id, uc.chat_id,
       (SELECT m.id FROM messages m
        WHERE m.chat_id = uc.chat_id
          AND NOT EXISTS (SELECT 1 FROM message_hidden h
                          WHERE h.message_id = m.id AND h.user_id = uc.user_id)
        ORDER BY m.sequence DESC LIMIT 1) AS message_id
FROM users_chats uc WHERE uc.hidden_at IS NULL;
