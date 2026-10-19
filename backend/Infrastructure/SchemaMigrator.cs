using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend.Infrastructure;

public interface ISchemaMigration
{
    string Id { get; }
    void Apply(SqliteConnection connection);
}

public static class SchemaMigrator
{
    public static void Apply(SqliteConnection c)
    {
        c.Execute("CREATE TABLE IF NOT EXISTS schema_migrations(id TEXT PRIMARY KEY, applied_at TEXT NOT NULL)");
        var migrations = typeof(ISchemaMigration).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ISchemaMigration).IsAssignableFrom(t))
            .Select(t => (ISchemaMigration)Activator.CreateInstance(t)!).OrderBy(m => m.Id).ToList();
        if (migrations.Select(m => m.Id).Distinct().Count() != migrations.Count)
            throw new InvalidOperationException("Duplicate schema migration id");
        foreach (var migration in migrations)
        {
            // BEGIN IMMEDIATE serializes migrations even across processes.
            c.Execute("BEGIN IMMEDIATE");
            try
            {
                if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations WHERE id=@id", new { id = migration.Id }) == 0)
                {
                    migration.Apply(c);
                    c.Execute("INSERT INTO schema_migrations VALUES(@id,@at)", new { id = migration.Id, at = Util.NowIso() });
                }
                c.Execute("COMMIT");
            }
            catch { c.Execute("ROLLBACK"); throw; }
        }
    }

    public static void AddColumn(SqliteConnection c, string table, string column, string declaration)
    {
        // Identifiers are developer-owned constants, never request input.
        if (!c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Contains(column, StringComparer.OrdinalIgnoreCase))
            c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {declaration}");
    }

    public static T Transaction<T>(System.Data.IDbConnection c, Func<T> action)
    {
        c.Execute("BEGIN IMMEDIATE");
        try { var result = action(); c.Execute("COMMIT"); return result; }
        catch { c.Execute("ROLLBACK"); throw; }
    }

    public static async Task<T> TransactionAsync<T>(System.Data.IDbConnection c, Func<Task<T>> action)
    {
        c.Execute("BEGIN IMMEDIATE");
        try { var result = await action(); c.Execute("COMMIT"); return result; }
        catch { c.Execute("ROLLBACK"); throw; }
    }
}
