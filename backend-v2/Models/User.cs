namespace Messenger.Api.Models;

/// <summary>A row from the users table.</summary>
public sealed record User(
    long Id,
    string Tag,
    string Name,
    string? Bio,
    string CreatedAt,
    string? LastSeenAt,
    string PasswordHash);
