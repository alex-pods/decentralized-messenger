using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record SendMessageRequest(
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("reply_to_id")] string? ReplyToId,
    [property: JsonPropertyName("client_ref")] string? ClientRef);
