using Messenger.Api.Common.Errors;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class MessagesEndpoints
{
    public static void MapMessagesEndpoints(this WebApplication app)
    {
        app.MapGet("/chats/{chatId:long}/messages", (long chatId, long? before_id, int? limit, MessagesService s) =>
            s.GetHistory(chatId, before_id, limit));

        app.MapPost("/chats/{chatId:long}/messages", (long chatId) =>
            ImplementMe.Result("POST /chats/{id}/messages — MessagesService.Send: проверка участника, MessagesRepository.Insert"));
        app.MapPatch("/messages/{id:long}", (long id) =>
            ImplementMe.Result("PATCH /messages/{id} — правка своего сообщения"));
        app.MapDelete("/messages/{id:long}", (long id) =>
            ImplementMe.Result("DELETE /messages/{id} — удаление"));
    }
}
