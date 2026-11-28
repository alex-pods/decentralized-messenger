using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record ForwardMessageRequest(
    [property: JsonPropertyName("chat_id")] string? ChatId,
    [property: JsonPropertyName("message_id")] string MessageId);
