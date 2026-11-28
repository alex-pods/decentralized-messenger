using System.Globalization;
using Messenger.Api.Antispam;
using Messenger.Api.Common.Errors;
using Messenger.Api.Contracts.Requests;
using Messenger.Api.Contracts.Responses;
using Messenger.Api.Realtime;
using Messenger.Api.Repositories;
using Messenger.Identifiers;

namespace Messenger.Api.Services;

internal sealed class MessageRelayService(
    IChatsRepository chats, RelayReceiptsRepository receipts, AntispamService antispam,
    WsEventPublisher publisher, TimeProvider clock)
{
    internal async Task<RelayMessageResponse> SendAsync(string chatId, string senderId,
        RelayMessageRequest request, CancellationToken ct)
    {
        if (await chats.GetAccessAsync(chatId, senderId, ct) is null)
            throw new NotFoundException("Chat not found");
        if (request.MessageId is not { } id || !EntityIds.IsMessageInChat(id, chatId))
            throw new ValidationException("Invalid message ID for this chat");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4096)
            throw new ValidationException("Text must be 1..4096 characters");

        var now = clock.GetUtcNow();
        var (receipt, created) = await receipts.ReserveAsync(id, chatId, senderId, now.ToString("O", CultureInfo.InvariantCulture), ct);
        if (receipt.SenderId != senderId)
            throw AppException.Conflict("message_id_conflict", "Message ID already used");
        if (!created)
        {
            if (receipt.QueuedConnections == 0)
                throw AppException.Conflict("relay_delivery_unknown", "Не удалось подтвердить отправку. Проверьте, дошло ли сообщение.");
            return new RelayMessageResponse(id, receipt.Sequence,
                DateTimeOffset.ParseExact(receipt.SentAt, "O", CultureInfo.InvariantCulture), checked((int)receipt.QueuedConnections));
        }

        try
        {
            antispam.CheckSend(senderId, chatId, null);
        }
        catch
        {
            await receipts.CompleteAsync(id, 0);
            throw;
        }

        // After claiming the ID, finish independently of the HTTP caller dropping its connection.
        // An interrupted dispatch can have reached a recipient. Keep its claim in that case.
        using var dispatch = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var message = new MessageResponse(id, chatId, senderId, null, request.Text, "relayed",
            now, null, null, null, null, null, null, Sequence: receipt.Sequence);
        var queued = await publisher.RelayToChatAsync(chatId, senderId, message, dispatch.Token);
        await receipts.CompleteAsync(id, queued);
        if (queued == 0)
            throw AppException.Conflict("relay_no_recipient", "Сейчас в чате нет подключённых получателей с поддержкой этого режима.");

        return new RelayMessageResponse(id, receipt.Sequence, now, queued);
    }
}
