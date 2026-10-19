using System.Text.Json.Serialization;

namespace MessengerBackend;

public record DeleteMessageIn(
    [property: JsonPropertyName("for_everyone")] bool? ForEveryone);
