using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record UpdateProfileRequest
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }
}
