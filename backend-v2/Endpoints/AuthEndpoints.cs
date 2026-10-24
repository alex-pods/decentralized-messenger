using Messenger.Api.Common.Errors;

namespace Messenger.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/auth");

        g.MapPost("/register", () => ImplementMe.Result("POST /auth/register — AuthService.Register: хэш пароля, UsersRepository.Insert"));
        g.MapPost("/login", () => ImplementMe.Result("POST /auth/login — AuthService.Login: проверка пароля, SessionsRepository.Create"));
        g.MapPost("/logout", () => ImplementMe.Result("POST /auth/logout — SessionsRepository.Delete"));
    }
}
