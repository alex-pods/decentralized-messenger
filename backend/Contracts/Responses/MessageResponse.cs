using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record MessageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("sender_id")] string SenderId,
    [property: JsonPropertyName("reply_to_id")] string? ReplyToId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content_state")] string ContentState,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("edited_at")] DateTimeOffset? EditedAt,
    [property: JsonPropertyName("read_at")] DateTimeOffset? ReadAt,
    [property: JsonPropertyName("deleted_at")] DateTimeOffset? DeletedAt,
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentResponse>? Attachments,
    [property: JsonPropertyName("reactions")] IReadOnlyList<ReactionResponse>? Reactions,
    [property: JsonPropertyName("forwarded_from")] ForwardedFromResponse? ForwardedFrom = null,
    [property: JsonPropertyName("sequence")] long Sequence = 0);
