using Messenger.Api.Infrastructure;

namespace Messenger.Api.Repositories.Models;

public enum MessageContentState
{
    Stored,
    NotStored,
    Deleted
}

public sealed record MessageRow(
    string Id,
    string ChatId,
    string SenderId,
    string? ReplyToId,
    string? Text,
    MessageContentState ContentState,
    DateTimeOffset SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? DeletedAt,
    ForwardInfo? Forward = null,
    long Sequence = 0
);

public sealed record ForwardInfo(string? AuthorId, string AuthorName, DateTimeOffset OriginalSentAt);

public sealed record NewMessage(
    string Id,
    string ChatId,
    string SenderId,
    Secret Text,
    string? ReplyToId,
    Secret? ClientRef
);

public sealed record AddMessageResult(MessageRow Message, bool Created, bool ContentMatches = true);

public sealed record DeleteMessageResult(MessageRow Message, bool Changed, IReadOnlyList<string> AttachmentPaths);

public sealed record HistoryQuery(int Limit, string? BeforeId = null, string? AfterId = null,
    long? BeforeSequence = null, long? AfterSequence = null);

public sealed record PinRow(
    string MessageId,
    string PinnedBy,
    DateTimeOffset PinnedAt,
    MessageRow Message);

public sealed record ReactionRow(string Emoji, IReadOnlyList<string> UserIds);
