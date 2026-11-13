using System.Text.Json.Serialization;
using Messenger.Api.Models;

namespace Messenger.Api.Contracts.Responses;

public sealed record AttachmentResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("file_type")] string? FileType,
    [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes)
{
    public static AttachmentResponse From(Attachment attachment) =>
        new(attachment.Id, attachment.MessageId, attachment.FileName, attachment.ContentType, attachment.SizeBytes);
}
