using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Responses;

/// <summary>«User + Avatar + Messages» с доски — то, что уходит клиенту.</summary>
public sealed record UserResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("last_message")] MessageResponse? LastMessage);
