using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IUsersRepository
{
    Task<User?> GetById(long id);
    Task<User?> GetByTag(string tag);
    Task<List<User>> Search(string query, int limit);
    Task<long> Insert(string tag, Secret name, Secret passwordHash);
    Task UpdateProfile(long id, Secret? name, bool updateBio, Secret? bio);
    Task TouchLastSeen(long id);
}

/// <summary>Database access for users.</summary>
public sealed class UsersRepository(DbConnectionFactory db) : IUsersRepository
{
    private const string Columns = "id, tag, name, bio, created_at AS CreatedAt, last_seen_at AS LastSeenAt, password_hash AS PasswordHash";

    public async Task<User?> GetById(long id)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<User>(
            $"SELECT {Columns} FROM users WHERE id = @id", new { id });
    }

    public async Task<User?> GetByTag(string tag)
    {
        using var connection = db.Open();
        return await connection.QuerySingleOrDefaultAsync<User>(
            $"SELECT {Columns} FROM users WHERE tag = @tag COLLATE NOCASE", new { tag });
    }

    public async Task<List<User>> Search(string query, int limit)
    {
        using var connection = db.Open();
        var users = await connection.QueryAsync<User>($"SELECT {Columns} FROM users ORDER BY tag");
        return users
            .Where(user => user.Tag.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           user.Name.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    public async Task<long> Insert(string tag, Secret name, Secret passwordHash)
    {
        using var connection = db.Open();
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO users(tag, name, password_hash) VALUES(@tag, @name, @passwordHash); SELECT last_insert_rowid()",
            new { tag, name, passwordHash });
    }

    public async Task UpdateProfile(long id, Secret? name, bool updateBio, Secret? bio)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "UPDATE users SET name = COALESCE(@name, name), bio = CASE WHEN @updateBio THEN @bio ELSE bio END WHERE id = @id",
            new { id, name, updateBio, bio });
    }

    public async Task TouchLastSeen(long id)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "UPDATE users SET last_seen_at = CURRENT_TIMESTAMP WHERE id = @id AND (last_seen_at IS NULL OR last_seen_at < datetime('now', '-1 minute'))",
            new { id });
    }
}
