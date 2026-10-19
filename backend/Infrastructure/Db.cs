using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MessengerBackend;

/// <summary>
/// SQLite connections and write serialization; feature modules own their schema and SQL.
/// </summary>
public static class Db
{
    public static string Path { get; } =
        Environment.GetEnvironmentVariable("MESSENGER_DB") ?? "messenger.db";

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static void Init()
    {
        Directory.CreateDirectory(PathUtilities.DirOf(Path));
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        Infrastructure.SchemaMigrator.Apply(c);
    }

    /// <summary>SQLite — один писатель; записи сериализуются, читатели живут параллельно (WAL).</summary>
    public static async Task<T> WriteAsync<T>(Func<IDbConnection, Task<T>> action)
    {
        await WriteLock.WaitAsync();
        try
        {
            await using var c = Open();
            return await action(c);
        }
        finally { WriteLock.Release(); }
    }

    public static T Write<T>(Func<IDbConnection, T> action)
    {
        WriteLock.Wait();
        try
        {
            using var c = Open();
            return action(c);
        }
        finally { WriteLock.Release(); }
    }

    public static T Read<T>(Func<IDbConnection, T> action)
    {
        using var c = Open();
        return action(c);
    }
}

internal static class PathUtilities
{
    public static string DirOf(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }
}
