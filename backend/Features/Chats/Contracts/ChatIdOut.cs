using System.Text.Json.Serialization;

namespace MessengerBackend;

public record ChatIdOut(
    [property: JsonPropertyName("chat_id")] long ChatId);
