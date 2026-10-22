using Messenger.Api.Common.Errors;

namespace Messenger.Api.Endpoints;

public static class ChatsEndpoints
{
    public static void MapChatsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/chats");

        g.MapGet("/", () => ImplementMe.Result("GET /chats — ChatsService.GetMyChats: чаты + участники + последнее сообщение"));
        g.MapPost("/", () => ImplementMe.Result("POST /chats — создать личный чат"));
        g.MapPost("/group", () => ImplementMe.Result("POST /chats/group — создать группу"));
    }
}
