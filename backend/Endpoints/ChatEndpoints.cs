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
                var membersByChat = Chats.MembersOfAll(c, chats.Select(ch => ch.Id).ToList());
                var lastById = Messages.LastVisibleMessagesOf(c, chats.Select(ch => ch.Id).ToList(), me.Id);
                var lastMessages = lastById.Values.ToList();
                var dtoByChat = Messages.ToDtoList(c, lastMessages,
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
                var other = ProfileEndpoints2.UserById(c, r.AnotherUserId.Value);
                var (chatId, created) = Chats.GetOrCreateDirect(c, me.Id, other.Id);
                if (!created)
                {
                    c.Execute("DELETE FROM chat_hidden WHERE chat_id=@c AND user_id=@u",
                        new { c = chatId, u = me.Id }); // повторное открытие снимает скрытие
                    return Results.Json(new ChatIdOut(chatId));
                }
                await SafePublish(() => hub.PublishToUsers(new[] { me.Id, other.Id },
                    new WsEvent("chat_created", chatId)
                    {
                        AlwaysDeliver = true,
                        Chat = ChatInfo(c, Chats.ById(c, chatId), me.Id,
                            Chats.MembersOfAll(c, [chatId]), null)
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
                foreach (var id in ids) ProfileEndpoints2.UserById(c, id);
                var chatId = Chats.CreateGroup(c, me.Id, title, ids);
                await SafePublish(() => hub.PublishToUsers(Chats.MemberIds(c, chatId),
                    new WsEvent("chat_created", chatId)
                    {
                        AlwaysDeliver = true,
                        Chat = ChatInfo(c, Chats.ById(c, chatId), me.Id, Chats.MembersOfAll(c, [chatId]), null)
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
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                return Results.Json(ChatInfo(c, chat, me.Id, Chats.MembersOfAll(c, [chat.Id]), null));
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
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
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

        // ---------- участники ----------

        app.MapGet("/chats/{chat_id:long}/members", (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read(c =>
            {
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                return Results.Json(Chats.MembersOf(c, chat.Id)
                    .Select(m => new MemberOut(m.User_Id, m.Tag!, m.Display_Name!, m.Role, m.Joined_At)).ToList());
            });
        })
        .WithTags("Members").WithName("getMembers").WithSummary("Участники чата")
        .WithDescription("[{id, tag, display_name, role: member|admin|owner, joined_at}]. Только участник.")
        .Produces<List<MemberOut>>(200);

        app.MapPost("/chats/{chat_id:long}/members", async (HttpContext ctx, long chat_id, [FromBody] AddMemberIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            if (r.UserId <= 0) return Errors.Err(400, "user_id должен быть положительным");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                if (chat.Type != Chats.GroupType)
                    return Errors.Err(400, "участников можно добавлять только в группу");
                Chats.RequireManager(c, chat.Id, me.Id);
                var target = ProfileEndpoints2.UserById(c, r.UserId);
                if (Chats.RoleOf(c, chat.Id, target.Id) != "")
                    return Results.NoContent(); // уже участник — идемпотентно
                c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
                    new { c = chat.Id, u = target.Id, j = Util.NowIso() });
                await SafePublish(() => hub.PublishToChat(chat.Id, Chats.MemberIds(c, chat.Id), me.Id,
                    new WsEvent("member_added", chat.Id) { UserId = target.Id, User = new UserCard(target.Id, target.Tag, target.Display_Name) }));
                await SafePublish(() => hub.PublishToUsers([target.Id],
                    new WsEvent("chat_created", chat.Id)
                    {
                        AlwaysDeliver = true,
                        Chat = ChatInfo(c, chat, target.Id, Chats.MembersOfAll(c, [chat.Id]), null)
                    }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("addMember").WithSummary("Добавить участника в группу")
        .WithDescription("Owner/admin, только группа; повторное добавление не создаёт дубль (204). " +
                         "События: member_added участникам (follow), chat_created добавленному (без follow).")
        .Produces(204);

        app.MapDelete("/chats/{chat_id:long}/members/{user_id:long}", async (HttpContext ctx, long chat_id, long user_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                if (chat.Type != Chats.GroupType)
                    return Errors.Err(400, "из direct-чата участников не удаляют");
                Chats.RequireManager(c, chat.Id, me.Id);
                var targetRole = Chats.RoleOf(c, chat.Id, user_id);
                if (targetRole == "") return Errors.Err(404, "пользователь не в группе");
                if (targetRole == "owner") return Errors.Err(403, "владельца исключить нельзя");
                if (targetRole == "admin" && Chats.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "снять admin может только owner");
                if (user_id == me.Id) return Errors.Err(400, "для выхода используйте POST /chats/{id}/leave");
                c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u",
                    new { c = chat.Id, u = user_id });
                await SafePublish(() => hub.PublishToChat(chat.Id, Chats.MemberIds(c, chat.Id).Append(user_id), me.Id,
                    new WsEvent("member_removed", chat.Id) { UserId = user_id }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("removeMember").WithSummary("Исключить участника из группы")
        .WithDescription("Owner/admin (admin снимает только member; owner — и admin; owner не исключаем). " +
                         "Исключённый сразу теряет доступ по HTTP и WS. Событие member_removed (включая исключённого).")
        .Produces(204);

        app.MapPost("/chats/{chat_id:long}/leave", async (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                if (chat.Type != Chats.GroupType)
                    return Errors.Err(400, "выйти из direct-чата нельзя");
                var myRole = Chats.RoleOf(c, chat.Id, me.Id);
                var others = c.Query<long>(
                    "SELECT user_id FROM chat_members WHERE chat_id=@c AND user_id<>@u",
                    new { c = chat.Id, u = me.Id }).ToList();
                if (myRole == "owner" && others.Count > 0)
                    return Errors.Err(409, "сначала передайте владение (POST /chats/{id}/owner)",
                        "owner_transfer_required");
                c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u", new { c = chat.Id, u = me.Id });
                if (myRole == "owner" && others.Count == 0)
                {
                    c.Execute("DELETE FROM chats WHERE id=@c", new { c = chat.Id }); // последний вышел — группа закрывается
                    await SafePublish(() => hub.PublishToUsers([me.Id],
                        new WsEvent("chat_deleted", chat.Id) { AlwaysDeliver = true }));
                    return Results.NoContent();
                }
                await SafePublish(() => hub.PublishToChat(chat.Id, others.Append(me.Id), me.Id,
                    new WsEvent("member_left", chat.Id) { UserId = me.Id }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("leaveChat").WithSummary("Выйти из группы")
        .WithDescription("Владелец не может выйти, пока есть другие участники: 409 owner_transfer_required. " +
                         "Выход последнего закрывает группу (каскадно удаляются сообщения/вложения). " +
                         "Direct покинуть нельзя (400). Событие member_left.")
        .Produces(204);

        app.MapPost("/chats/{chat_id:long}/owner", async (HttpContext ctx, long chat_id, [FromBody] TransferOwnerIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            if (r.UserId <= 0) return Errors.Err(400, "user_id должен быть положительным");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                if (chat.Type != Chats.GroupType) return Errors.Err(400, "владение есть только у групп");
                if (Chats.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "передать владение может только текущий owner");
                var target = ProfileEndpoints2.UserById(c, r.UserId);
                if (Chats.RoleOf(c, chat.Id, target.Id) == "")
                    return Errors.Err(404, "новый владелец должен быть участником группы");
                if (target.Id == me.Id) return Errors.Err(400, "вы уже владелец");
                c.Execute("UPDATE chat_members SET role='admin' WHERE chat_id=@c AND user_id=@me",
                    new { c = chat.Id, me = me.Id });
                c.Execute("UPDATE chat_members SET role='owner' WHERE chat_id=@c AND user_id=@u",
                    new { c = chat.Id, u = target.Id });
                await SafePublish(() => hub.PublishToUsers(Chats.MemberIds(c, chat.Id),
                    new WsEvent("owner_changed", chat.Id) { AlwaysDeliver = true, UserId = target.Id }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("transferOwner").WithSummary("Передать владение группой")
        .WithDescription("Только owner и только действующему участнику; атомарно: прежний owner становится admin, " +
                         "новый — owner (в группе всегда ровно один owner). Событие owner_changed (без follow).")
        .Produces(204);

        app.MapPatch("/chats/{chat_id:long}/members/{user_id:long}", async (HttpContext ctx, long chat_id, long user_id,
            [FromBody] SetRoleIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var role = r.Role?.Trim();
            if (role is not ("admin" or "member"))
                return Errors.Err(400, "role должен быть 'admin' или 'member'");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                if (chat.Type != Chats.GroupType) return Errors.Err(400, "роли есть только в группах");
                if (Chats.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "роли меняет только owner");
                var targetRole = Chats.RoleOf(c, chat.Id, user_id);
                if (targetRole == "") return Errors.Err(404, "пользователь не в группе");
                if (targetRole == "owner")
                    return Errors.Err(403, "роль owner не меняется; передайте владение");
                c.Execute("UPDATE chat_members SET role=@r WHERE chat_id=@c AND user_id=@u",
                    new { r = role, c = chat.Id, u = user_id });
                await SafePublish(() => hub.PublishToUsers(Chats.MemberIds(c, chat.Id),
                    new WsEvent("member_role_changed", chat.Id) { AlwaysDeliver = true, UserId = user_id, Role = role }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("setMemberRole").WithSummary("Назначить/снять admin")
        .WithDescription("Только owner; цель — участник, не owner. role: admin | member. " +
                         "Событие member_role_changed (без follow).")
        .Produces(204);
    }

    /// <summary>Сбой рассылки события не должен превращать уже зафиксированное изменение в ошибку запроса.</summary>
    internal static Task SafePublish(Func<Task> publish)
    {
        try { return publish(); }
        catch (Exception) { return Task.CompletedTask; }
    }

    internal static object ChatInfo(IDbConnection c, ChatRow chat, long viewerId,
        Dictionary<long, List<MemberRow>> membersByChat, MessageOut? lastMessage)
    {
        if (chat.Type == Chats.DirectType)
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

/// <summary>Общие запросы пользователей для соседних модулей эндпоинтов.</summary>
internal static class ProfileEndpoints2
{
    public static UserRow UserById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<UserRow>("SELECT * FROM users WHERE id=@id", new { id })
        ?? throw new HttpException(404, "пользователь не найден");
}
