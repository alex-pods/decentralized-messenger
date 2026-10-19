using Dapper;
using Microsoft.Data.Sqlite;
using MessengerBackend.Infrastructure;

namespace MessengerBackend.Features.Accounts;

public sealed class InitialSchema : ISchemaMigration
{
    public string Id => "001_accounts";
    public void Apply(SqliteConnection c)
    {
        c.Execute("""
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
            """);
        SchemaMigrator.AddColumn(c, "users", "storage_version", "INTEGER NOT NULL DEFAULT 0");
        c.Execute("CREATE INDEX IF NOT EXISTS idx_sessions_expiry ON user_sessions(expires_at)");
        c.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_users_tag_nocase ON users(tag COLLATE NOCASE)");
    }
}
