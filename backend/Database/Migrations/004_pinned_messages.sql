CREATE TABLE pinned_messages(
    chat_id    INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    pinned_by  INTEGER NOT NULL REFERENCES users(id),
    pinned_at  TEXT    NOT NULL,
    PRIMARY KEY (chat_id, message_id)
);
CREATE INDEX idx_pinned_messages_chat ON pinned_messages(chat_id, pinned_at DESC, message_id DESC);

CREATE TRIGGER trg_pin_same_chat
BEFORE INSERT ON pinned_messages
WHEN (SELECT chat_id FROM messages WHERE id = NEW.message_id) IS NOT NEW.chat_id
BEGIN
    SELECT RAISE(ABORT, 'pinned message is from another chat');
END;
