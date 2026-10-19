using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Endpoints;
using MessengerBackend.Hubs;
using Microsoft.AspNetCore.Mvc;

namespace MessengerBackend;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        var hub = app.Services.GetRequiredService<Hub>();
        var filesRoot = AppCtx.FilesRootFromEnv();
        app.MapPost("/messages/{message_id:long}/attachments", async (HttpContext ctx, long message_id) =>
        {
            var me = Accounts.RequireUser(ctx);
            // Reject unauthorized uploads before buffering multipart data. Recheck before writing.
            Db.Read(c => Files.RequireAttachableMessage(c, message_id, me.Id));
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                return Errors.Err(400, "нужен multipart/form-data с полем file");
            }
            if (form.Files.Count == 0) return Errors.Err(400, "поле file обязательно (можно несколько)");
            if (form.Files.Count > 10) return Errors.Err(400, "не более 10 файлов за запрос");
            foreach (var f in form.Files)
                if (f.Length > Files.MaxAttachmentBytes)
                    return Errors.Err(413, $"файл {f.FileName} слишком большой: {f.Length} байт (макс. 10 МБ)");
            return await Db.WriteAsync<IResult>(async c =>
            {
                var m = Files.RequireAttachableMessage(c, message_id, me.Id);
                var results = new List<AttachmentOut>();
                var written = new List<string>();
                try
                {
                    await Infrastructure.SchemaMigrator.TransactionAsync(c, async () =>
                    {
                        foreach (var f in form.Files)
                        {
                            var (_, dto, path) = await Files.SaveAttachment(c, filesRoot, m, f);
                            written.Add(path);
                            results.Add(dto);
                        }
                        return 0;
                    });
                }
                catch
                {
                    foreach (var path in written)
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { app.Logger.LogError(ex, "Failed to clean up rolled back attachment"); }
                    }
                    throw;
                }
                foreach (var att in results)
                    await ChatEndpoints.SafePublish(() => hub.PublishToChat(m.Chat_Id,
                        ChatRepository.MemberIds(c, m.Chat_Id), me.Id,
                        new WsEvent("attachment_added", m.Chat_Id) { MessageId = m.Id, Attachment = att }));
                return Results.Json(results, statusCode: 201);
            });
        })
        .WithTags("Attachments").WithName("uploadAttachments").WithSummary("Вложить файлы в своё сообщение")
        .WithDescription("multipart/form-data, поле file (до 10 файлов за запрос, каждый до 10 МБ → 413). " +
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
                var msg = MessageRepository.ById(c, att.Message_Id);
                ChatRepository.RequireMember(c, msg.Chat_Id, me.Id);
                if (!File.Exists(att.Path_To_File)) return Errors.Err(404, "файл отсутствует на диске");
                ctx.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(Infrastructure.StorageEncryption.ReadFile(att.Path_To_File), "application/octet-stream", att.File_Name);
            });
        })
        .WithTags("Attachments").WithName("downloadAttachment").WithSummary("Скачать вложение")
        .WithDescription("Только участник чата (403). Бинарный ответ application/octet-stream, " +
                         "Content-Disposition: attachment и X-Content-Type-Options: nosniff. " +
                         "Вложения удалённого для всех сообщения → 404.")
        .Produces(200);
    }
}
