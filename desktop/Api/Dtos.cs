using System.Text.Json.Serialization;

namespace MessengerDesktop;

public sealed class ErrorDto
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class TokenDto
{
    [JsonPropertyName("user_session_token")] public string UserSessionToken { get; set; } = "";
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

public sealed class ChatIdDto
{
    [JsonPropertyName("chat_id")] public long ChatId { get; set; }
}

public sealed class MessageIdDto
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
}

public sealed class UserCardDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? "@" + Tag : $"{DisplayName}  @{Tag}";
}

public sealed class ProfileDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("bio")] public string? Bio { get; set; }
    [JsonPropertyName("join_date")] public string? JoinDate { get; set; }
    [JsonPropertyName("last_seen_at")] public string? LastSeenAt { get; set; }
}

public sealed class SettingsDto
{
    [JsonPropertyName("save_history")] public bool SaveHistory { get; set; }
}

public sealed class MessageDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("chat_id")] public long ChatId { get; set; }
    [JsonPropertyName("sender_id")] public long SenderId { get; set; }
    [JsonPropertyName("reply_to_id")] public long? ReplyToId { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("content_state")] public string ContentState { get; set; } = "stored";
    [JsonPropertyName("sent_at")] public string? SentAt { get; set; }
    [JsonPropertyName("delivered_at")] public string? DeliveredAt { get; set; }
    [JsonPropertyName("edited_at")] public string? EditedAt { get; set; }
    [JsonPropertyName("read_at")] public string? ReadAt { get; set; }
    [JsonPropertyName("deleted_at")] public string? DeletedAt { get; set; }
}

public sealed class MemberDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "member";
    [JsonPropertyName("joined_at")] public string? JoinedAt { get; set; }
}

public sealed class ChatDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("other_user")] public UserCardDto? OtherUser { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("created_by")] public long? CreatedBy { get; set; }
    [JsonPropertyName("last_message")] public MessageDto? LastMessage { get; set; }
}

public sealed class WsEventDto
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("chat_id")] public long? ChatId { get; set; }
    [JsonPropertyName("message")] public MessageDto? Message { get; set; }
    [JsonPropertyName("message_id")] public long? MessageId { get; set; }
    [JsonPropertyName("for_everyone")] public bool? ForEveryone { get; set; }
    [JsonPropertyName("user_id")] public long? UserId { get; set; }
    [JsonPropertyName("user")] public UserCardDto? User { get; set; }
    [JsonPropertyName("emoji")] public string? Emoji { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("last_read_message_id")] public long? LastReadMessageId { get; set; }
}

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
