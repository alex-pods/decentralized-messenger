using Messenger.Core.Models;

namespace Messenger.Core.Interfaces;

public interface IMessageStore
{
    Task<User?> GetUserAsync(long id, CancellationToken ct = default);
    Task SetVisibleChatsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    Task SetChatVisibleAsync(long id, bool visible, CancellationToken ct = default);
    Task ReconcileHistoryAsync(long chatId, IReadOnlyList<long> serverIds, long upperId, CancellationToken ct = default);
    Task HideServerMessageAsync(long serverId, CancellationToken ct = default);
    Task RetryFailedAsync(long chatId, CancellationToken ct = default);
    Task UpsertUserAsync(User incoming, CancellationToken ct = default);
    Task<Chat> GetOrCreateChatAsync(long serverChatId, long peerUserId, CancellationToken ct = default);
    Task<List<Chat>> GetChatListAsync(bool archived = false, CancellationToken ct = default);
    Task<Chat> UpsertChatAsync(ChatInfo incoming, CancellationToken ct = default);
    Task<List<ChatMember>> GetMembersAsync(long chatId, CancellationToken ct = default);
    Task ReplaceMembersAsync(long chatId, IReadOnlyList<ChatMemberInfo> members, CancellationToken ct = default);
    Task UpsertMemberAsync(long chatId, ChatMemberInfo member, CancellationToken ct = default);
    Task RemoveMemberAsync(long chatId, long userId, CancellationToken ct = default);

    Task<Message> EnqueueOutgoingAsync(
        long chatId, long currentUserId, string text,
        long? replyToMessageId = null,
        IReadOnlyList<AttachmentInfo>? attachments = null,
        CancellationToken ct = default);

    Task MarkSentAsync(Guid clientId, long serverId, DateTime serverSentAt, CancellationToken ct = default);
    Task MarkSendFailedAsync(long messageId, CancellationToken ct = default);

    Task<Message?> SaveIncomingAsync(IncomingMessage m, CancellationToken ct = default);
    Task ApplyDeliveredAsync(long serverMessageId, DateTime at, CancellationToken ct = default);
    Task ApplyReadAsync(long chatId, long upToServerId, DateTime at, CancellationToken ct = default);
    Task ApplyRemoteEditAsync(long serverMessageId, string newText, DateTime editedAt, CancellationToken ct = default);
    Task<List<string>> ApplyRemoteDeleteAsync(long serverMessageId, DateTime at, CancellationToken ct = default);

    Task<int> MarkChatReadAsync(long chatId, CancellationToken ct = default);
    Task<List<string>> DeleteLocallyAsync(long messageId, CancellationToken ct = default);
    Task SetReactionAsync(long messageId, long userId, string? emoji, bool enqueueForServer, CancellationToken ct = default);

    Task<List<Message>> GetMessagesPageAsync(long chatId, MessageCursor? before = null, int limit = 50, CancellationToken ct = default);
    Task<List<Message>> SearchAsync(string text, long? chatId = null, int limit = 50, CancellationToken ct = default);

    Task<List<OutboxOperation>> GetDueOutboxAsync(int limit = 20, CancellationToken ct = default);
    Task CompleteOutboxAsync(long operationId, CancellationToken ct = default);
    Task RecordOutboxFailureAsync(long operationId, string error, CancellationToken ct = default);

    Task<string?> GetStateAsync(string key, CancellationToken ct = default);
    Task SetStateAsync(string key, string value, CancellationToken ct = default);
}