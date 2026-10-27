namespace Messenger.Api.Repositories.Models;

public enum MessageContentState
{
    Stored,
    NotStored,
    Deleted
}

public sealed record MessageRow(
    long Id,
    long ChatId,
    long SenderId,
    long? ReplyToId,
    string? Text,
    MessageContentState ContentState,
    DateTimeOffset SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? DeletedAt
);

public sealed record NewMessage(
    long ChatId,
    long SenderId,
    string Text,
    long? ReplyToId,
    string? ClientRef
);

public sealed record AddMessageResult(MessageRow Message, bool Created);

public sealed record HistoryQuery(int Limit, long? BeforeId, long? AfterId);

public sealed record ReactionRow(string Emoji, IReadOnlyList<long> UserIds);
