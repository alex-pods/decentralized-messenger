using System.Text.Json.Serialization;

namespace Messenger.Api.Contracts.Requests;

public sealed record SendMessageRequest([property: JsonPropertyName("text")] string Text);
