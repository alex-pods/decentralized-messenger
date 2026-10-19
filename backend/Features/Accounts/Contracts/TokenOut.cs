using System.Text.Json.Serialization;

namespace MessengerBackend;

public record TokenOut(
    [property: JsonPropertyName("user_session_token")] string UserSessionToken,
    [property: JsonPropertyName("expires_at")] string ExpiresAt);
