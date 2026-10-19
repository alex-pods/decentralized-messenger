using Dapper;
using Microsoft.Data.Sqlite;
using MessengerBackend.Infrastructure;

namespace MessengerBackend.Features.Chats;

public sealed class InitialSchema : ISchemaMigration
{
    public string Id => "002_chats";
    public void Apply(SqliteConnection c)
    {
        c.Execute("""
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
            CREATE TABLE IF NOT EXISTS chat_hidden(
                          chat_id INTEGER NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
                          user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                          PRIMARY KEY(chat_id, user_id)
                        );
            """);
        SchemaMigrator.AddColumn(c, "chat_members", "last_read_message_id", "INTEGER");
        SchemaMigrator.AddColumn(c, "chat_members", "read_at", "TEXT");
        c.Execute("CREATE INDEX IF NOT EXISTS idx_chat_members_user ON chat_members(user_id)");
        c.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_chat_owner ON chat_members(chat_id) WHERE role='owner'");
        c.Execute("CREATE INDEX IF NOT EXISTS idx_chat_hidden_user ON chat_hidden(user_id,chat_id)");
    }
}
