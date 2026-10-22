using Messenger.Api.Common.Errors;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

/// <summary>«Ручки» пользователей: разобрать запрос → вызвать сервис → вернуть ответ. Без логики.</summary>
public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/users");

        g.MapGet("/", (UsersService s) => s.GetUsers());
        g.MapGet("/{id:long}", (long id, UsersService s) => s.GetUser(id));

        g.MapPatch("/me", () => ImplementMe.Result("PATCH /users/me — UsersService.UpdateProfile + UsersRepository.Update"));
        g.MapPost("/me/avatar", () => ImplementMe.Result("POST /users/me/avatar — сохранить файл, AvatarsRepository.Upsert"));
    }
}
