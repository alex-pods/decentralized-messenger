using System.Text.Json.Serialization;
using Messenger.Api.Models;

namespace Messenger.Api.Contracts.Responses;

public sealed record MessageResponse(
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
    [property: JsonPropertyName("attachments")] IReadOnlyList<AttachmentResponse> Attachments,
    [property: JsonPropertyName("reactions")] IReadOnlyList<object> Reactions)
{
    public static MessageResponse From(
        Message m,
        string? readAt = null,
        IReadOnlyList<AttachmentResponse>? attachments = null) =>
        new(m.Id, m.ChatId, m.SenderId, m.ReplyToId, m.Text, m.ContentState, ApiTimestamp.Format(m.SentAt),
            ApiTimestamp.FormatNullable(m.DeliveredAt), ApiTimestamp.FormatNullable(m.EditedAt),
            ApiTimestamp.FormatNullable(readAt), ApiTimestamp.FormatNullable(m.DeletedAt), attachments ?? [], []);
}
