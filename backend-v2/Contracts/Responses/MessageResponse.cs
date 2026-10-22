using System.Text.Json.Serialization;
using Messenger.Api.Models;

namespace Messenger.Api.Contracts.Responses;

public sealed record MessageResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("sender_id")] long SenderId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sent_at")] string SentAt)
{
    public static MessageResponse From(Message m) => new(m.Id, m.ChatId, m.SenderId, m.Text, m.SentAt);
}
