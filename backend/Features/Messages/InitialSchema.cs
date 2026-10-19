using Dapper;
using Microsoft.Data.Sqlite;
using MessengerBackend.Infrastructure;

namespace MessengerBackend.Features.Messages;

public sealed class InitialSchema : ISchemaMigration
{
    public string Id => "003_messages";
    public void Apply(SqliteConnection c)
    {
        c.Execute("""
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
            """);
        SchemaMigrator.AddColumn(c, "messages", "client_ref", "TEXT");
        SchemaMigrator.AddColumn(c, "messages", "client_ref_payload", "TEXT");
        SchemaMigrator.AddColumn(c, "messages", "storage_version", "INTEGER NOT NULL DEFAULT 0");
        c.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_messages_sender_ref ON messages(sender_id, client_ref) WHERE client_ref IS NOT NULL");
        c.Execute("CREATE INDEX IF NOT EXISTS idx_reactions_msg ON message_reactions(message_id)");
        c.Execute("CREATE INDEX IF NOT EXISTS idx_message_hidden_user ON message_hidden(user_id,message_id)");
    }
}
