using System.Data;
using Dapper;

namespace MessengerBackend.Core;

/// <summary>Файлы: вложения сообщений и аватары. Имена, размеры, проверка содержимого аватаров.</summary>
public static class Files
{
    public const long MaxAttachmentBytes = 10_000_000;
    public const long MaxAvatarBytes = 5_000_000;

    public static string SafeFileName(string? name)
    {
        var clean = Path.GetFileName(name ?? "");
        foreach (var ch in Path.GetInvalidFileNameChars())
            clean = clean.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(clean) ? "file" : clean;
    }

    public static string SafeExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return !string.IsNullOrEmpty(ext) && ext.Length <= 10 &&
               ext.All(ch => char.IsLetterOrDigit(ch) || ch == '.')
            ? ext.ToLowerInvariant() : "";
    }

    public static string TruncType(string? s, int max) => string.IsNullOrEmpty(s)
        ? "application/octet-stream" : (s.Length <= max ? s : s[..max]);

    /// <summary>Fully decode PNG/JPEG/WebP, checking dimensions before allocating pixel memory.</summary>
    public static bool IsValidAvatarImage(byte[] data, out string reason)
    {
        reason = "only complete PNG, JPEG or WebP images up to 4096x4096 are allowed";
        using var source = SkiaSharp.SKData.CreateCopy(data);
        using var codec = SkiaSharp.SKCodec.Create(source);
        if (codec is null || codec.EncodedFormat is not (SkiaSharp.SKEncodedImageFormat.Png or
            SkiaSharp.SKEncodedImageFormat.Jpeg or SkiaSharp.SKEncodedImageFormat.Webp)) return false;
        if (codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096) return false;
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height,
            SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        return codec.GetPixels(info, bitmap.GetPixels()) == SkiaSharp.SKCodecResult.Success;
    }

    public static MessageRow RequireAttachableMessage(IDbConnection c, long messageId, long userId)
    {
        var m = MessageRepository.ById(c, messageId);
        ChatRepository.RequireMember(c, m.Chat_Id, userId);
        if (m.Sender_Id != userId) throw new HttpException(403, "вкладывать файлы может только автор сообщения");
        if (m.Deleted_At is not null) throw new HttpException(409, "message_deleted", "message_deleted");
        if (m.Text.Length == 0) throw new HttpException(409, "content_not_stored", "content_not_stored");
        return m;
    }

    public static async Task<(long Id, AttachmentOut Dto, string Path)> SaveAttachment(
        IDbConnection c, string filesRoot, MessageRow msg, IFormFile f)
    {
        var dir = Path.Combine(filesRoot, "attachments", msg.Id.ToString());
        Directory.CreateDirectory(dir);
        var diskName = $"{Guid.NewGuid():N}.bin";
        var abs = Path.Combine(dir, diskName);
        await using var buffer = new MemoryStream();
        await f.CopyToAsync(buffer);
        await Infrastructure.StorageEncryption.WriteFile(abs, buffer.ToArray());
        var name = SafeFileName(f.FileName);
        var type = TruncType(f.ContentType, 32);
        long id;
        try
        {
            id = c.ExecuteScalar<long>(
            "INSERT INTO attachments(message_id, file_name, path_to_file, file_type, file_size_bytes) " +
            "VALUES(@m, @n, @p, @t, @s); SELECT last_insert_rowid()",
                new { m = msg.Id, n = name, p = abs, t = type, s = f.Length });
        }
        catch
        {
            File.Delete(abs);
            throw;
        }
        return (id, new AttachmentOut(id, msg.Id, name, type, f.Length), abs);
    }

    public static AttachmentRow AttachmentById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<AttachmentRow>("SELECT * FROM attachments WHERE id=@i", new { i = id })
        ?? throw new HttpException(404, "вложение не найдено");

    public static string? AvatarPathOf(IDbConnection c, long userId) =>
        c.ExecuteScalar<string?>("SELECT path_to_avatar_file FROM users WHERE id=@u", new { u = userId });

    public static string AvatarAbsPath(string filesRoot, string rel) =>
        Path.Combine(filesRoot, "avatars", Path.GetFileName(rel));
}
