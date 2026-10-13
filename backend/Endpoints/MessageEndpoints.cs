using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Endpoints;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();
        var filesRoot = AppCtx.FilesRootFromEnv();

        // ---------- история ----------

        app.MapGet("/chats/{chat_id:long}/messages", (HttpContext ctx, long chat_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            var (limit, before, after, err) = Paging.Parse(ctx);
            if (err is not null) return err;
            return Db.Read<IResult>(c =>
            {
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                // after_id — синхронизация после переподключения: сообщения НОВЕЕ курсора, по возрастанию;
                // без after_id — обычная постраничная выдача от новых к старым (внутри страницы по возрастанию)
                var rows = (after is null
                        ? c.Query<MessageRow>(
                            """
                            SELECT * FROM messages WHERE chat_id=@c
                               AND (@b IS NULL OR id < @b)
                               AND id NOT IN (SELECT message_id FROM message_hidden WHERE user_id=@u)
                               ORDER BY id DESC LIMIT @l
                            """, new { c = chat.Id, b = before, u = me.Id, l = limit ?? 50 }).Reverse().ToList()
                        : c.Query<MessageRow>(
                            """
                            SELECT * FROM messages WHERE chat_id=@c
                               AND id > @a
                               AND (@b IS NULL OR id < @b)
                               AND id NOT IN (SELECT message_id FROM message_hidden WHERE user_id=@u)
                               ORDER BY id ASC LIMIT @l
                            """, new { c = chat.Id, a = after, b = before, u = me.Id, l = limit ?? 50 }).ToList());
                return Results.Json(Messages.ToDtoList(c, rows, Chats.MembersOf(c, chat.Id)));
            });
        })
        .WithTags("Messages").WithName("getHistory").WithSummary("История сообщений")
        .WithDescription("Постраничная выдача: до limit (1..100, default 50) сообщений с id СТРОГО меньше before_id, " +
                         "внутри страницы от старых к новым; без before_id — начиная с самых последних. " +
                         "Некорректные limit/before_id → 400. Удалённые для всех — заглушки (content_state=deleted, " +
                         "text=null, без вложений/реакций). Скрытые «у себя» не возвращаются. " +
                         "read_at — агрегат: прочитано всеми участниками, кроме отправителя; " +
                         "delivered_at — доставлено хотя бы одному онлайн-получателю. " +
                         "not_stored встречается только в legacy-записях (текст утрачен).")
        .Produces<List<MessageOut>>(200);

        // ---------- отправка ----------

        app.MapPost("/chats/{chat_id:long}/messages", async (HttpContext ctx, long chat_id, [FromBody] SendMessageIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var text = r.Text ?? "";
            if (text.Trim().Length < 1) return Errors.Err(400, "text не должен быть пустым");
            if (text.Length > Messages.MaxTextLength)
                return Errors.Err(400, $"text слишком длинный (макс. {Messages.MaxTextLength})");
            if (r.ReplyToId is < 1) return Errors.Err(400, "reply_to_id должен быть положительным id сообщения");
            if (r.ClientRef is { Length: > 64 })
                return Errors.Err(400, "client_ref слишком длинный (макс. 64)");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                if (r.ReplyToId is { } rid) Messages.ValidateReply(c, chat.Id, rid);
                var (id, created, row) = Messages.Send(c, chat.Id, me.Id, text, r.ReplyToId, r.ClientRef);
                if (!created)
                    return Results.Json(new MessageIdOut(id)); // идемпотентный повтор
                // сообщение уже зафиксировано в БД: событие и delivered_at — пост-эффекты, сбой их не откатывает
                var members = Chats.MembersOf(c, chat.Id);
                try
                {
                    var delivered = await hub.PublishToChat(chat.Id,
                        members.Select(m => m.User_Id), me.Id,
                        new WsEvent("message", chat.Id) { Message = Messages.ToDto(c, row, members) });
                    if (delivered > 0)
                        c.Execute("UPDATE messages SET delivered_at=@d WHERE id=@i AND delivered_at IS NULL",
                            new { d = Util.NowIso(), i = id });
                }
                catch { /* доставка — не причина терять сохранённое сообщение */ }
                return Results.Json(new MessageIdOut(id), statusCode: 201);
            });
        })
        .WithTags("Messages").WithName("sendMessage").WithSummary("Отправить сообщение")
        .WithDescription("text 1..4096, reply_to_id? (сообщение ЭТОГО чата; ответ на заглушку удалённого разрешён), " +
                         "client_ref? — идемпотентный ключ ОТПРАВИТЕЛЯ (до 64 символов): повтор с тем же ключом " +
                         "и содержимым возвращает прежний message_id (200), с другим содержимым — 409 " +
                         "client_ref_conflict. Обычное сообщение всегда сохраняется на сервере. " +
                         "Событие message — подписанным (follow). 201 → message_id.")
        .Produces<MessageIdOut>(201).Produces<MessageIdOut>(200);

        // ---------- прочтения ----------

        app.MapPost("/chats/{chat_id:long}/read", async (HttpContext ctx, long chat_id, [FromBody] ReadIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            if (r.MessageId <= 0) return Errors.Err(400, "message_id должен быть положительным");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var chat = Chats.ById(c, chat_id);
                Chats.RequireMember(c, chat.Id, me.Id);
                var msg = c.QueryFirstOrDefault<MessageRow>(
                    "SELECT * FROM messages WHERE id=@i", new { i = r.MessageId });
                if (msg is null || msg.Chat_Id != chat.Id)
                    return Errors.Err(404, "message_id не найден в этом чате");
                var beforeCursor = c.ExecuteScalar<long?>(
                    "SELECT last_read_message_id FROM chat_members WHERE chat_id=@c AND user_id=@u",
                    new { c = chat.Id, u = me.Id });
                Messages.MarkRead(c, chat.Id, me.Id, r.MessageId);
                var effective = c.ExecuteScalar<long>(
                    "SELECT COALESCE(last_read_message_id, 0) FROM chat_members WHERE chat_id=@c AND user_id=@u",
                    new { c = chat.Id, u = me.Id });
                if (effective != (beforeCursor ?? 0)) // курсор монотонен: без движения события нет
                    await ChatEndpoints.SafePublish(() => hub.PublishToChat(chat.Id,
                        Chats.MemberIds(c, chat.Id), me.Id,
                        new WsEvent("read", chat.Id) { UserId = me.Id, LastReadMessageId = effective }));
                return Results.NoContent();
            });
        })
        .WithTags("Messages").WithName("markRead").WithSummary("Подтвердить прочтение")
        .WithDescription("Курсор прочтения по message_id: не уменьшается, закрывает и более ранние сообщения. " +
                         "GET истории ничего не помечает. Событие read — подписанным участникам.")
        .Produces(204);

        // ---------- правка / удаление ----------

        app.MapPatch("/messages/{message_id:long}", async (HttpContext ctx, long message_id, [FromBody] EditMessageIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var text = r.Text ?? "";
            if (text.Trim().Length < 1) return Errors.Err(400, "text не должен быть пустым");
            if (text.Length > Messages.MaxTextLength)
                return Errors.Err(400, $"text слишком длинный (макс. {Messages.MaxTextLength})");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = Messages.ById(c, message_id);
                Chats.RequireMember(c, m.Chat_Id, me.Id);
                if (m.Sender_Id != me.Id) return Errors.Err(403, "редактировать может только автор");
                if (m.Deleted_At is not null) return Errors.Err(409, "message_deleted", "message_deleted");
                if (m.Text.Length == 0) return Errors.Err(409, "content_not_stored", "content_not_stored");
                c.Execute("UPDATE messages SET text=@t, edited_at=@ea WHERE id=@i",
                    new { t = text, ea = Util.NowIso(), i = message_id });
                var upd = Messages.ById(c, message_id);
                await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                    Chats.MemberIds(c, m.Chat_Id), me.Id,
                    new WsEvent("message_edited", m.Chat_Id) { Message = Messages.ToDto(c, upd) }));
                return Results.NoContent();
            });
        })
        .WithTags("Messages").WithName("editMessage").WithSummary("Редактировать своё сообщение")
        .WithDescription("Только автор; меняются text и edited_at. 409 message_deleted — удалено; " +
                         "409 content_not_stored — legacy-запись без текста. Событие message_edited.")
        .Produces(204);

        app.MapDelete("/messages/{message_id:long}", async (HttpContext ctx, long message_id, [FromBody] DeleteMessageIn? r) =>
        {
            var me = Accounts.RequireUser(ctx);
            if (r?.ForEveryone is null)
                return Errors.Err(400, "for_everyone обязателен: true (для всех) или false (скрыть у себя)");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = Messages.ById(c, message_id);
                Chats.RequireMember(c, m.Chat_Id, me.Id);
                if (r.ForEveryone.Value)
                {
                    if (m.Sender_Id != me.Id && Chats.RoleOf(c, m.Chat_Id, me.Id) is not ("owner" or "admin"))
                        return Errors.Err(403, "удалить для всех может автор или owner/admin группы");
                    if (m.Deleted_At is not null) return Results.NoContent(); // идемпотентно
                    var files = Messages.DeleteForEveryone(c, message_id);
                    foreach (var f in files)
                        try { if (File.Exists(f)) File.Delete(f); } catch { /* фоновая уборка подберёт */ }
                    var upd = Messages.ById(c, message_id);
                    await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                        Chats.MemberIds(c, m.Chat_Id), me.Id,
                        new WsEvent("message_deleted", m.Chat_Id)
                        {
                            MessageId = message_id, Message = Messages.ToDto(c, upd), ForEveryone = true
                        }));
                    return Results.NoContent();
                }
                c.Execute("INSERT OR IGNORE INTO message_hidden(message_id, user_id) VALUES(@m, @u)",
                    new { m = message_id, u = me.Id });
                await ChatEndpoints.SafePublish(() => hub.PublishToUsers([me.Id],
                    new WsEvent("message_deleted", m.Chat_Id)
                    {
                        AlwaysDeliver = true, MessageId = message_id, ForEveryone = false
                    }));
                return Results.NoContent();
            });
        })
        .WithTags("Messages").WithName("deleteMessage").WithSummary("Удалить сообщение")
        .WithDescription("for_everyone ОБЯЗАТЕЛЕН. true — для всех (автор или owner/admin): deleted_at, " +
                         "вложения отзываются (GET вложения → 404, файлы удаляются), реакции снимаются, " +
                         "в истории заглушка; повтор идемпотентен. false — скрыть у себя: сообщение остаётся " +
                         "у остальных; событие только действующим сессиям этого пользователя. " +
                         "Изменения удалённого → 409 message_deleted.")
        .Produces(204);

        // ---------- реакции ----------

        app.MapPost("/messages/{message_id:long}/reactions", async (HttpContext ctx, long message_id, [FromBody] ReactionIn r) =>
        {
            var me = Accounts.RequireUser(ctx);
            var emoji = (r.Emoji ?? "").Trim();
            if (emoji.Length is < 1 or > 32) return Errors.Err(400, "emoji должна быть 1-32 символа");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = Messages.ById(c, message_id);
                Chats.RequireMember(c, m.Chat_Id, me.Id);
                if (m.Deleted_At is not null) return Errors.Err(409, "message_deleted", "message_deleted");
                c.Execute("INSERT OR IGNORE INTO message_reactions(message_id, user_id, emoji) VALUES(@m, @u, @e)",
                    new { m = message_id, u = me.Id, e = emoji });
                await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                    Chats.MemberIds(c, m.Chat_Id), me.Id,
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
                var m = Messages.ById(c, message_id);
                Chats.RequireMember(c, m.Chat_Id, me.Id);
                var removed = c.Execute(
                    "DELETE FROM message_reactions WHERE message_id=@m AND user_id=@u AND emoji=@e",
                    new { m = message_id, u = me.Id, e = em });
                if (removed == 0) return Errors.Err(404, "такой реакции не было");
                await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                    Chats.MemberIds(c, m.Chat_Id), me.Id,
                    new WsEvent("reaction_removed", m.Chat_Id) { MessageId = message_id, UserId = me.Id, Emoji = em }));
                return Results.NoContent();
            });
        })
        .WithTags("Reactions").WithName("removeReaction").WithSummary("Убрать свою реакцию")
        .WithDescription("Удаляет только СВОЮ реакцию с данной emoji. 404 — реакции не было. Событие reaction_removed.")
        .Produces(204);

        // ---------- вложения ----------

        app.MapPost("/messages/{message_id:long}/attachments", async (HttpContext ctx, long message_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (InvalidOperationException)
            {
                return Errors.Err(400, "нужен multipart/form-data с полем file");
            }
            if (form.Files.Count == 0) return Errors.Err(400, "поле file обязательно (можно несколько)");
            foreach (var f in form.Files)
                if (f.Length > Files.MaxAttachmentBytes)
                    return Errors.Err(413, $"файл {f.FileName} слишком большой: {f.Length} байт (макс. 10 МБ)");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = Messages.ById(c, message_id);
                Chats.RequireMember(c, m.Chat_Id, me.Id);
                if (m.Sender_Id != me.Id)
                    return Errors.Err(403, "вкладывать файлы может только автор сообщения");
                if (m.Deleted_At is not null) return Errors.Err(409, "message_deleted", "message_deleted");
                if (m.Text.Length == 0) return Errors.Err(409, "content_not_stored", "content_not_stored");
                var results = new List<AttachmentOut>();
                foreach (var f in form.Files)
                {
                    var (_, dto) = await Files.SaveAttachment(c, filesRoot, m, f);
                    results.Add(dto);
                }
                foreach (var att in results)
                    await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                        Chats.MemberIds(c, m.Chat_Id), me.Id,
                        new WsEvent("attachment_added", m.Chat_Id) { MessageId = m.Id, Attachment = att }));
                return Results.Json(results, statusCode: 201);
            });
        })
        .WithTags("Attachments").WithName("uploadAttachments").WithSummary("Вложить файлы в своё сообщение")
        .WithDescription("multipart/form-data, поле file (можно несколько, каждый до 10 МБ → 413). " +
                         "Только автор сохранённого сообщения: 409 message_deleted / 409 content_not_stored. " +
                         "201 → МАССИВ AttachmentOut. Удаление для всех отзывает вложения (GET → 404). " +
                         "Событие attachment_added на каждый файл.")
        .DisableAntiforgery()
        .Produces(201);

        app.MapGet("/attachments/{attachment_id:long}", (HttpContext ctx, long attachment_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            return Db.Read<IResult>(c =>
            {
                var att = Files.AttachmentById(c, attachment_id);
                var msg = Messages.ById(c, att.Message_Id);
                Chats.RequireMember(c, msg.Chat_Id, me.Id);
                if (!File.Exists(att.Path_To_File)) return Errors.Err(404, "файл отсутствует на диске");
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{att.File_Name}\"";
                ctx.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(att.Path_To_File, att.File_Type, att.File_Name);
            });
        })
        .WithTags("Attachments").WithName("downloadAttachment").WithSummary("Скачать вложение")
        .WithDescription("Только участник чата (403). Бинарный ответ с фактическим Content-Type, " +
                         "Content-Disposition: attachment и X-Content-Type-Options: nosniff. " +
                         "Вложения удалённого для всех сообщения → 404.")
        .Produces(200);
    }
}
