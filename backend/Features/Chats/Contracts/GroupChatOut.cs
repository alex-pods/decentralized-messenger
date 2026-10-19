using System.Text.Json.Serialization;

namespace MessengerBackend;

public record GroupChatOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("created_by")] long? CreatedBy,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("last_message")] MessageOut? LastMessage);
