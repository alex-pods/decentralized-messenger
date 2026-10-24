using System.Data;
using Microsoft.Data.Sqlite;

namespace Messenger.Api.Database;


public sealed class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string path){
        var settings = new SqliteConnectionStringBuilder{
            DataSource = path,
            ForeignKeys = true
        };

        _connectionString = settings.ToString();
    }

    public IDbConnection Open(){
        var connection = new SqliteConnection(_connectionString);

        try{
            connection.Open();
            return connection;
        }
        catch{
            connection.Dispose();
            throw;
        }
    }
}
