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
