namespace LocalBackend.Models;

public sealed record IncomingMessage(
    long ServerId,
    long ChatId,
    long SenderId,
    long? ReplyToServerId,
    string Text,
    DateTime SentAt,
    DateTime? EditedAt = null,
    IReadOnlyList<AttachmentInfo>? Attachments = null);