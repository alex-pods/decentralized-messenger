using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record MessageResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("sender_id")] long SenderId,
    [property: JsonPropertyName("reply_to_id")] long? ReplyToId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content_state")] string ContentState,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("edited_at")] DateTimeOffset? EditedAt,
    [property: JsonPropertyName("read_at")] DateTimeOffset? ReadAt,
    [property: JsonPropertyName("deleted_at")] DateTimeOffset? DeletedAt,
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentResponse>? Attachments,
    [property: JsonPropertyName("reactions")] IReadOnlyList<ReactionResponse>? Reactions);
