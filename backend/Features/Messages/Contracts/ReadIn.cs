using System.Text.Json.Serialization;

namespace MessengerBackend;

public record ReadIn(
    [property: JsonPropertyName("message_id")] long MessageId);
