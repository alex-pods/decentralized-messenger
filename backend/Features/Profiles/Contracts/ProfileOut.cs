using System.Text.Json.Serialization;

namespace MessengerBackend;

public record ProfileOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("path_to_avatar_file")] string? PathToAvatarFile,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("join_date")] string JoinDate,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt);
