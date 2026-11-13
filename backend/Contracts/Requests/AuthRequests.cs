using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record RegisterRequest(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("password")] string? Password);

public sealed record LoginRequest(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("password")] string? Password);
