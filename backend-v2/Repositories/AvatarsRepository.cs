using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IAvatarsRepository
{
    /// <summary>Аватары сразу для многих пользователей — одним запросом, без N+1.</summary>
    Task<Dictionary<long, Avatar>> GetByUserIds(IReadOnlyCollection<long> userIds);
}

public sealed class AvatarsRepository(DbConnectionFactory db) : IAvatarsRepository
{
    public async Task<Dictionary<long, Avatar>> GetByUserIds(IReadOnlyCollection<long> userIds)
    {
        if (userIds.Count == 0) return [];
        using var c = db.Open();
        var rows = await c.QueryAsync<Avatar>(
            "SELECT user_id AS UserId, url FROM avatars WHERE user_id IN @userIds", new { userIds });
        return rows.ToDictionary(a => a.UserId);
    }
}
