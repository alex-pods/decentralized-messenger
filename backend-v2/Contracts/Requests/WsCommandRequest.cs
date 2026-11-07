using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;


public sealed record WsCommandRequest(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("chat_id")] long? ChatId = null
);