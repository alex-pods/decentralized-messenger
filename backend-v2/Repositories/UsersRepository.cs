using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IUsersRepository
{
    Task<List<User>> GetAll();
    Task<User?> GetById(long id);
}

/// <summary>Только SQL по таблице users. Никаких правил и HTTP.</summary>
public sealed class UsersRepository(DbConnectionFactory db) : IUsersRepository
{
    public async Task<List<User>> GetAll()
    {
        using var c = db.Open();
        return (await c.QueryAsync<User>("SELECT id, tag, name FROM users ORDER BY id")).ToList();
    }

    public async Task<User?> GetById(long id)
    {
        using var c = db.Open();
        return await c.QuerySingleOrDefaultAsync<User>("SELECT id, tag, name FROM users WHERE id = @id", new { id });
    }
}
