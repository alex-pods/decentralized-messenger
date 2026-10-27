using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth");

        group.MapPost("/register", async (RegisterRequest request, IAuthService service) =>
        {
            await service.Register(request.Login, request.Password);
            return Results.StatusCode(StatusCodes.Status201Created);
        })
        .WithName("Register")
        .WithSummary("Регистрация");

        group.MapPost("/login", async (LoginRequest request, IAuthService service) =>
            Results.Ok(await service.Login(request.Login, request.Password)))
        .WithName("Login")
        .WithSummary("Вход")
        .Produces<TokenResponse>();

        group.MapPost("/logout", async (HttpContext context, IAuthService service) =>
        {
            await service.Logout(CurrentUser.GetToken(context));
            return Results.NoContent();
        })
        .WithName("Logout")
        .WithSummary("Выход");
    }
}
