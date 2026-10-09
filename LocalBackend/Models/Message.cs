namespace LocalBackend.Models;

public class Message
{
    /// <summary>Локальный автоинкрементный id — основной ключ в локальной БД.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Id, который клиент сам сгенерировал при создании сообщения.
    /// Нужен, чтобы сообщение можно было сохранить ДО ответа сервера и чтобы
    /// повторная отправка не создавала дублей (идемпотентность).
    /// </summary>
    public Guid ClientId { get; set; }

    /// <summary>Id сообщения на сервере. NULL, пока сервер не принял сообщение.</summary>
    public long? ServerId { get; set; }

    public long ChatId { get; set; }
    public Chat Chat { get; set; } = null!;

    public long SenderId { get; set; }
    public User Sender { get; set; } = null!;

    /// <summary>true — отправлено мной (удобно для UI и фильтров, избавляет от сравнения с текущим пользователем).</summary>
    public bool IsOutgoing { get; set; }

    /// <summary>Ответ на сообщение — локальная ссылка. SET NULL, если исходное удалено «у меня».</summary>
    public long? ReplyToMessageId { get; set; }
    public Message? ReplyTo { get; set; }

    /// <summary>Серверный id сообщения, на которое отвечают. Нужен, если оригинала нет локально (например, пришёл раньше / был стёрт).</summary>
    public long? ReplyToServerId { get; set; }

    /// <summary>Текст. Для сообщений только с вложениями — пустая строка. После удаления «у всех» очищается.</summary>
    public string Text { get; set; } = "";

    public DateTime SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? EditedAt { get; set; }

    /// <summary>
    /// Исходящие: когда собеседник прочитал.
    /// Входящие: когда я прочитал (NULL = непрочитанное).
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Надгробие: сообщение удалено «у всех». Строка остаётся, чтобы ответы на неё не ломались.</summary>
    public DateTime? DeletedAt { get; set; }

    public MessageSyncState SyncState { get; set; }

    public List<Attachment> Attachments { get; set; } = [];
    public List<MessageReaction> Reactions { get; set; } = [];
}