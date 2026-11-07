using System.Text.Json.Serialization;
using Messenger.Api.Models;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Contracts.Responses;

public sealed record ProfileResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("path_to_avatar_file")] string? AvatarPath,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("join_date")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt)
{
    public static ProfileResponse From(User user, string? avatarPath) =>
        new(user.Id, user.Tag, user.Name, avatarPath, user.Bio,
            SqliteTime.Parse(user.CreatedAt), SqliteTime.ParseOrNull(user.LastSeenAt));
}
