using System.Text.Json.Serialization;

namespace MessengerBackend;

public record RegisterIn(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password);
