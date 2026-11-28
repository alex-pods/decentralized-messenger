using Messenger.Api.Infrastructure;

namespace Messenger.Api.Models;

/// <summary>A row from the avatars table.</summary>
public sealed record Avatar(string UserId, Secret Path, string ContentType);
