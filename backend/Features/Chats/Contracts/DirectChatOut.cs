using System.Text.Json.Serialization;

namespace MessengerBackend;

public record DirectChatOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("other_user")] UserCard OtherUser,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("last_message")] MessageOut? LastMessage);
