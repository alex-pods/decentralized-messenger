using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record ForwardedFromResponse(
    [property: JsonPropertyName("author_id")] string? AuthorId,
    [property: JsonPropertyName("author_name")] string AuthorName,
    [property: JsonPropertyName("original_sent_at")] DateTimeOffset OriginalSentAt);
