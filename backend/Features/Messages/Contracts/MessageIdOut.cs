using System.Text.Json.Serialization;

namespace MessengerBackend;

public record MessageIdOut(
    [property: JsonPropertyName("message_id")] long MessageId);
