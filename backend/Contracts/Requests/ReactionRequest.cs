using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record ReactionRequest(
    [property: JsonPropertyName("emoji")] string? Emoji);