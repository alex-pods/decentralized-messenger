using System.Text.Json.Serialization;

namespace MessengerBackend;

public record MessageOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("sender_id")] long SenderId,
    [property: JsonPropertyName("reply_to_id")] long? ReplyToId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("content_state")] string ContentState,
    [property: JsonPropertyName("sent_at")] string SentAt,
    [property: JsonPropertyName("delivered_at")] string? DeliveredAt,
    [property: JsonPropertyName("edited_at")] string? EditedAt,
    [property: JsonPropertyName("read_at")] string? ReadAt,
    [property: JsonPropertyName("deleted_at")] string? DeletedAt,
    [property: JsonPropertyName("attachments")] List<AttachmentOut> Attachments,
    [property: JsonPropertyName("reactions")] List<ReactionOut> Reactions);
