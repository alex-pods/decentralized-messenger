-- Reserve relayed IDs and order without writing a message or any content-derived data.
CREATE TABLE relay_receipts (
    id TEXT PRIMARY KEY,
    chat_id TEXT NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
    sender_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    sent_at TEXT NOT NULL,
    queued_connections INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_relay_receipts_chat ON relay_receipts(chat_id);
CREATE INDEX idx_relay_receipts_sender ON relay_receipts(sender_id);

CREATE TRIGGER trg_messages_reject_relay_id
BEFORE INSERT ON messages
WHEN EXISTS (SELECT 1 FROM relay_receipts WHERE id = NEW.id)
BEGIN
    SELECT RAISE(ABORT, 'message ID reserved for relay');
END;
