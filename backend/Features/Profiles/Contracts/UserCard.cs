using System.Text.Json.Serialization;

namespace MessengerBackend;

public record UserCard(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName);
