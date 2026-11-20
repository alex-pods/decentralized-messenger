using Messenger.Api.Common.Errors;
using Messenger.Api.Models;
using Messenger.Api.Repositories;
namespace Messenger.Api.Endpoints;
public sealed class PublicThemeRead;
public static class ThemesEndpoints
{
    public static void MapThemesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/themes").WithTags("Themes");
        group.MapGet("/{id}", async (string id, ThemesRepository repository) => Results.Ok(await repository.Get(id)))
            .WithMetadata(new PublicThemeRead()).WithSummary("Read a shared theme without authentication");
        group.MapPost("/", async (HttpContext context, ThemeCreate request, ThemesRepository repository) =>
        {
            var theme = await repository.Create(CurrentUser.GetId(context), request);
            return Results.Created($"{context.Request.PathBase}/themes/{theme.Id}", theme);
        }).WithSummary("Publish a version 1 theme");
        group.MapPatch("/{id}", async (HttpContext context, string id, ThemeUpdate request, ThemesRepository repository) =>
            Results.Ok(await repository.Update(CurrentUser.GetId(context), id, request))).WithSummary("Owner-only partial update with expected version");
        group.MapDelete("/{id}", async (HttpContext context, string id, ThemesRepository repository) =>
        {
            await repository.Delete(CurrentUser.GetId(context), id); return Results.NoContent();
        }).WithSummary("Unpublish an owned theme");
    }
}
