using static MessengerBackend.Endpoints.ChatEndpoints;
using System.Data;
using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend.Endpoints;



public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();
        // ---------- участники ----------

        app.MapGet("/chats/{chat_id:long}/members", (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read(c =>
            {
                var chat = ChatRepository.ById(c, chat_id);
                ChatRepository.RequireMember(c, chat.Id, me.Id);
                return Results.Json(ChatRepository.MembersOf(c, chat.Id)
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
                var chat = ChatRepository.ById(c, chat_id);
                if (chat.Type != ChatRepository.GroupType)
                    return Errors.Err(400, "участников можно добавлять только в группу");
                ChatRepository.RequireManager(c, chat.Id, me.Id);
                var target = ProfileRepository.ById(c, r.UserId);
                if (ChatRepository.RoleOf(c, chat.Id, target.Id) != "")
                    return Results.NoContent(); // уже участник — идемпотентно
                c.Execute("INSERT INTO chat_members(chat_id, user_id, role, joined_at) VALUES(@c, @u, 'member', @j)",
                    new { c = chat.Id, u = target.Id, j = Util.NowIso() });
                await SafePublish(() => hub.PublishToChat(chat.Id, ChatRepository.MemberIds(c, chat.Id), me.Id,
                    new WsEvent("member_added", chat.Id) { UserId = target.Id, User = new UserCard(target.Id, target.Tag, target.Display_Name) }));
                await SafePublish(() => hub.PublishToUsers([target.Id],
                    new WsEvent("chat_created", chat.Id)
                    {
                        AlwaysDeliver = true,
                        Chat = ChatInfo(c, chat, target.Id, ChatRepository.MembersOfAll(c, [chat.Id]), null)
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
                var chat = ChatRepository.ById(c, chat_id);
                if (chat.Type != ChatRepository.GroupType)
                    return Errors.Err(400, "из direct-чата участников не удаляют");
                ChatRepository.RequireManager(c, chat.Id, me.Id);
                var targetRole = ChatRepository.RoleOf(c, chat.Id, user_id);
                if (targetRole == "") return Errors.Err(404, "пользователь не в группе");
                if (targetRole == "owner") return Errors.Err(403, "владельца исключить нельзя");
                if (targetRole == "admin" && ChatRepository.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "снять admin может только owner");
                if (user_id == me.Id) return Errors.Err(400, "для выхода используйте POST /chats/{id}/leave");
                c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u",
                    new { c = chat.Id, u = user_id });
                await SafePublish(() => hub.PublishToChat(chat.Id, ChatRepository.MemberIds(c, chat.Id).Append(user_id), me.Id,
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
                var chat = ChatRepository.ById(c, chat_id);
                ChatRepository.RequireMember(c, chat.Id, me.Id);
                if (chat.Type != ChatRepository.GroupType)
                    return Errors.Err(400, "выйти из direct-чата нельзя");
                var myRole = ChatRepository.RoleOf(c, chat.Id, me.Id);
                var others = c.Query<long>(
                    "SELECT user_id FROM chat_members WHERE chat_id=@c AND user_id<>@u",
                    new { c = chat.Id, u = me.Id }).ToList();
                if (myRole == "owner" && others.Count > 0)
                    return Errors.Err(409, "сначала передайте владение (POST /chats/{id}/owner)",
                        "owner_transfer_required");
                var closing = myRole == "owner" && others.Count == 0;
                var files = closing ? c.Query<string>(
                    "SELECT a.path_to_file FROM attachments a JOIN messages m ON m.id=a.message_id WHERE m.chat_id=@c",
                    new { c = chat.Id }).ToList() : [];
                Infrastructure.SchemaMigrator.Transaction(c, () =>
                {
                    c.Execute("DELETE FROM chat_members WHERE chat_id=@c AND user_id=@u", new { c = chat.Id, u = me.Id });
                    if (closing) c.Execute("DELETE FROM chats WHERE id=@c", new { c = chat.Id });
                    return 0;
                });
                if (closing)
                {
                    foreach (var path in files)
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { app.Logger.LogError(ex, "Failed to delete closed group's attachment"); }
                    }
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
                var chat = ChatRepository.ById(c, chat_id);
                if (chat.Type != ChatRepository.GroupType) return Errors.Err(400, "владение есть только у групп");
                if (ChatRepository.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "передать владение может только текущий owner");
                var target = ProfileRepository.ById(c, r.UserId);
                if (ChatRepository.RoleOf(c, chat.Id, target.Id) == "")
                    return Errors.Err(404, "новый владелец должен быть участником группы");
                if (target.Id == me.Id) return Errors.Err(400, "вы уже владелец");
                Infrastructure.SchemaMigrator.Transaction(c, () =>
                {
                    c.Execute("UPDATE chat_members SET role='admin' WHERE chat_id=@c AND user_id=@me",
                        new { c = chat.Id, me = me.Id });
                    c.Execute("UPDATE chat_members SET role='owner' WHERE chat_id=@c AND user_id=@u",
                        new { c = chat.Id, u = target.Id });
                    return 0;
                });
                await SafePublish(() => hub.PublishToUsers(ChatRepository.MemberIds(c, chat.Id),
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
                var chat = ChatRepository.ById(c, chat_id);
                if (chat.Type != ChatRepository.GroupType) return Errors.Err(400, "роли есть только в группах");
                if (ChatRepository.RoleOf(c, chat.Id, me.Id) != "owner")
                    return Errors.Err(403, "роли меняет только owner");
                var targetRole = ChatRepository.RoleOf(c, chat.Id, user_id);
                if (targetRole == "") return Errors.Err(404, "пользователь не в группе");
                if (targetRole == "owner")
                    return Errors.Err(403, "роль owner не меняется; передайте владение");
                c.Execute("UPDATE chat_members SET role=@r WHERE chat_id=@c AND user_id=@u",
                    new { r = role, c = chat.Id, u = user_id });
                await SafePublish(() => hub.PublishToUsers(ChatRepository.MemberIds(c, chat.Id),
                    new WsEvent("member_role_changed", chat.Id) { AlwaysDeliver = true, UserId = user_id, Role = role }));
                return Results.NoContent();
            });
        })
        .WithTags("Members").WithName("setMemberRole").WithSummary("Назначить/снять admin")
        .WithDescription("Только owner; цель — участник, не owner. role: admin | member. " +
                         "Событие member_role_changed (без follow).")
        .Produces(204);
    }
}
