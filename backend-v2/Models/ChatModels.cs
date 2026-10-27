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

public sealed record UserCardRow(long Id, string Tag, string DisplayName);

public sealed record ChatAccess(ChatType Type, MemberRole? Role);

public sealed record ChatSummary(
    long Id,
    ChatType Type,
    string? Title,
    long? CreatedBy,
    DateTimeOffset CreatedAt,
    UserCardRow? OtherUser,
    long? LastMessageId
);

public sealed record MemberRow(
    long UserId,
    string Tag,
    string DisplayName,
    MemberRole Role,
    DateTimeOffset JoinedAt
);

public sealed record DirectChatResult(long ChatId, bool Created);
