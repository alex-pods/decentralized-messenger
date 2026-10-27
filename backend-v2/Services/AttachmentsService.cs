using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Infrastructure;
using Messenger.Api.Models;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Services;

public sealed record AttachmentDownload(string Path, string FileName);

public interface IAttachmentsService
{
    Task<List<AttachmentResponse>> Upload(long messageId, long userId, IReadOnlyList<IFormFile> formFiles,
        CancellationToken cancellationToken);
    Task<AttachmentDownload> GetDownload(long attachmentId, long userId);
}

public sealed class AttachmentsService(
    IAttachmentsRepository attachments,
    IMessagesRepository messages,
    IChatMembershipRepository memberships,
    IFileStorage files,
    ILogger<AttachmentsService> logger) : IAttachmentsService
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxFiles = 10;

    public async Task<List<AttachmentResponse>> Upload(
        long messageId, long userId, IReadOnlyList<IFormFile> formFiles, CancellationToken cancellationToken)
    {
        if (formFiles.Count == 0) throw AppException.BadRequest("file is required");
        if (formFiles.Count > MaxFiles) throw AppException.BadRequest("up to 10 files may be uploaded at once");

        var message = await messages.GetByIdAsync(messageId, cancellationToken)
            ?? throw AppException.NotFound("message not found");
        await RequireMember(message.ChatId, userId);
        if (message.SenderId != userId)
            throw AppException.Forbidden("only the message author may attach files");
        if (message.ContentState == MessageContentState.Deleted)
            throw AppException.Conflict("message_deleted", "message has been deleted");
        if (message.ContentState == MessageContentState.NotStored)
            throw AppException.Conflict("content_not_stored", "message content is not stored");

        foreach (var file in formFiles)
            if (file.Length > MaxFileBytes)
                throw AppException.PayloadTooLarge($"file {file.FileName} exceeds 10 MB");

        var uploadedPaths = new List<string>();
        var rows = new List<Attachment>();
        try
        {
            foreach (var file in formFiles)
            {
                var path = $"attachments/{Guid.NewGuid():N}.bin";
                await using (var source = file.OpenReadStream())
                    await files.Write(path, source, cancellationToken);
                uploadedPaths.Add(path);

                var fileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
                rows.Add(new Attachment(0, messageId,
                    string.IsNullOrWhiteSpace(fileName) ? "file" : fileName,
                    string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                    file.Length, path));
            }

            await attachments.InsertMany(rows);
        }
        catch
        {
            await CleanupFiles(uploadedPaths);
            throw;
        }

        var saved = await attachments.GetByMessageIds([messageId]);
        return saved.Where(a => uploadedPaths.Contains(a.StoragePath))
            .Select(AttachmentResponse.From).ToList();
    }

    public async Task<AttachmentDownload> GetDownload(long attachmentId, long userId)
    {
        var attachment = await attachments.GetById(attachmentId)
            ?? throw AppException.NotFound("attachment not found");
        var message = await messages.GetByIdAsync(attachment.MessageId)
            ?? throw AppException.NotFound("message not found");
        await RequireMember(message.ChatId, userId);
        if (!files.Exists(attachment.StoragePath))
            throw AppException.NotFound("attachment file not found");
        return new AttachmentDownload(files.GetAbsolutePath(attachment.StoragePath), attachment.FileName);
    }

    private async Task RequireMember(long chatId, long userId)
    {
        if (!await memberships.ChatExists(chatId))
            throw AppException.NotFound("chat not found");
        if (!await memberships.IsMember(chatId, userId))
            throw AppException.Forbidden("user is not a chat member");
    }

    private async Task CleanupFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                await files.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Failed to clean up attachment file {AttachmentPath}", path);
            }
        }
    }
}
