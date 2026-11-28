using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

internal static class MessageRelayEndpoints
{
    internal sealed class RelayBody;

    internal static void MapMessageRelayEndpoints(this WebApplication app)
    {
        app.MapPost("/chats/{chatId}/messages/relay", async (string chatId, RelayMessageRequest request,
            MessageRelayService relay, HttpContext context, CancellationToken ct) =>
        {
            var result = await relay.SendAsync(chatId, CurrentUser.GetId(context), request, ct);
            return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
        })
        .WithMetadata(new RelayBody())
        .WithSummary("Передать текст подключённым участникам без сохранения содержимого");
    }
}
