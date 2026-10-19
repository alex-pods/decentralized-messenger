using System.Text.Json.Serialization;

namespace MessengerBackend;

public record EditMessageIn(
    [property: JsonPropertyName("text")] string Text);
