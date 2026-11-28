using Microsoft.AspNetCore.Http.Features;
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

        group.MapGet("/search", async (HttpContext context, string? query, bool? prefix, IUsersService service) =>
            Results.Ok(await service.Search(CurrentUser.GetId(context), query ?? "", prefix == true)))
        .WithName("SearchUsers")
        .WithSummary("Поиск по тегу. Скрытые теги доступны собеседникам и участникам общих групп, включая историю доступных групп.")
        .Produces<List<UserCardResponse>>();

        group.MapGet("/{user_id}", async (string user_id, IUsersService service) =>
            Results.Ok(await service.GetProfileById(user_id)))
        .WithName("GetUserProfile")
        .WithSummary("Профиль пользователя")
        .Produces<ProfileResponse>();

        group.MapPost("/me/profile", async (HttpContext context, IUsersService service) =>
        {
            var (form, file) = await ReadProfilePhoto(context);
            await service.UpdateProfileWithAvatar(CurrentUser.GetId(context), new UpdateProfileRequest
            {
                DisplayName = form.TryGetValue("display_name", out var name) ? name.ToString() : null,
                Bio = form.TryGetValue("bio", out var bio) ? bio.ToString() : null
            }, file, context.RequestAborted);
            return Results.NoContent();
        })
        .WithName("UpdateMyProfileWithAvatar")
        .WithSummary("Сохранить имя, описание и аватар профиля одной операцией")
        .DisableAntiforgery()
        .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/me/avatar", async (HttpContext context, IAvatarsService service) =>
        {
            var (_, file) = await ReadProfilePhoto(context);
            var path = await service.Upload(CurrentUser.GetId(context), file, context.RequestAborted);
            return Results.Json(new { path_to_avatar_file = path }, statusCode: StatusCodes.Status201Created);
        })
        .WithName("UploadMyAvatar")
        .WithSummary("Загрузить свой аватар")
        .DisableAntiforgery()
        .Produces(StatusCodes.Status201Created);

        group.MapGet("/{user_id}/avatar", async (string user_id, IAvatarsService service) =>
        {
            var avatar = await service.GetDownload(user_id);
            return Results.File(avatar.Path, avatar.ContentType);
        })
        .WithName("GetUserAvatar")
        .WithSummary("Скачать аватар пользователя");
    }

    static async Task<(IFormCollection Form, IFormFile File)> ReadProfilePhoto(HttpContext context)
    {
        const long maxRequestBytes = 5 * 1024 * 1024 + 16 * 1024;
        if (context.Request.ContentLength > maxRequestBytes)
            throw AppException.PayloadTooLarge("avatar exceeds 5 MB");
        var maxSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxSize is { IsReadOnly: false }) maxSize.MaxRequestBodySize = maxRequestBytes;
        if (context.Request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            throw AppException.BadRequest("multipart/form-data with a file field is required");
        IFormCollection form;
        try { form = await context.Request.ReadFormAsync(context.RequestAborted); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            throw AppException.BadRequest("multipart/form-data with a file field is required");
        }
        var files = form.Files.GetFiles("file");
        if (files.Count != 1)
            throw AppException.BadRequest("exactly one file field is required");
        return (form, files[0]);
    }
}
