using Messenger.Api.Common.Errors;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Infrastructure;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Realtime;

namespace Messenger.Api.Services;

public interface IMessagesService
{
    Task<IReadOnlyList<MessageRow>> GetHistoryAsync(long chatId, long viewerId, HistoryQuery query, CancellationToken ct = default);
    Task<MessageRow> GetMessageAsync(long messageId, long viewerId, CancellationToken ct = default);
    Task<AddMessageResult> SendAsync(long chatId, long senderId, SendMessageRequest request, CancellationToken ct = default);
    Task<MessageRow> EditAsync(long messageId, long editorId, EditMessageRequest request, CancellationToken ct = default);
    Task<MessageRow> DeleteAsync(long messageId, long deleterId, bool forEveryone, CancellationToken ct = default);
    Task<IReadOnlyList<PinRow>> ListPinsAsync(long chatId, long viewerId, CancellationToken ct = default);
    Task PinAsync(long chatId, long userId, long messageId, CancellationToken ct = default);
    Task UnpinAsync(long chatId, long userId, long messageId, CancellationToken ct = default);
}

public sealed class MessagesService(
    IMessagesRepository messages,
    IChatsRepository chats,
    IAttachmentsRepository attachments,
    IFileStorage files,
    ILogger<MessagesService> logger,
    IReactionsService reactions,
    WsEventPublisher publisher) : IMessagesService
{
    private const int MaxTextLength = 4096;

    public async Task<IReadOnlyList<MessageRow>> GetHistoryAsync(
        long chatId, long viewerId, HistoryQuery query, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        if (query.Limit is < 1 or > 100 ||
            query.BeforeId is < 1 ||
            query.AfterId is < 1)
        {
            throw new ValidationException("Invalid history limit or cursor");
        }
        return await messages.GetHistoryAsync(chatId, viewerId, query, ct);
    }

    public async Task<MessageRow> GetMessageAsync(long messageId, long viewerId, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, viewerId, ct);
        return message;
    }

    public async Task<AddMessageResult> SendAsync(
        long chatId, long senderId, SendMessageRequest request, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, senderId, ct);

        var text = request.Text;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new ValidationException(
                $"Text must be 1..{MaxTextLength} characters");

        if (request.ClientRef is { Length: > 64 })
            throw new ValidationException("ClientRef must be at most 64 characters");

        if (request.ReplyToId is < 1)
            throw new ValidationException("ReplyToId must be positive");

        if (request.ReplyToId is long replyToId)
        {
            var parent = await RequireMessageAsync(replyToId, ct);

            if (parent.ChatId != chatId)
                throw new ValidationException(
                    "Reply target must be in the same chat");
        }

        var result = await messages.AddAsync(
            new NewMessage(chatId, senderId, text, request.ReplyToId, request.ClientRef), ct);
        if (!result.ContentMatches)
            throw AppException.Conflict(
                "client_ref_conflict",
                "ClientRef was already used with different message content");
        if (result.Created)
        {
            try
            {
                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var response = await MessageResponseFactory.CreateAsync(
                    result.Message,
                    senderId,
                    reactions,
                    attachments,
                    timeout.Token);

                var delivered = await publisher.PublishToChatAsync(
                    chatId,
                    senderId,
                    new WsEventResponse(
                        "message",
                        ChatId: chatId,
                        Message: response),
                    timeout.Token);

                if (delivered > 0)
                {
                    await messages.MarkDeliveredAsync(
                        result.Message.Id, timeout.Token);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to publish saved message {MessageId}",
                    result.Message.Id);
            }
        }

    return result;
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

        var text = request.Text;

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new ValidationException(
                $"Text must be 1..{MaxTextLength} characters");

        var edited = await messages.EditAsync(messageId, text, ct)
            ?? throw new NotFoundException("Message not found");

        await PublishMessageChangeAsync(
            "message_edited",
            edited,
            editorId);

        return edited;
    }

    public async Task<MessageRow> DeleteAsync(
        long messageId, long deleterId, bool forEveryone, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        var access = await RequireAccessAsync(message.ChatId, deleterId, ct);

        if (!forEveryone)
        {
            await messages.HideAsync(messageId, deleterId, ct);

            try
            {
                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));

                await publisher.PublishToUserAsync(
                    deleterId,
                    new WsEventResponse(
                        "message_deleted",
                        ChatId: message.ChatId,
                        MessageId: messageId,
                        ForEveryone: false),
                    timeout.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to publish personal deletion of message {MessageId}",
                    messageId);
            }

            return message;
        }

        var wasPinned = await messages.IsPinnedAsync(message.ChatId, messageId, ct);

        var canDelete = message.SenderId == deleterId
            || (access.Type == ChatType.Group && access.Role is MemberRole.Admin or MemberRole.Owner);

        if (!canDelete)
            throw new ForbiddenException("Not allowed to delete this message");

        if (message.ContentState == MessageContentState.Deleted)
            return message;

        

        var result = await messages.DeleteAsync(messageId, ct)
            ?? throw new NotFoundException("Message not found");

        var deleted = result.Message;

        if (!result.Changed)
            return deleted;

        await PublishMessageChangeAsync(
            "message_deleted",
            deleted,
            deleterId,
            forEveryone: true);

        if (wasPinned)
        {
            await messages.UnpinAsync(message.ChatId, messageId, ct);
            await PublishPinChangeAsync("message_unpinned", deleted, deleterId);
        }

        foreach (var path in result.AttachmentPaths)
        {
            try
            {
                await files.Delete(path, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to remove deleted attachment {Path}",
                    path);
            }
        }

        return deleted;
    }

    public async Task<IReadOnlyList<PinRow>> ListPinsAsync(
        long chatId, long viewerId, CancellationToken ct = default)
    {
        await RequireAccessAsync(chatId, viewerId, ct);
        return await messages.ListPinsAsync(chatId, viewerId, ct);
    }

    public async Task PinAsync(long chatId, long userId, long messageId, CancellationToken ct = default)
    {
        var access = await RequireAccessAsync(chatId, userId, ct);
        RequirePinRight(access);
        var message = await RequireMessageAsync(messageId, ct);
        if (message.ChatId != chatId)
            throw new ValidationException("Message must belong to this chat");
        if (message.ContentState != MessageContentState.Stored)
            throw AppException.Conflict("message_not_pinnable", "Only stored messages can be pinned");
        if (!await messages.IsPinnedAsync(chatId, messageId, ct)
            && await messages.CountPinsAsync(chatId, ct) >= 20)
            throw new ValidationException("A chat can have at most 20 pinned messages");

        var created = await messages.PinAsync(chatId, messageId, userId, DateTimeOffset.UtcNow, ct);
        if (created)
            await PublishPinChangeAsync("message_pinned", message, userId);
    }

    public async Task UnpinAsync(long chatId, long userId, long messageId, CancellationToken ct = default)
    {
        var access = await RequireAccessAsync(chatId, userId, ct);
        RequirePinRight(access);
        var message = await RequireMessageAsync(messageId, ct);
        if (message.ChatId != chatId)
            throw new ValidationException("Message must belong to this chat");
        var removed = await messages.UnpinAsync(chatId, messageId, ct);
        if (removed)
            await PublishPinChangeAsync("message_unpinned", message, userId);
    }

    private static void RequirePinRight(ChatAccess access)
    {
        if (access.Type == ChatType.Group && access.Role is not (MemberRole.Admin or MemberRole.Owner))
            throw new ForbiddenException("Only admins can pin messages");
    }

    private async Task PublishPinChangeAsync(string eventType, MessageRow message, long actorId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await MessageResponseFactory.CreateAsync(
                message, actorId, reactions, attachments, timeout.Token);
            await publisher.PublishToChatAsync(
                message.ChatId,
                actorId,
                new WsEventResponse(
                    eventType,
                    ChatId: message.ChatId,
                    Message: response,
                    MessageId: message.Id,
                    UserId: actorId),
                timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish {EventType} for message {MessageId}",
                eventType,
                message.Id);
        }
    }

    private async Task PublishMessageChangeAsync(
        string eventType,
        MessageRow message,
        long actorId,
        bool? forEveryone = null)
    {
        try
        {
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var response = await MessageResponseFactory.CreateAsync(
                message,
                actorId,
                reactions,
                attachments,
                timeout.Token);

            await publisher.PublishToChatAsync(
                message.ChatId,
                actorId,
                new WsEventResponse(
                    eventType,
                    ChatId: message.ChatId,
                    Message: response,
                    MessageId: forEveryone.HasValue ? message.Id : null,
                    ForEveryone: forEveryone),
                timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to publish {EventType} for message {MessageId}",
                eventType,
                message.Id);
        }
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
