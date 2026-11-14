using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record ForwardMessageRequest(
    [property: JsonPropertyName("chat_id")] long? ChatId);
