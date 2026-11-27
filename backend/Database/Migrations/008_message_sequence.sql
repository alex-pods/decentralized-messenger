-- IDs remain identities. Sequence is the server-assigned order within a chat.
ALTER TABLE chats ADD COLUMN last_message_sequence INTEGER NOT NULL DEFAULT 0;
ALTER TABLE messages ADD COLUMN sequence INTEGER CHECK (sequence > 0);
ALTER TABLE users_chats ADD COLUMN last_read_sequence INTEGER NOT NULL DEFAULT 0;

WITH ordered AS (
    SELECT id, ROW_NUMBER() OVER (PARTITION BY chat_id ORDER BY id) AS position
    FROM messages
)
UPDATE messages SET sequence = (SELECT position FROM ordered WHERE ordered.id = messages.id);

UPDATE chats SET last_message_sequence = COALESCE(
    (SELECT MAX(sequence) FROM messages WHERE chat_id = chats.id), 0);

-- A legacy read cursor can point at a removed row; preserve its boundary too.
UPDATE users_chats SET last_read_sequence = COALESCE(
    (SELECT MAX(sequence) FROM messages
     WHERE chat_id = users_chats.chat_id AND id <= users_chats.last_read_message_id), 0);

CREATE UNIQUE INDEX ux_messages_chat_sequence ON messages(chat_id, sequence);

-- Runs in the INSERT transaction, including inserts made by forwarding.
-- Keep the counter on the chat so deleting messages never reuses a sequence.
CREATE TRIGGER trg_message_sequence
AFTER INSERT ON messages
BEGIN
    UPDATE chats SET last_message_sequence = last_message_sequence + 1 WHERE id = NEW.chat_id;
    UPDATE messages SET sequence = (SELECT last_message_sequence FROM chats WHERE id = NEW.chat_id)
    WHERE id = NEW.id;
END;

CREATE TRIGGER trg_message_sequence_immutable
BEFORE UPDATE OF sequence, chat_id ON messages
WHEN OLD.sequence IS NOT NULL AND (NEW.sequence IS NOT OLD.sequence OR NEW.chat_id <> OLD.chat_id)
BEGIN
    SELECT RAISE(ABORT, 'message order is immutable');
END;

DROP VIEW message_read_at;
CREATE VIEW message_read_at AS
SELECT m.id AS message_id,
       CASE WHEN COUNT(uc.user_id) > 0 AND MIN(uc.last_read_sequence) >= m.sequence
            THEN MAX(uc.last_read_at) END AS read_at
FROM messages m
LEFT JOIN users_chats uc ON uc.chat_id = m.chat_id AND uc.user_id <> m.sender_id
GROUP BY m.id;

DROP VIEW user_chat_last_message;
CREATE VIEW user_chat_last_message AS
SELECT uc.user_id, uc.chat_id,
       (SELECT m.id FROM messages m
        WHERE m.chat_id = uc.chat_id
          AND NOT EXISTS (SELECT 1 FROM message_hidden h
                          WHERE h.message_id = m.id AND h.user_id = uc.user_id)
        ORDER BY m.sequence DESC LIMIT 1) AS message_id
FROM users_chats uc WHERE uc.hidden_at IS NULL;
