using System.Data;
using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Models;
using Messenger.Identifiers;

namespace Messenger.Api.Repositories;

public interface IUsersRepository
{
    Task<User?> GetById(string id);
    Task<User?> GetByTag(string tag);
    Task<List<UserSearchResult>> Search(string viewerId, string query, int limit, bool prefix);
    Task<string> Insert(string tag, Secret name, Secret passwordHash);
    Task UpdateProfile(string id, Secret? name, bool updateBio, Secret? bio, bool? allowTagSearch, IDbTransaction? transaction = null);
    Task TouchLastSeen(string id);
}

/// <summary>Database access for users.</summary>
public sealed class UsersRepository(DbConnectionFactory db) : IUsersRepository
{
    private const string Columns = "id, tag, name, bio, created_at AS CreatedAt, last_seen_at AS LastSeenAt, allow_tag_search AS AllowTagSearch, password_hash AS PasswordHash";

    public async Task<User?> GetById(string id)
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

    public async Task<List<UserSearchResult>> Search(string viewerId, string query, int limit, bool prefix)
    {
        using var connection = db.Open();
        // The NOCASE tag index serves both the exact lookup and the escaped prefix scan.
        var predicate = prefix ? "u.tag LIKE @pattern ESCAPE '~'" : "u.tag = @query COLLATE NOCASE";
        var pattern = prefix ? query.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_") + "%" : null;
        var users = await connection.QueryAsync<UserSearchResult>(
            $"""
            SELECT u.id, u.tag, u.name
            FROM users u
            WHERE {predicate} AND (
                u.allow_tag_search = 1 OR EXISTS (
                    SELECT 1
                    FROM users_chats mine
                    WHERE mine.user_id = @viewerId AND (
                        EXISTS (
                            SELECT 1 FROM users_chats peer
                            WHERE peer.user_id = u.id AND peer.chat_id = mine.chat_id
                        ) OR EXISTS (
                            SELECT 1 FROM group_activity history
                            WHERE history.chat_id = mine.chat_id AND history.user_id = u.id
                        )
                    )
                )
            )
            ORDER BY u.tag LIMIT @limit
            """, new { viewerId, query, pattern, limit });
        return users.ToList();
    }

    public async Task<string> Insert(string tag, Secret name, Secret passwordHash)
    {
        using var connection = db.Open();
        var id = EntityIds.NewUser();
        await connection.ExecuteAsync("INSERT INTO users(id, tag, name, password_hash) VALUES(@id, @tag, @name, @passwordHash)",
            new { id, tag, name, passwordHash });
        return id;
    }

    public async Task UpdateProfile(string id, Secret? name, bool updateBio, Secret? bio, bool? allowTagSearch, IDbTransaction? transaction = null)
    {
        using var ownedConnection = transaction == null ? db.Open() : null;
        var connection = transaction?.Connection ?? ownedConnection!;
        await connection.ExecuteAsync(
            "UPDATE users SET name = COALESCE(@name, name), bio = CASE WHEN @updateBio THEN @bio ELSE bio END, allow_tag_search = COALESCE(@allowTagSearch, allow_tag_search) WHERE id = @id",
            new { id, name, updateBio, bio, allowTagSearch }, transaction);
    }

    public async Task TouchLastSeen(string id)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            "UPDATE users SET last_seen_at = CURRENT_TIMESTAMP WHERE id = @id AND (last_seen_at IS NULL OR last_seen_at < datetime('now', '-1 minute'))",
            new { id });
    }
}
