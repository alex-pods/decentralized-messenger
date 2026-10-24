using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Messenger.Api.Endpoints;

public static class MessagesEndpoints
{
    public static void MapMessagesEndpoints(this WebApplication app)
    {
        app.MapGet("/chats/{chat_id:long}/messages",
            async (long chat_id, long? before_id, long? after_id, int? limit,
                HttpContext context, IMessagesService service) =>
                Results.Ok(await service.GetHistory(CurrentUser.GetId(context), chat_id, before_id, after_id, limit)))
            .WithName("GetMessageHistory")
            .WithSummary("История сообщений")
            .Produces<List<MessageResponse>>();

        app.MapPost("/chats/{chat_id:long}/messages",
            async (long chat_id, SendMessageRequest request, HttpContext context, IMessagesService service) =>
            {
                var sent = await service.Send(CurrentUser.GetId(context), chat_id, request);
                return Results.Json(new MessageIdResponse(sent.Id),
                    statusCode: sent.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            })
            .WithName("SendMessage")
            .WithSummary("Отправить сообщение")
            .Produces<MessageIdResponse>(StatusCodes.Status201Created)
            .Produces<MessageIdResponse>(StatusCodes.Status200OK);

        app.MapPost("/chats/{chat_id:long}/read",
            async (long chat_id, MarkReadRequest request, HttpContext context, IMessagesService service) =>
            {
                await service.MarkRead(CurrentUser.GetId(context), chat_id, request.MessageId);
                return Results.NoContent();
            })
            .WithName("MarkChatRead")
            .WithSummary("Подтвердить прочтение");

        app.MapPatch("/messages/{message_id:long}",
            async (long message_id, EditMessageRequest request, HttpContext context, IMessagesService service) =>
            {
                await service.Edit(CurrentUser.GetId(context), message_id, request);
                return Results.NoContent();
            })
            .WithName("EditMessage")
            .WithSummary("Редактировать своё сообщение");

        app.MapDelete("/messages/{message_id:long}",
            async (long message_id, [FromBody] DeleteMessageRequest? request, HttpContext context,
                IMessagesService service) =>
            {
                await service.Delete(CurrentUser.GetId(context), message_id, request);
                return Results.NoContent();
            })
            .WithName("DeleteMessage")
            .WithSummary("Удалить сообщение");
    }
}
