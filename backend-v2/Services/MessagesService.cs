using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Models;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public sealed record SentMessage(long Id, bool Created);

public interface IMessagesService
{
    Task<List<MessageResponse>> GetHistory(long userId, long chatId, long? beforeId, long? afterId, int? limit);
    Task<SentMessage> Send(long userId, long chatId, SendMessageRequest request);
    Task MarkRead(long userId, long chatId, long? messageId);
    Task Edit(long userId, long messageId, EditMessageRequest request);
    Task Delete(long userId, long messageId, DeleteMessageRequest? request);
}

public sealed class MessagesService(
    IMessagesRepository messages,
    IAttachmentsRepository attachments,
    IChatMembershipRepository memberships,
    IFileStorage files,
    ILogger<MessagesService> logger) : IMessagesService
{
    private const int MaxTextLength = 4096;

    public async Task<List<MessageResponse>> GetHistory(
        long userId, long chatId, long? beforeId, long? afterId, int? limit)
    {
        await RequireMember(chatId, userId);
        var pageSize = limit ?? 50;
        if (pageSize is < 1 or > 100) throw AppException.BadRequest("limit must be between 1 and 100");
        if (beforeId is <= 0 || afterId is <= 0)
            throw AppException.BadRequest("message cursors must be positive");

        var history = await messages.GetByChat(chatId, userId, beforeId, afterId, pageSize);
        return await ToResponses(history);
    }

    public async Task<SentMessage> Send(long userId, long chatId, SendMessageRequest request)
    {
        var text = request.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw AppException.BadRequest("text must not be empty");
        if (text.Length > MaxTextLength)
            throw AppException.BadRequest($"text must be at most {MaxTextLength} characters");
        if (request.ReplyToId is <= 0)
            throw AppException.BadRequest("reply_to_id must be positive");
        if (request.ClientRef is { Length: 0 or > 64 })
            throw AppException.BadRequest("client_ref must be 1-64 characters");

        await RequireMember(chatId, userId);
        if (request.ReplyToId is { } replyToId)
        {
            var reply = await messages.GetById(replyToId) ?? throw AppException.NotFound("reply message not found");
            if (reply.ChatId != chatId)
                throw AppException.BadRequest("reply_to_id must refer to a message in this chat");
        }

        var inserted = await messages.Insert(chatId, userId, text, request.ReplyToId, request.ClientRef);
        if (inserted.Existing is { } existing)
        {
            if (existing.ChatId != chatId || existing.ReplyToId != request.ReplyToId || existing.Text != text)
                throw AppException.Conflict("client_ref_conflict", "client_ref was already used with different content");
            return new SentMessage(inserted.Id, false);
        }
        return new SentMessage(inserted.Id, inserted.Created);
    }

    public async Task MarkRead(long userId, long chatId, long? messageId)
    {
        if (messageId is null or <= 0)
            throw AppException.BadRequest("message_id must be positive");
        await RequireMember(chatId, userId);
        var message = await messages.GetById(messageId.Value);
        if (message is null || message.ChatId != chatId)
            throw AppException.NotFound("message_id was not found in this chat");
        await messages.MarkRead(chatId, userId, messageId.Value);
    }

    public async Task Edit(long userId, long messageId, EditMessageRequest request)
    {
        var text = request.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            throw AppException.BadRequest("text must not be empty");
        if (text.Length > MaxTextLength)
            throw AppException.BadRequest($"text must be at most {MaxTextLength} characters");

        var message = await messages.GetById(messageId) ?? throw AppException.NotFound("message not found");
        await RequireMember(message.ChatId, userId);
        if (message.SenderId != userId)
            throw AppException.Forbidden("only the message author may edit it");
        if (message.ContentState == "deleted")
            throw AppException.Conflict("message_deleted", "message has been deleted");
        if (message.ContentState == "not_stored")
            throw AppException.Conflict("content_not_stored", "message content is not stored");
        await messages.UpdateText(messageId, text);
    }

    public async Task Delete(long userId, long messageId, DeleteMessageRequest? request)
    {
        if (request?.ForEveryone is null)
            throw AppException.BadRequest("for_everyone is required");
        var message = await messages.GetById(messageId) ?? throw AppException.NotFound("message not found");
        await RequireMember(message.ChatId, userId);

        if (!request.ForEveryone.Value)
        {
            await messages.HideForUser(messageId, userId);
            return;
        }

        if (message.SenderId != userId)
        {
            var role = await memberships.GetRole(message.ChatId, userId);
            if (role is not ("owner" or "admin"))
                throw AppException.Forbidden("only the author or a chat admin may delete for everyone");
        }
        if (message.ContentState == "deleted") return;

        var paths = await messages.DeleteForEveryone(messageId);
        foreach (var path in paths)
        {
            try
            {
                await files.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to remove deleted message attachment {AttachmentPath}", path);
            }
        }
    }

    private async Task<List<MessageResponse>> ToResponses(IReadOnlyCollection<Message> rows)
    {
        var relatedAttachments = await attachments.GetByMessageIds(rows.Select(message => message.Id).ToArray());
        var attachmentsByMessage = relatedAttachments
            .GroupBy(attachment => attachment.MessageId)
            .ToDictionary(group => group.Key, group => group.Select(AttachmentResponse.From).ToList());

        var result = new List<MessageResponse>(rows.Count);
        foreach (var message in rows)
        {
            var readAt = await messages.GetReadAt(message.Id);
            result.Add(MessageResponse.From(message, readAt,
                attachmentsByMessage.GetValueOrDefault(message.Id, [])));
        }
        return result;
    }

    private async Task RequireMember(long chatId, long userId)
    {
        if (!await memberships.ChatExists(chatId))
            throw AppException.NotFound("chat not found");
        if (!await memberships.IsMember(chatId, userId))
            throw AppException.Forbidden("user is not a chat member");
    }
}
