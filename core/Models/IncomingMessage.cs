namespace Messenger.Core.Models;

public sealed record IncomingMessage(
    long ServerId, long ChatId, long SenderId, long? ReplyToServerId,
    string? Text, DateTime SentAt, DateTime? EditedAt = null,
    IReadOnlyList<AttachmentInfo>? Attachments = null,
    Guid? ClientId = null, bool IsOutgoing = false,
    string ContentState = "stored", DateTime? DeliveredAt = null,
    DateTime? ReadAt = null, DateTime? DeletedAt = null,
    bool CountUnread = true);
