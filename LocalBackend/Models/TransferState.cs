namespace LocalBackend.Models;

public enum TransferState
{
    /// <summary>Исходящее: ждёт загрузки на сервер. Входящее: ждёт скачивания.</summary>
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3,
}