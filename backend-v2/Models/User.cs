using Messenger.Api.Infrastructure;

namespace Messenger.Api.Models;

/// <summary>A row from the users table.</summary>
public sealed record User(
    long Id,
    string Tag,
    Secret Name,
    Secret? Bio,
    string CreatedAt,
    string? LastSeenAt,
    Secret PasswordHash);
