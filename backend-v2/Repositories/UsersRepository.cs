using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IUsersRepository
{
    Task<List<User>> GetAll();
    Task<User?> GetById(long id);
    Task<User?> GetByTag(string tag);
    Task<long> Insert(string tag, string name, string passwordHash);
}

/// <summary>Только SQL по таблице users. Никаких правил и HTTP.</summary>
public sealed class UsersRepository : IUsersRepository
{
    private readonly DbConnectionFactory _db;

    public UsersRepository(DbConnectionFactory db){
        _db = db;
    }

    public async Task<List<User>> GetAll(){
        using var connection = _db.Open();

        var users = await connection.QueryAsync<User>(
            """
            SELECT
                id AS Id,
                tag AS Tag,
                name AS Name,
                password_hash AS PasswordHash
            FROM users
            ORDER BY id;
            """
        );

        return users.ToList();
    }

    public async Task<User?> GetById(long id){
        if(id <= 0){
            throw new ArgumentOutOfRangeException(
                nameof(id),
                id,
                "Идентификатор должен быть положительным."
            );
        }
        using var connection = _db.Open();

        return await connection.QuerySingleOrDefaultAsync<User>(
            """
            SELECT
                id AS Id,
                tag AS Tag,
                name AS Name,
                password_hash AS PasswordHash
            FROM users
            WHERE id = @id;
            """,
            new { id }
        );
    }

    public async Task<User?> GetByTag(string tag){
        if(string.IsNullOrWhiteSpace(tag)){
            throw new ArgumentException(
                "Тег не должен быть пустым.",
                nameof(tag)
            );
        }

        using var connection = _db.Open();

        return await connection.QuerySingleOrDefaultAsync<User>(
            """
            SELECT
                id AS Id,
                tag AS Tag,
                name AS Name,
                password_hash AS PasswordHash
            FROM users
            WHERE tag = @tag;
            """,
            new { tag }
        );
    }

    public async Task<long> Insert(
        string tag,
        string name,
        string passwordHash
    ){
        if(string.IsNullOrWhiteSpace(tag)){
            throw new ArgumentException(
                "Тег не должен быть пустым.",
                nameof(tag)
            );
        }

        if(string.IsNullOrWhiteSpace(name)){
            throw new ArgumentException(
                "Имя не должно быть пустым.",
                nameof(name)
            );
        }

        if(string.IsNullOrWhiteSpace(passwordHash)){
            throw new ArgumentException(
                "Хеш пароля не должен быть пустым.",
                nameof(passwordHash)
            );
        }

        using var connection = _db.Open();

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO users(tag, name, password_hash)
            VALUES(@tag, @name, @passwordHash)
            RETURNING id;
            """,
            new { tag, name, passwordHash }
        );
    }
}
