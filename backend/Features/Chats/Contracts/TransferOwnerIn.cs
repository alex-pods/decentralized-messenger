using System.Text.Json.Serialization;

namespace MessengerBackend;

public record TransferOwnerIn(
    [property: JsonPropertyName("user_id")] long UserId);
