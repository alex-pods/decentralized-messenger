using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

/// <summary>«GetUsers Сервис» с доски: берёт данные из трёх репозиториев и собирает ответ.</summary>
public sealed class UsersService(IUsersRepository users, IAvatarsRepository avatars, IMessagesRepository messages)
{
    public async Task<List<UserResponse>> GetUsers()
    {
        var list = await users.GetAll();
        var ids = list.Select(u => u.Id).ToList();
        var avatarById = await avatars.GetByUserIds(ids);
        var lastById = await messages.GetLastBySenders(ids);

        return list.Select(u => new UserResponse(
            u.Id, u.Tag, u.Name,
            avatarById.GetValueOrDefault(u.Id)?.Url,
            lastById.TryGetValue(u.Id, out var m) ? MessageResponse.From(m) : null)).ToList();
    }

    public async Task<UserResponse> GetUser(long id)
    {
        var u = await users.GetById(id) ?? throw AppException.NotFound("пользователь не найден");
        var avatar = (await avatars.GetByUserIds([id])).GetValueOrDefault(id);
        var last = (await messages.GetLastBySenders([id])).GetValueOrDefault(id);
        return new UserResponse(u.Id, u.Tag, u.Name, avatar?.Url, last is null ? null : MessageResponse.From(last));
    }
}
