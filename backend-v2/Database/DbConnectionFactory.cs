using System.Data;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Database;

/// <summary>«DatabaseConnection» с доски: единственное место, которое знает строку подключения.</summary>
public sealed class DbConnectionFactory(string path)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        ForeignKeys = true
    }.ToString();

    public IDbConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }
}
