using Messenger.Api.Infrastructure;

namespace Messenger.Api.Models;

/// <summary>A row from the users table.</summary>
public sealed record User(
    string Id,
    string Tag,
    Secret Name,
    Secret? Bio,
    string CreatedAt,
    string? LastSeenAt,
    long AllowTagSearch,
    Secret PasswordHash);

public sealed record UserSearchResult(string Id, string Tag, Secret Name);
