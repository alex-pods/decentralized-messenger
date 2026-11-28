using System.Data;
using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IAvatarsRepository
{
    Task<Dictionary<string, Avatar>> GetByUserIds(IReadOnlyCollection<string> userIds);
    Task<string?> Swap(Avatar avatar, IDbTransaction transaction);
}

public sealed class AvatarsRepository(DbConnectionFactory db) : IAvatarsRepository
{
    public async Task<Dictionary<string, Avatar>> GetByUserIds(IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) return [];
        using var connection = db.Open();
        var rows = await connection.QueryAsync<Avatar>(
            "SELECT user_id AS UserId, path AS Path, content_type AS ContentType FROM avatars WHERE user_id IN @userIds",
            new { userIds = userIds.Distinct().ToArray() });
        return rows.ToDictionary(avatar => avatar.UserId);
    }

    public async Task<string?> Swap(Avatar avatar, IDbTransaction transaction)
    {
        var connection = transaction.Connection!;
        var previous = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT path FROM avatars WHERE user_id = @UserId", avatar, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO avatars(user_id, path, content_type) VALUES(@UserId, @Path, @ContentType)
            ON CONFLICT(user_id) DO UPDATE SET path = excluded.path, content_type = excluded.content_type,
                updated_at = CURRENT_TIMESTAMP
            """, avatar, transaction);
        return previous;
    }
}
