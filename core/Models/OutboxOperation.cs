namespace Messenger.Core.Models;

/// <summary>
/// Очередь исходящих операций для сервера. Благодаря ей приложение работает офлайн:
/// всё сначала пишется в БД + в outbox (одной транзакцией), а фоновый воркер потом
/// отправляет на сервер и удаляет запись. Порядок = порядок Id.
/// </summary>
public class OutboxOperation
{
    public long Id { get; set; }
    public OutboxOperationType Type { get; set; }

    public long? MessageId { get; set; }
    public Message? Message { get; set; }

    public long? ChatId { get; set; }

    /// <summary>Доп. данные операции в JSON (новый текст, эмодзи, ServerId и т.п.).</summary>
    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}