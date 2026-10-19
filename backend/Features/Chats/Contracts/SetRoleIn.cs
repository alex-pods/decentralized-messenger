using System.Text.Json.Serialization;

namespace MessengerBackend;

public record SetRoleIn(
    [property: JsonPropertyName("role")] string Role);
