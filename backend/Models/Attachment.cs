using Messenger.Api.Infrastructure;

namespace Messenger.Api.Models;

/// <summary>A row from the attachments table.</summary>
public sealed record Attachment(
    long Id,
    string MessageId,
    Secret FileName,
    Secret? ContentType,
    long SizeBytes,
    Secret StoragePath);
