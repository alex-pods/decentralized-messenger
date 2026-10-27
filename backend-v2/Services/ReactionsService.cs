using Messenger.Api.Common.Errors;
using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;

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
    IChatsRepository chats) : IReactionsService
{
    private const int MaxEmojiLength = 32;

    public async Task<IReadOnlyList<ReactionRow>> GetAsync(long messageId, long viewerId, CancellationToken ct = default)
    {
        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, viewerId, ct);

        var all = await reactions.GetByMessageIdsAsync(new[] { messageId }, ct);
        return all.TryGetValue(messageId, out var list) ? list : Array.Empty<ReactionRow>();
    }

    public async Task AddAsync(long messageId, long userId, string emoji, CancellationToken ct = default)
    {
        ValidateEmoji(emoji);

        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, userId, ct);

        await reactions.AddAsync(messageId, userId, emoji, ct);
    }

    public async Task RemoveAsync(long messageId, long userId, string emoji, CancellationToken ct = default)
    {
        ValidateEmoji(emoji);

        var message = await RequireMessageAsync(messageId, ct);
        await RequireAccessAsync(message.ChatId, userId, ct);

        await reactions.RemoveAsync(messageId, userId, emoji, ct);
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
