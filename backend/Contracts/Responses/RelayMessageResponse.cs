using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record RelayMessageResponse(
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sent_at")] DateTimeOffset SentAt,
    [property: JsonPropertyName("queued_connections")] int QueuedConnections);
