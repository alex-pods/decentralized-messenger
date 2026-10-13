using System.Text.Json.Serialization;

namespace MessengerBackend;

// ---------- Входные DTO (авторизация REST — только Authorization: Bearer) ----------

public record RegisterIn(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password);

public record LoginIn(
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("password")] string Password);

/// <summary>Частичное обновление профиля: null/отсутствие = без изменений; bio:"" — очистить.</summary>
public record UpdateProfileIn(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("bio")] string? Bio);

public record CreateChatIn(
    [property: JsonPropertyName("another_user_id")] long? AnotherUserId);

public record CreateGroupIn(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("member_ids")] List<long> MemberIds);

public record AddMemberIn(
    [property: JsonPropertyName("user_id")] long UserId);

public record TransferOwnerIn(
    [property: JsonPropertyName("user_id")] long UserId);

public record SetRoleIn(
    [property: JsonPropertyName("role")] string Role);

public record ReadIn(
    [property: JsonPropertyName("message_id")] long MessageId);

/// <summary>client_ref — идемпотентный ключ отправителя (необязателен): повтор запроса
/// с тем же ключом и содержимым возвращает прежний message_id, с другим содержимым — 409.</summary>
public record SendMessageIn(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("reply_to_id")] long? ReplyToId,
    [property: JsonPropertyName("client_ref")] string? ClientRef);

public record EditMessageIn(
    [property: JsonPropertyName("text")] string Text);

/// <summary>for_everyone обязателен: true — для всех (deleted_at, отзыв вложений),
/// false — скрыть у себя.</summary>
public record DeleteMessageIn(
    [property: JsonPropertyName("for_everyone")] bool? ForEveryone);

public record ReactionIn(
    [property: JsonPropertyName("emoji")] string Emoji);

// ---------- Ответы ----------

public record TokenOut(
    [property: JsonPropertyName("user_session_token")] string UserSessionToken,
    [property: JsonPropertyName("expires_at")] string ExpiresAt);

public record ChatIdOut(
    [property: JsonPropertyName("chat_id")] long ChatId);

public record MessageIdOut(
    [property: JsonPropertyName("message_id")] long MessageId);

public record AttachmentOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("file_type")] string FileType,
    [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes);

public record ReactionOut(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("user_ids")] List<long> UserIds);

public record UserCard(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName);

public record ProfileOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("path_to_avatar_file")] string? PathToAvatarFile,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("join_date")] string JoinDate,
    [property: JsonPropertyName("last_seen_at")] string? LastSeenAt);

/// <summary>content_state: stored — сохранено; deleted — удалено для всех (заглушка);
/// not_stored — legacy-запись времён save_history (текст утрачен, новым сообщениям не бывает).</summary>
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

public record MemberOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joined_at")] string JoinedAt);

public record DirectChatOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("other_user")] UserCard OtherUser,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("last_message")] MessageOut? LastMessage);

public record GroupChatOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("last_message")] MessageOut? LastMessage);
