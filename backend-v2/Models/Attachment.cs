namespace Messenger.Api.Models;

/// <summary>A row from the attachments table.</summary>
public sealed record Attachment(
    long Id,
    long MessageId,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string StoragePath);
