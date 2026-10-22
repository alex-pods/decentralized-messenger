using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Repositories;

namespace Messenger.Api.Services;

public sealed class MessagesService(IMessagesRepository messages)
{
    public async Task<List<MessageResponse>> GetHistory(long chatId, long? beforeId, int? limit)
    {
        var n = limit ?? 50;
        if (n is < 1 or > 100) throw AppException.BadRequest("limit: от 1 до 100");
        // implement me: проверить, что текущий пользователь — участник чата (иначе AppException.Forbidden)
        var list = await messages.GetByChat(chatId, beforeId, n);
        return list.Select(MessageResponse.From).ToList();
    }
}
