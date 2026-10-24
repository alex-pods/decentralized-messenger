namespace Messenger.Api.Models;

/// <summary>A row from the messages table.</summary>
public sealed record Message(
    long Id,
    long ChatId,
    long SenderId,
    string? Text,
    string SentAt,
    string? EditedAt,
    string? DeliveredAt,
    string? DeletedAt,
    long? ReplyToId,
    string ContentState);
