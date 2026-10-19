using Dapper;
using Microsoft.Data.Sqlite;
using MessengerBackend.Infrastructure;

namespace MessengerBackend.Features.Attachments;

public sealed class InitialSchema : ISchemaMigration
{
    public string Id => "004_attachments";
    public void Apply(SqliteConnection c)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS attachments(
                          id              INTEGER PRIMARY KEY AUTOINCREMENT,
                          message_id      INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                          file_name       TEXT NOT NULL,
                          path_to_file    TEXT NOT NULL,
                          file_type       VARCHAR(32) NOT NULL,
                          file_size_bytes INTEGER NOT NULL
                        );
            """);
        c.Execute("CREATE INDEX IF NOT EXISTS idx_attachments_msg ON attachments(message_id)");
    }
}
