using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record TokenResponse(
    [property: JsonPropertyName("user_session_token")] string UserSessionToken,
    [property: JsonPropertyName("expires_at")] string ExpiresAt);

public sealed record MessageIdResponse(
    [property: JsonPropertyName("message_id")] long MessageId);
