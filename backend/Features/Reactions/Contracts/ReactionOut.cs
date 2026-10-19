using System.Text.Json.Serialization;

namespace MessengerBackend;

public record ReactionOut(
    [property: JsonPropertyName("emoji")] string Emoji,
    [property: JsonPropertyName("user_ids")] List<long> UserIds);
