using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record PinMessageRequest(
    [property: JsonPropertyName("message_id")] long MessageId);
