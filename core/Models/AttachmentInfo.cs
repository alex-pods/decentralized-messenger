namespace Messenger.Core.Models;
public sealed record AttachmentInfo(
    string FileName,
    string FileType,
    long FileSizeBytes,
    string? LocalPath = null,     // исходящее: файл, выбранный пользователем
    string? RemoteId = null);     // входящее: что скачивать с сервера