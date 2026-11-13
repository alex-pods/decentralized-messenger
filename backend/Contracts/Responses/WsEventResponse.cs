using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record WsEventResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("chat_id")] long? ChatId = null,
    [property: JsonPropertyName("user_id")] long? UserId = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("message")] MessageResponse? Message = null,
    [property: JsonPropertyName("message_id")] long? MessageId = null,
    [property: JsonPropertyName("for_everyone")] bool? ForEveryone = null,
    [property: JsonPropertyName("last_read_message_id")] long? LastReadMessageId = null,
    [property: JsonPropertyName("emoji")] string? Emoji = null,
    [property: JsonPropertyName("chat")] object? Chat = null,
    [property: JsonPropertyName("user")] UserCardResponse? User = null,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("attachment")] AttachmentResponse? Attachment = null
);

