using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Services;

namespace Messenger.Api.Endpoints;

public static class AttachmentsEndpoints
{
    public static void MapAttachmentsEndpoints(this WebApplication app)
    {
        app.MapPost("/messages/{message_id:long}/attachments",
            async (long message_id, HttpContext context, IAttachmentsService service) =>
            {
                IFormCollection form;
                try
                {
                    form = await context.Request.ReadFormAsync(context.RequestAborted);
                }
                catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
                {
                    throw AppException.BadRequest("multipart/form-data with file fields is required");
                }
                var files = form.Files.Where(file => file.Name == "file").ToList();
                var response = await service.Upload(message_id, CurrentUser.GetId(context), files,
                    context.RequestAborted);
                return Results.Json(response, statusCode: StatusCodes.Status201Created);
            })
            .WithName("UploadMessageAttachments")
            .WithSummary("Вложить файлы в сообщение")
            .DisableAntiforgery()
            .Produces<List<AttachmentResponse>>(StatusCodes.Status201Created);

        app.MapGet("/attachments/{attachment_id:long}",
            async (long attachment_id, HttpContext context, IAttachmentsService service) =>
            {
                var download = await service.GetDownload(attachment_id, CurrentUser.GetId(context));
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(download.Path, "application/octet-stream", download.FileName);
            })
            .WithName("DownloadAttachment")
            .WithSummary("Скачать вложение");
    }
}
