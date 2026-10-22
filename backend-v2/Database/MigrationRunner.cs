using Dapper;

namespace Messenger.Api.Database;

/// <summary>Выполняет Database/Migrations/*.sql по порядку имени, каждый файл один раз.</summary>
public static class MigrationRunner
{
    public static void Run(DbConnectionFactory db)
    {
        using var c = db.Open();
        c.Execute("CREATE TABLE IF NOT EXISTS schema_migrations(id TEXT PRIMARY KEY)");
        var dir = Path.Combine(AppContext.BaseDirectory, "Database", "Migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Order())
        {
            var id = Path.GetFileName(file);
            if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations WHERE id=@id", new { id }) > 0) continue;
            using var tx = c.BeginTransaction();
            c.Execute(File.ReadAllText(file), transaction: tx);
            c.Execute("INSERT INTO schema_migrations(id) VALUES(@id)", new { id }, tx);
            tx.Commit();
        }
    }
}
