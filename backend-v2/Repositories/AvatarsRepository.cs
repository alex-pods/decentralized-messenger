using Dapper;
using Messenger.Api.Database;
using Messenger.Api.Infrastructure;
using Messenger.Api.Models;

namespace Messenger.Api.Repositories;

public interface IAvatarsRepository
{
    Task<Dictionary<long, Avatar>> GetByUserIds(IReadOnlyCollection<long> userIds);
    Task Upsert(Avatar avatar);
}

public sealed class AvatarsRepository(DbConnectionFactory db) : IAvatarsRepository
{
    public async Task<Dictionary<long, Avatar>> GetByUserIds(IReadOnlyCollection<long> userIds)
    {
        if (userIds.Count == 0) return [];
        using var connection = db.Open();
        var rows = await connection.QueryAsync<AvatarRaw>(
            "SELECT user_id AS UserId, path AS Path, content_type AS ContentType FROM avatars WHERE user_id IN @userIds",
            new { userIds = userIds.Distinct().ToArray() });
        return rows.ToDictionary(
            row => row.UserId,
            row => new Avatar(row.UserId, Crypto.Decrypt(row.Path)!, row.ContentType));
    }

    public async Task Upsert(Avatar avatar)
    {
        using var connection = db.Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO avatars(user_id, path, content_type) VALUES(@UserId, @Path, @ContentType)
            ON CONFLICT(user_id) DO UPDATE SET path = excluded.path, content_type = excluded.content_type,
                updated_at = CURRENT_TIMESTAMP
            """,
            new
            {
                avatar.UserId,
                Path = Crypto.Encrypt(avatar.Path),
                avatar.ContentType
            });
    }

    private sealed class AvatarRaw
    {
        public long UserId { get; set; }
        public string Path { get; set; } = "";
        public string ContentType { get; set; } = "";
    }
}
