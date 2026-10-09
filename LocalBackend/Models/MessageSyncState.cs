namespace LocalBackend.Models;

/// <summary>Состояние синхронизации сообщения с сервером.</summary>
public enum MessageSyncState
{
    /// <summary>Создано локально, ещё не принято сервером (лежит в outbox).</summary>
    Pending = 0,
    /// <summary>Принято сервером (у исходящих есть ServerId) / получено от сервера (входящие).</summary>
    Synced = 1,
    /// <summary>Не удалось отправить, нужна ручная повторная отправка.</summary>
    Failed = 2,
}