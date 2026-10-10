namespace Messenger.Core.Models;
public class Attachment
{
    public long Id { get; set; }

    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;

    public string FileName { get; set; } = null!;

    /// <summary>Путь к файлу на диске. NULL, пока входящее вложение не скачано.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Идентификатор / путь файла на сервере (для скачивания и загрузки).</summary>
    public string? RemoteId { get; set; }

    /// <summary>MIME-тип или категория (≤ 127).</summary>
    public string FileType { get; set; } = null!;
    public long FileSizeBytes { get; set; }

    public TransferState TransferState { get; set; }
    public string? ThumbnailLocalPath { get; set; }
}
