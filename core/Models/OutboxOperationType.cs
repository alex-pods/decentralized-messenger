namespace Messenger.Core.Models;

public enum OutboxOperationType
{
    SendMessage = 0,
    EditMessage = 1,
    DeleteMessage = 2,
    SetReaction = 3,
    RemoveReaction = 4,
    /// <summary>Прочитал чат (ChatId + PayloadJson с ServerId последнего прочитанного).</summary>
    MarkRead = 5,
    /// <summary>Подтверждение серверу, что входящее сообщение надёжно сохранено локально.</summary>
    AckDelivered = 6,
}