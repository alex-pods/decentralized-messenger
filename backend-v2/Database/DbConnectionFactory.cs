using System.Data;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Database;


public sealed class DbConnectionFactory
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
