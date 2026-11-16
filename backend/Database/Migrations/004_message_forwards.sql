CREATE TABLE message_forwards(
    message_id       INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
    author_id        INTEGER REFERENCES users(id) ON DELETE SET NULL,
    author_name      TEXT    NOT NULL,
    original_sent_at TEXT    NOT NULL
);

CREATE TRIGGER trg_message_forward_cleanup
AFTER UPDATE OF content_state ON messages
WHEN NEW.content_state = 'deleted' AND OLD.content_state <> 'deleted'
BEGIN
    DELETE FROM message_forwards WHERE message_id = NEW.id;
END;
