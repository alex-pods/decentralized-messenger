using System.Text.Json.Serialization;

namespace MessengerBackend;

public record UpdateProfileIn(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("bio")] string? Bio);
