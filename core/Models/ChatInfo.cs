namespace Messenger.Core.Models;

public sealed record ChatInfo(
    long Id,
    string Type,
    string? Title,
    long? PeerUserId,
    DateTime CreatedAt);