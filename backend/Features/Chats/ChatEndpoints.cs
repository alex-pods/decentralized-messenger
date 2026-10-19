using System.Data;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();

        app.MapGet("/chats", (HttpContext ctx) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read(c =>
            {
                var chats = c.Query<ChatRow>(
                    """
                    SELECT ch.* FROM chats ch
                    JOIN chat_members m ON m.chat_id = ch.id
                    WHERE m.user_id=@u AND ch.id NOT IN (SELECT chat_id FROM chat_hidden WHERE user_id=@u)
                    ORDER BY ch.id DESC
                    """, new { u = me.Id }).ToList();
                var membersByChat = ChatRepository.MembersOfAll(c, chats.Select(ch => ch.Id).ToList());
                var lastById = MessageRepository.LastVisibleMessagesOf(c, chats.Select(ch => ch.Id).ToList(), me.Id);
                var lastMessages = lastById.Values.ToList();
                var dtoByChat = MessageRepository.ToDtoList(c, lastMessages,
                        lastMessages.Count > 0
                            ? membersByChat.SelectMany(kv => kv.Value).ToList()
                            : [])
                    .Select((dto, i) => (dto, lastMessages[i].Chat_Id))
                    .ToDictionary(t => t.Item2, t => t.dto);
                var list = chats.Select(ch =>
                    ChatInfo(c, ch, me.Id, membersByChat, dtoByChat.GetValueOrDefault(ch.Id))).ToList();
                return Results.Json(list);
            });
        })
        .WithTags("Chats").WithName("listChats").WithSummary("Мои чаты")
        .WithDescription("direct -> other_user; group -> title/created_by. last_message — последнее не скрытое " +
                         "«у себя» сообщение. Скрытые через DELETE /chats/{id} чаты не возвращаются. " +
                         "Прямых N+1-запросов нет: участники и последние сообщения грузятся пакетно.")
        .Produces(200);

        app.MapPost("/chats", async (HttpContext ctx, [FromBody] CreateChatIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            if (r.AnotherUserId is not > 0)
                return Errors.Err(400, "another_user_id должен быть положительным id пользователя");
            if (r.AnotherUserId == me.Id)
                return Errors.Err(400, "нельзя создать личный чат с самим собой");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var other = ProfileRepository.ById(c, r.AnotherUserId.Value);
                var (chatId, created) = ChatRepository.GetOrCreateDirect(c, me.Id, other.Id);
                if (!created)
                {
                    c.Execute("DELETE FROM chat_hidden WHERE chat_id=@c AND user_id=@u",
                        new { c = chatId, u = me.Id }); // повторное открытие снимает скрытие
                    return Results.Json(new ChatIdOut(chatId));
                }
                foreach (var recipient in new[] { me.Id, other.Id })
                    await SafePublish(() => hub.PublishToUsers([recipient],
                        new WsEvent("chat_created", chatId)
                        {
                            AlwaysDeliver = true,
                            Chat = ChatInfo(c, ChatRepository.ById(c, chatId), recipient,
                                ChatRepository.MembersOfAll(c, [chatId]), null)
                        }));
                return Results.Json(new ChatIdOut(chatId), statusCode: 201);
            });
        })
        .WithTags("Chats").WithName("createDirectChat").WithSummary("Личный чат (единственный на пару)")
        .WithDescription("201 — создан; 200 — чат этой пары уже существует, возвращён его chat_id " +
                         "(единственность гарантирует UNIQUE(user1,user2) в direct_chats при user1<user2; " +
                         "одновременное создание с двух сторон даёт один чат). Повтор снимает скрытие у вызывающего. " +
                         "Событие WS chat_created обоим (без follow).")
        .Produces<ChatIdOut>(201).Produces<ChatIdOut>(200);

        app.MapPost("/chats/group", async (HttpContext ctx, [FromBody] CreateGroupIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var title = r.Title?.Trim() ?? "";
            if (title.Length is < 1 or > 64) return Errors.Err(400, "title должен быть 1-64 символа");
            var ids = (r.MemberIds ?? []).Distinct().Where(id => id > 0 && id != me.Id).ToList();
            return await Db.WriteAsync<IResult>(async c =>
            {
                foreach (var id in ids) ProfileRepository.ById(c, id);
                var chatId = ChatRepository.CreateGroup(c, me.Id, title, ids);
                await SafePublish(() => hub.PublishToUsers(ChatRepository.MemberIds(c, chatId),
                    new WsEvent("chat_created", chatId)
                    {
                        AlwaysDeliver = true,
                        Chat = ChatInfo(c, ChatRepository.ById(c, chatId), me.Id, ChatRepository.MembersOfAll(c, [chatId]), null)
                    }));
                return Results.Json(new ChatIdOut(chatId), statusCode: 201);
            });
        })
        .WithTags("Chats").WithName("createGroupChat").WithSummary("Создать группу")
        .WithDescription("Каждый вызов создаёт НОВУЮ группу, даже с тем же составом; создатель — owner. " +
                         "member_ids дедуплицируются; несуществующий user_id → 404. Событие chat_created (без follow).")
        .Produces<ChatIdOut>(201);

        app.MapGet("/chats/{chat_id:long}", (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read(c =>
            {
                var chat = ChatRepository.ById(c, chat_id);
                ChatRepository.RequireMember(c, chat.Id, me.Id);
                return Results.Json(ChatInfo(c, chat, me.Id, ChatRepository.MembersOfAll(c, [chat.Id]), null));
            });
        })
        .WithTags("Chats").WithName("getChat").WithSummary("Карточка чата")
        .WithDescription("direct -> other_user; group -> title/created_by/created_at. Только участник (403).")
        .Produces(200);

        app.MapDelete("/chats/{chat_id:long}", async (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = ChatRepository.ById(c, chat_id);
                ChatRepository.RequireMember(c, chat.Id, me.Id);
                c.Execute("INSERT OR IGNORE INTO chat_hidden(chat_id, user_id) VALUES(@c, @u)",
                    new { c = chat.Id, u = me.Id });
                await SafePublish(() => hub.PublishToUsers([me.Id],
                    new WsEvent("chat_hidden", chat.Id) { AlwaysDeliver = true }));
                return Results.NoContent();
            });
        })
        .WithTags("Chats").WithName("hideChat").WithSummary("Скрыть чат у себя")
        .WithDescription("Скрывает чат только у вызывающего (chat_hidden); участники, сообщения и вложения " +
                         "не затрагиваются. НЕ выход из группы и НЕ удаление группы. Для direct повторный " +
                         "POST /chats возвращает тот же chat_id и снимает скрытие. Идемпотентно.")
        .Produces(204);

    }

    /// <summary>Сбой рассылки события не должен превращать уже зафиксированное изменение в ошибку запроса.</summary>
    internal static async Task SafePublish(Func<Task> publish)
    {
        try { await publish(); }
        catch (Exception) { /* State is committed; delivery failure must not change the HTTP outcome. */ }
    }

    internal static object ChatInfo(IDbConnection c, ChatRow chat, long viewerId,
        Dictionary<long, List<MemberRow>> membersByChat, MessageOut? lastMessage)
    {
        if (chat.Type == ChatRepository.DirectType)
        {
            var other = membersByChat.GetValueOrDefault(chat.Id)?
                .FirstOrDefault(m => m.User_Id != viewerId);
            var otherCard = other is not null
                ? new UserCard(other.User_Id, other.Tag!, other.Display_Name!)
                : null;
            return new DirectChatOut(chat.Id, "direct", otherCard!, chat.Created_At, chat.Created_By, lastMessage);
        }
        return new GroupChatOut(chat.Id, "group", chat.Title ?? "", chat.Created_By, chat.Created_At, lastMessage);
    }
}
