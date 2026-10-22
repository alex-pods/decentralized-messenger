using System.Data;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Database;

/// <summary>«DatabaseConnection» с доски: единственное место, которое знает строку подключения.</summary>
public sealed class DbConnectionFactory(string path)
{
    public IDbConnection Open()
    {
        var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        return c;
    }
}
