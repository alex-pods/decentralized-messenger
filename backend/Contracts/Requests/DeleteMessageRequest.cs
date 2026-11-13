using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record DeleteMessageRequest(
    [property: JsonPropertyName("for_everyone")] bool? ForEveryone);
