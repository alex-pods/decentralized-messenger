using System.Text.Json.Serialization;

namespace MessengerBackend;

public record CreateGroupIn(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("member_ids")] List<long> MemberIds);
