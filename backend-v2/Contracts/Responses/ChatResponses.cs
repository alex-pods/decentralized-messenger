using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

public sealed record ReactionResponse(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("user_ids")] IReadOnlyList<long> UserIds);

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
