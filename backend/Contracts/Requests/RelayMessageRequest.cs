using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RelayMessageRequest(
    [property: JsonPropertyName("message_id")] string? MessageId,
    [property: JsonPropertyName("text")] string? Text);
