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

            connection.Execute(sql, transaction: transaction);

            connection.Execute(
                "INSERT INTO schema_migrations(id) VALUES(@id)",
                new { id },
                transaction: transaction
            );

            transaction.Commit();
            Console.WriteLine($"Applied: {id}");
        }
    }
}
