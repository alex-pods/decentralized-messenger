using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend.Infrastructure;

public sealed class StorageSchemaMigration : ISchemaMigration
{
    public string Id => "005_storage_file_migration";

    public void Apply(SqliteConnection c)
    {
        c.Execute("CREATE TABLE IF NOT EXISTS storage_metadata(id INTEGER PRIMARY KEY CHECK(id=1), key_probe TEXT NOT NULL)");
        SchemaMigrator.AddColumn(c, "storage_metadata", "legacy_files", "INTEGER NOT NULL DEFAULT 0");
        c.Execute("""
            CREATE TABLE storage_file_migrations(
                path TEXT PRIMARY KEY,
                source_hash TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1))
            );
            CREATE TABLE storage_maintenance(id TEXT PRIMARY KEY, completed_at TEXT NOT NULL);
            """);
    }
}
