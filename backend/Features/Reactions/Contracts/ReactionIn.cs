using System.Text.Json.Serialization;

namespace MessengerBackend;

public record ReactionIn(
    [property: JsonPropertyName("emoji")] string Emoji);
