using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalBackend.Db;

/// <summary>Создание/настройка локальной БД.</summary>
public static class MessengerDb
{
    public static DbContextOptions<MessengerDbContext> CreateOptions(string dbFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbFilePath))!);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        return new DbContextOptionsBuilder<MessengerDbContext>().UseSqlite(cs).Options;
    }

    /// <summary>Фабрика контекстов: у каждого вызова свой DbContext, безопасно для UI + фоновых потоков.</summary>
    public static IDbContextFactory<MessengerDbContext> CreateFactory(string dbFilePath)
        => new SimpleDbContextFactory(CreateOptions(dbFilePath));

    private sealed class SimpleDbContextFactory(DbContextOptions<MessengerDbContext> options)
        : IDbContextFactory<MessengerDbContext>
    {
        public MessengerDbContext CreateDbContext() => new(options);
    }
}