using System.Text.Json.Serialization;

namespace MessengerBackend;

public record LoginIn(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password);
