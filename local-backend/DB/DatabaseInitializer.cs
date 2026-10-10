using Microsoft.EntityFrameworkCore;

namespace LocalBackend.Db;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IDbContextFactory<MessengerDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        // Only additive changes; old profiles and messages remain in place.
        foreach (var (table, column, declaration) in new[]
        {
            ("Chats", "IsServerVisible", "INTEGER NOT NULL DEFAULT 1"),
            ("Messages", "ContentState", "TEXT NOT NULL DEFAULT 'stored'"),
            ("Messages", "IsHidden", "INTEGER NOT NULL DEFAULT 0")
        })
        {
            var columns = await db.Database.SqlQuery<string>(
                $"SELECT name AS Value FROM pragma_table_info({table})").ToListAsync(ct);
            if (!columns.Contains(column))
            {
                // Identifiers come only from the fixed list above, never from input.
                var upgradeSql = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
                await db.Database.ExecuteSqlRawAsync(upgradeSql, ct);
            }
        }

        string[] sql =
        [
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS MessagesFts
            USING fts5(Text, content='Messages', content_rowid='Id');
            """,
            """
            CREATE TRIGGER IF NOT EXISTS Messages_fts_ai AFTER INSERT ON Messages BEGIN
                INSERT INTO MessagesFts(rowid, Text) VALUES (new.Id, new.Text);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS Messages_fts_ad AFTER DELETE ON Messages BEGIN
                INSERT INTO MessagesFts(MessagesFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS Messages_fts_au AFTER UPDATE OF Text ON Messages BEGIN
                INSERT INTO MessagesFts(MessagesFts, rowid, Text) VALUES ('delete', old.Id, old.Text);
                INSERT INTO MessagesFts(rowid, Text) VALUES (new.Id, new.Text);
            END;
            """,
        ];

        foreach (var command in sql)
            await db.Database.ExecuteSqlRawAsync(command, ct);
    }
}
