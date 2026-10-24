namespace Messenger.Api.Models;

/// <summary>A row from the avatars table.</summary>
public sealed record Avatar(long UserId, string Path, string ContentType);
