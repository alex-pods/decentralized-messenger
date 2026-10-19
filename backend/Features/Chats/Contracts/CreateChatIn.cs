using System.Text.Json.Serialization;

namespace MessengerBackend;

public record CreateChatIn(
    [property: JsonPropertyName("another_user_id")] long? AnotherUserId);
