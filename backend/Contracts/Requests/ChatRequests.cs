using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record CreateDirectRequest(
    [property: JsonPropertyName("another_user_id")] string AnotherUserId,
    [property: JsonPropertyName("chat_id")] string ChatId);

public sealed record CreateGroupRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("member_ids")] IReadOnlyCollection<string>? MemberIds,
    [property: JsonPropertyName("chat_id")] string ChatId);

public sealed record AddMemberRequest(
    [property: JsonPropertyName("user_id")] string UserId);

public sealed record SetRoleRequest(
    [property: JsonPropertyName("role")] string Role);

public sealed record TransferOwnerRequest(
    [property: JsonPropertyName("user_id")] string UserId);

public sealed record MarkChatReadRequest(
    [property: JsonPropertyName("message_id")] string MessageId);
