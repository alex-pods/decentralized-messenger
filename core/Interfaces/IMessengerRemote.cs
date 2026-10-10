using Messenger.Core.Remote;
namespace Messenger.Core.Interfaces;

// Только операции, необходимые локальному MVP. Остальные команды UI пока используют клиент напрямую.
public interface IMessengerRemote
{
    Task<ProfileDto> GetMe(CancellationToken ct = default);
    Task<ProfileDto> GetUser(long id, CancellationToken ct = default);
    Task<List<ChatDto>> GetChats(CancellationToken ct = default);
    Task<ChatDto> GetChat(long chatId, CancellationToken ct = default);
    Task<List<MemberDto>> GetMembers(long chatId, CancellationToken ct = default);
    Task<List<MessageDto>> GetMessages(long chatId, int limit = 50, long? beforeId = null, CancellationToken ct = default);
    Task<MessageIdDto> SendQueuedMessage(long chatId, string text, Guid clientId, long? replyToId, CancellationToken ct = default);
    Task MarkRead(long chatId, long messageId, CancellationToken ct = default);
}
