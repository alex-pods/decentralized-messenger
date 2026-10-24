using System.Text.Json.Serialization;
using Messenger.Api.Models;

namespace Messenger.Api.Contracts.Responses;

public sealed record UserCardResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("display_name")] string DisplayName)
{
    public static UserCardResponse From(User user) => new(user.Id, user.Tag, user.Name);
}
