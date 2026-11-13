using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record EditMessageRequest(
    [property: JsonPropertyName("text")] string? Text);
