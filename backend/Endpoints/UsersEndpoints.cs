using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/users");

        group.MapGet("/me", async (HttpContext context, IUsersService service) =>
            Results.Ok(await service.GetProfile(CurrentUser.GetId(context))))
        .WithName("GetMyProfile")
        .WithSummary("Мой профиль")
        .Produces<ProfileResponse>();

        group.MapPatch("/me", async (HttpContext context, UpdateProfileRequest request, IUsersService service) =>
        {
            await service.UpdateProfile(CurrentUser.GetId(context), request);
            return Results.NoContent();
        })
        .WithName("UpdateMyProfile")
        .WithSummary("Изменить мой профиль");

        group.MapGet("/search", async (string? query, IUsersService service) =>
            Results.Ok(await service.Search(query ?? "")))
        .WithName("SearchUsers")
        .WithSummary("Поиск пользователей")
        .Produces<List<UserCardResponse>>();

        group.MapGet("/{user_id:long}", async (long user_id, IUsersService service) =>
            Results.Ok(await service.GetProfileById(user_id)))
        .WithName("GetUserProfile")
        .WithSummary("Профиль пользователя")
        .Produces<ProfileResponse>();

        group.MapPost("/me/avatar", async (HttpContext context, IAvatarsService service) =>
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                throw AppException.BadRequest("multipart/form-data with a file field is required");
            }
            var uploaded = form.Files.Where(file => file.Name == "file").ToList();
            if (uploaded.Count != 1)
                throw AppException.BadRequest("exactly one file field is required");
            var path = await service.Upload(CurrentUser.GetId(context), uploaded[0], context.RequestAborted);
            return Results.Json(new { path_to_avatar_file = path }, statusCode: StatusCodes.Status201Created);
        })
        .WithName("UploadMyAvatar")
        .WithSummary("Загрузить свой аватар")
        .DisableAntiforgery()
        .Produces(StatusCodes.Status201Created);

        group.MapGet("/{user_id:long}/avatar", async (long user_id, IAvatarsService service) =>
        {
            var avatar = await service.GetDownload(user_id);
            return Results.File(avatar.Path, avatar.ContentType);
        })
        .WithName("GetUserAvatar")
        .WithSummary("Скачать аватар пользователя");
    }
}
