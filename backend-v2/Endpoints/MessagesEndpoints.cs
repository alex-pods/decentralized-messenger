using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Endpoints;

public static class MessagesEndpoints
{
    public static void MapMessagesEndpoints(this WebApplication app)
    {
        app.MapPatch("/messages/{message_id:long}",
            async (long message_id, EditMessageRequest request, HttpContext context,
                IMessagesService service, CancellationToken cancellationToken) =>
            {
                await service.EditAsync(message_id, CurrentUser.GetId(context), request, cancellationToken);
                return Results.NoContent();
            })
            .WithName("EditMessage")
            .WithSummary("Редактировать своё сообщение");

        app.MapDelete("/messages/{message_id:long}",
            async (long message_id, [FromBody] DeleteMessageRequest? request, HttpContext context,
                IMessagesService service, CancellationToken cancellationToken) =>
            {
                if (request?.ForEveryone is null)
                    throw AppException.BadRequest("for_everyone is required");
                await service.DeleteAsync(message_id, CurrentUser.GetId(context), request.ForEveryone.Value,
                    cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteMessage")
            .WithSummary("Удалить сообщение");
    }
}
