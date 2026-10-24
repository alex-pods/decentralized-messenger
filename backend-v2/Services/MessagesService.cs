using Messenger.Api.Common.Errors;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Services;

public interface IMessagesService
{
    Task<IReadOnlyList<MessageRow>> GetHistoryAsync(long chatId, long viewerId, HistoryQuery query, CancellationToken ct = default);
    Task<MessageRow> GetMessageAsync(long messageId, long viewerId, CancellationToken ct = default);
    Task<MessageRow> SendAsync(long chatId, long senderId, SendMessageRequest request, CancellationToken ct = default);
    Task<MessageRow> EditAsync(long messageId, long editorId, EditMessageRequest request, CancellationToken ct = default);
    Task<MessageRow> DeleteAsync(long messageId, long deleterId, bool forEveryone, CancellationToken ct = default);
}

public sealed class MessagesService(
    IMessagesRepository messages,
    IChatsRepository chats,
    IAttachmentsRepository attachments,
    IFileStorage files,
    ILogger<MessagesService> logger) : IMessagesService
{
    private const int MaxTextLength = 4096;

    public async Task<IReadOnlyList<MessageRow>> GetHistoryAsync(
        long chatId, long viewerId, HistoryQuery query, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        return await messages.GetHistoryAsync(chatId, viewerId, query, ct);
    }

    public async Task<MessageRow> GetMessageAsync(long messageId, long viewerId, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, viewerId, ct);
        return message;
    }

    public async Task<MessageRow> SendAsync(
        long chatId, long senderId, SendMessageRequest request, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, senderId, ct);

        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            throw new ValidationException($"Text must be 1..{MaxTextLength} characters");

        if (request.ClientRef is { Length: > 64 })
            throw new ValidationException("ClientRef must be at most 64 characters");

        if (request.ReplyToId is long replyToId)
        {
            var parent = await RequireMessageAsync(replyToId, ct);
            if (parent.ChatId != chatId)
                throw new ValidationException("Reply target must be in the same chat");
            if (parent.ContentState != MessageContentState.Stored)
                throw new ValidationException("Cannot reply to a deleted message");
        }

        var result = await messages.AddAsync(
            new NewMessage(chatId, senderId, text, request.ReplyToId, request.ClientRef), ct);
        if (!result.Created && (result.Message.ChatId != chatId ||
            result.Message.ReplyToId != request.ReplyToId ||
            result.Message.Text != text))
            throw new ConflictException("ClientRef was already used with different message content");
        return result.Message;
    }

    public async Task<MessageRow> EditAsync(
        long messageId, long editorId, EditMessageRequest request, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, editorId, ct);

        if (message.SenderId != editorId)
            throw new ForbiddenException("Only the author can edit the message");
        if (message.ContentState != MessageContentState.Stored)
            throw new ConflictException("Only stored messages can be edited");

        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
            throw new ValidationException($"Text must be 1..{MaxTextLength} characters");

        return await messages.EditAsync(messageId, text, ct)
            ?? throw new NotFoundException("Message not found");
    }

    public async Task<MessageRow> DeleteAsync(
        long messageId, long deleterId, bool forEveryone, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        var access = await RequireAccessAsync(message.ChatId, deleterId, ct);

        if (!forEveryone)
        {
            await messages.HideAsync(messageId, deleterId, ct);
            return message;
        }

        var canDelete = message.SenderId == deleterId
            || (access.Type == ChatType.Group && access.Role is MemberRole.Admin or MemberRole.Owner);

        if (!canDelete)
            throw new ForbiddenException("Not allowed to delete this message");

        if (message.ContentState == MessageContentState.Deleted)
            return message;

        var relatedAttachments = await attachments.GetByMessageIds([messageId]);
        var deleted = await messages.DeleteAsync(messageId, ct)
            ?? throw new NotFoundException("Message not found");

        foreach (var attachment in relatedAttachments)
        {
            try
            {
                await files.Delete(attachment.StoragePath, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to remove deleted attachment {AttachmentId}", attachment.Id);
            }
        }
        return deleted;
    }

    private async Task<MessageRow> RequireMessageAsync(long messageId, CancellationToken ct)
        => await messages.GetByIdAsync(messageId, ct)
            ?? throw new NotFoundException("Message not found");

    private async Task<ChatAccess> RequireAccessAsync(long chatId, long userId, CancellationToken ct)
    {
        var access = await chats.GetAccessAsync(chatId, userId, ct);
        if (access is not null) return access;
        if (!await chats.ExistsAsync(chatId, ct))
            throw new NotFoundException("Chat not found");
        throw new ForbiddenException("User is not a chat member");
    }
}
