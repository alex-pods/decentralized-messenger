using Dapper;

namespace Messenger.Api.Database;

public static class MigrationRunner
{
    public static void Run(DbConnectionFactory db)
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "Migrations"
        );

        var files = Directory.GetFiles(directory, "*.sql")
            .OrderBy(path => path, StringComparer.Ordinal);

        using var connection = db.Open();
        connection.Execute("PRAGMA journal_mode = WAL;");

        connection.Execute(
            "CREATE TABLE IF NOT EXISTS schema_migrations(" +
            "id TEXT NOT NULL PRIMARY KEY)"
        );

        foreach(var file in files){
            var id = Path.GetFileName(file);

            using var transaction = connection.BeginTransaction();

            var applied = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM schema_migrations WHERE id = @id",
                new { id },
                transaction: transaction
            );  

            if(applied > 0){
                transaction.Commit();
                Console.WriteLine($"Already applied: {id}");
                continue;
            }

            var sql = File.ReadAllText(file);

            var replacesIdentities = id == "009_random_entity_ids.sql";
            if (replacesIdentities) RequireForeignKeys(connection, transaction);
            connection.Execute(sql, transaction: transaction);
            if (replacesIdentities) RequireForeignKeys(connection, transaction);

            connection.Execute(
                "INSERT INTO schema_migrations(id) VALUES(@id)",
                new { id },
                transaction: transaction
            );

            transaction.Commit();
            Console.WriteLine($"Applied: {id}");
        }
    }

    private static void RequireForeignKeys(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
    {
        if (connection.Query("PRAGMA foreign_key_check;", transaction: transaction).Any())
            throw new InvalidDataException("The identity migration requires valid database references");
    }
}
