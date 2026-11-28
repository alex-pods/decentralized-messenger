using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class ReactionsEndpoints
{
    public static void MapReactionsEndpoints(this WebApplication app)
    {
        app.MapPost(
            "/messages/{messageId}/reactions",
            async (
                string messageId,
                ReactionRequest request,
                HttpContext context,
                IReactionsService service,
                CancellationToken ct) =>
            {
                await service.AddAsync(
                    messageId,
                    CurrentUser.GetId(context),
                    request.Emoji ?? "",
                    ct);

                return Results.NoContent();
            });

        app.MapDelete(
            "/messages/{messageId}/reactions/{emoji}",
            async (
                string messageId,
                string emoji,
                HttpContext context,
                IReactionsService service,
                CancellationToken ct) =>
            {
                await service.RemoveAsync(
                    messageId,
                    CurrentUser.GetId(context),
                    Uri.UnescapeDataString(emoji),
                    ct);

                return Results.NoContent();
            });
    }
}