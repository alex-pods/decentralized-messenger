using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record PinResponse(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("pinned_by")] long PinnedBy,
    [property: JsonPropertyName("pinned_at")] DateTimeOffset PinnedAt,
    [property: JsonPropertyName("message")] MessageResponse Message);
