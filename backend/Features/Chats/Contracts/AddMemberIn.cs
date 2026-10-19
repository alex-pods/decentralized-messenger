using System.Text.Json.Serialization;

namespace MessengerBackend;

public record AddMemberIn(
    [property: JsonPropertyName("user_id")] long UserId);
