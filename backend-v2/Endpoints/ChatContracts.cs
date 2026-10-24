using System.Text.Json.Serialization;

namespace Messenger.Api.Endpoints;

public sealed record CreateDirectBody(
    [property: JsonPropertyName("another_user_id")] long AnotherUserId);

public sealed record CreateGroupBody(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("member_ids")] IReadOnlyCollection<long>? MemberIds);

public sealed record AddMemberBody(
    [property: JsonPropertyName("user_id")] long UserId);

public sealed record SetRoleBody(
    [property: JsonPropertyName("role")] string Role);

public sealed record TransferOwnerBody(
    [property: JsonPropertyName("user_id")] long UserId);

public sealed record ReadBody(
    [property: JsonPropertyName("message_id")] long MessageId);

public sealed record SendMessageBody(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reply_to_id")] long? ReplyToId,
    [property: JsonPropertyName("client_ref")] string? ClientRef);

public sealed record UserCardResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed record AttachmentResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("file_type")] string? FileType,
    [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes);

public sealed record ReactionResponse(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("user_ids")] IReadOnlyList<long> UserIds);

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

public sealed record DirectChatResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("other_user")] UserCardResponse OtherUser,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("last_message")] MessageResponse? LastMessage);

public sealed record GroupChatResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("last_message")] MessageResponse? LastMessage);

public sealed record MemberResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt);

public sealed record ChatIdResponse(
    [property: JsonPropertyName("chat_id")] long ChatId);

public sealed record MessageIdResponse(
    [property: JsonPropertyName("message_id")] long MessageId);

public sealed record ApiErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("details")] object? Details,
    [property: JsonPropertyName("trace_id")] string TraceId);
