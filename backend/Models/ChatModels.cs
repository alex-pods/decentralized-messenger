namespace Messenger.Api.Repositories.Models;

public enum ChatType
{
    Direct,
    Group
}

public enum MemberRole
{
    Member,
    Admin,
    Owner
}

public enum LeaveResult
{
    Left,
    GroupClosed,
    OwnerTransferRequired
}

public sealed record UserCardRow(string Id, string Tag, string DisplayName);

public sealed record ChatAccess(ChatType Type, MemberRole? Role);

public sealed record ChatSummary(
    string Id,
    ChatType Type,
    string? Title,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    UserCardRow? OtherUser,
    string? LastMessageId
);

public sealed record MemberRow(
    string UserId,
    string Tag,
    string DisplayName,
    MemberRole Role,
    DateTimeOffset JoinedAt
);

public sealed record MarkReadResult(
    string? LastReadMessageId,
    bool Changed,
    long LastReadSequence);

public sealed record LeaveGroupResult(
    LeaveResult Result,
    IReadOnlyList<string> AttachmentPaths);

public sealed record DirectChatResult(string ChatId, bool Created);
