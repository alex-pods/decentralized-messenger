using Messenger.Api.Common.Errors;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Realtime;

namespace Messenger.Api.Services;

public interface IReactionsService
{
    Task<IReadOnlyList<ReactionRow>> GetAsync(long messageId, long viewerId, CancellationToken ct = default);
    Task AddAsync(long messageId, long userId, string emoji, CancellationToken ct = default);
    Task RemoveAsync(long messageId, long userId, string emoji, CancellationToken ct = default);
}

public sealed class ReactionsService(
    IReactionsRepository reactions,
    IMessagesRepository messages,
    IChatsRepository chats,
    WsEventPublisher publisher) : IReactionsService
{
    private const int MaxEmojiLength = 32;

    public async Task<IReadOnlyList<ReactionRow>> GetAsync(long messageId, long viewerId, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, viewerId, ct);

        var all = await reactions.GetByMessageIdsAsync(new[] { messageId }, ct);
        return all.TryGetValue(messageId, out var list) ? list : Array.Empty<ReactionRow>();
    }

    public async Task AddAsync(
        long messageId,
        long userId,
        string emoji,
        CancellationToken ct = default)
    {
        emoji = (emoji ?? "").Trim();
        ValidateEmoji(emoji);

        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, userId, ct);

        if (message.ContentState == MessageContentState.Deleted)
            throw AppException.Conflict("message_deleted", "message_deleted");

        var added = await reactions.AddAsync(
            messageId, userId, emoji, ct);

        if (!added)
        {
            // Между проверкой и INSERT сообщение могли удалить.
            var current = await RequireMessageAsync(messageId, ct);

            if (current.ContentState == MessageContentState.Deleted)
                throw AppException.Conflict("message_deleted", "message_deleted");
        }

        await publisher.PublishToChatSafelyAsync(
            message.ChatId,
            userId,
            new WsEventResponse(
                "reaction_added",
                ChatId: message.ChatId,
                UserId: userId,
                MessageId: messageId,
                Emoji: emoji));
    }

    public async Task RemoveAsync(
        long messageId,
        long userId,
        string emoji,
        CancellationToken ct = default)
    {
        emoji = (emoji ?? "").Trim();
        ValidateEmoji(emoji);

        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, userId, ct);

        var removed = await reactions.RemoveAsync(
            messageId, userId, emoji, ct);

        if (!removed)
            throw new NotFoundException("Reaction not found");

        await publisher.PublishToChatSafelyAsync(
            message.ChatId,
            userId,
            new WsEventResponse(
                "reaction_removed",
                ChatId: message.ChatId,
                UserId: userId,
                MessageId: messageId,
                Emoji: emoji));
    }

    private static void ValidateEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji) || emoji.Length > MaxEmojiLength)
            throw new ValidationException($"Emoji must be 1..{MaxEmojiLength} characters");
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
