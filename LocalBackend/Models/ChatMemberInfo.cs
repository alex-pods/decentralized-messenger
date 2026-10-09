namespace LocalBackend.Models;

public sealed record ChatMemberInfo(
    long UserId,
    string Role,
    DateTime JoinedAt);