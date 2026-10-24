using System.Text.Json.Serialization;
using Messenger.Api.Models;

namespace Messenger.Api.Contracts.Responses;

public sealed record ProfileResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("path_to_avatar_file")] string? AvatarPath,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("join_date")] string JoinedAt,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt)
{
    public static ProfileResponse From(User user, string? avatarPath) =>
        new(user.Id, user.Tag, user.Name, avatarPath, user.Bio, ApiTimestamp.Format(user.CreatedAt),
            ApiTimestamp.FormatNullable(user.LastSeenAt));
}
