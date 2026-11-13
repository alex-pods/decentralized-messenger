using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record CreateDirectRequest(
    [property: JsonPropertyName("another_user_id")] long AnotherUserId);

public sealed record CreateGroupRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("member_ids")] IReadOnlyCollection<long>? MemberIds);

public sealed record AddMemberRequest(
    [property: JsonPropertyName("user_id")] long UserId);

public sealed record SetRoleRequest(
    [property: JsonPropertyName("role")] string Role);

public sealed record TransferOwnerRequest(
    [property: JsonPropertyName("user_id")] long UserId);

public sealed record MarkChatReadRequest(
    [property: JsonPropertyName("message_id")] long MessageId);
