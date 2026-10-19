using System.Text.Json.Serialization;

namespace MessengerBackend;

public record AttachmentOut(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("file_type")] string FileType,
    [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes);
