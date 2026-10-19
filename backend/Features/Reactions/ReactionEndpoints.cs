using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Endpoints;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend;

public static class ReactionEndpoints
{
    public static void MapReactionEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();
        var filesRoot = AppCtx.FilesRootFromEnv();
        app.MapPost("/messages/{message_id:long}/reactions", async (HttpContext ctx, long message_id, [FromBody] ReactionIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var emoji = (r.Emoji ?? "").Trim();
            if (emoji.Length is < 1 or > 32) return Errors.Err(400, "emoji должна быть 1-32 символа");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = MessageRepository.ById(c, message_id);
                ChatRepository.RequireMember(c, m.Chat_Id, me.Id);
                if (m.Deleted_At is not null) return Errors.Err(409, "message_deleted", "message_deleted");
                c.Execute("INSERT OR IGNORE INTO message_reactions(message_id, user_id, emoji) VALUES(@m, @u, @e)",
                    new { m = message_id, u = me.Id, e = emoji });
                await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                    ChatRepository.MemberIds(c, m.Chat_Id), me.Id,
                    new WsEvent("reaction_added", m.Chat_Id) { MessageId = message_id, UserId = me.Id, Emoji = emoji }));
                return Results.NoContent();
            });
        })
        .WithTags("Reactions").WithName("addReaction").WithSummary("Поставить реакцию")
        .WithDescription("Идемпотентно: (message, user, emoji) не дублируется. Участник чата; " +
                         "409 message_deleted — на удалённое. Событие reaction_added.")
        .Produces(204);

        app.MapDelete("/messages/{message_id:long}/reactions/{emoji}", async (HttpContext ctx, long message_id, string emoji) =>
        {
            var me = Accounts.RequireUser(ctx);
            var em = Uri.UnescapeDataString(emoji ?? "").Trim();
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = MessageRepository.ById(c, message_id);
                ChatRepository.RequireMember(c, m.Chat_Id, me.Id);
                var removed = c.Execute(
                    "DELETE FROM message_reactions WHERE message_id=@m AND user_id=@u AND emoji=@e",
                    new { m = message_id, u = me.Id, e = em });
                if (removed == 0) return Errors.Err(404, "такой реакции не было");
                await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                    ChatRepository.MemberIds(c, m.Chat_Id), me.Id,
                    new WsEvent("reaction_removed", m.Chat_Id) { MessageId = message_id, UserId = me.Id, Emoji = em }));
                return Results.NoContent();
            });
        })
        .WithTags("Reactions").WithName("removeReaction").WithSummary("Убрать свою реакцию")
        .WithDescription("Удаляет только СВОЮ реакцию с данной emoji. 404 — реакции не было. Событие reaction_removed.")
        .Produces(204);

    }
}
