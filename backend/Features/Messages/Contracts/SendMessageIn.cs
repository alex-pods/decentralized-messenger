using System.Text.Json.Serialization;

namespace MessengerBackend;

public record SendMessageIn(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("reply_to_id")] long? ReplyToId,
    [property: JsonPropertyName("client_ref")] string? ClientRef);
