using System.Data;
using Dapper;

namespace MessengerBackend.Core;

/// <summary>Файлы: вложения сообщений и аватары. Имена, размеры, проверка содержимого аватаров.</summary>
public static class Files
{
    public const long MaxAttachmentBytes = 10_000_000;
    public const long MaxAvatarBytes = 5_000_000;
    private static readonly string[] AllowedImageSignatures = ["png", "jpeg", "webp"];

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

    /// <summary>Аватары: только реальные PNG/JPEG/WebP (по сигнатуре; PNG — и габариты до 4096).</summary>
    public static bool IsValidAvatarImage(byte[] data, out string reason)
    {
        reason = "";
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            var w = BitConverter.ToInt32(new[] { data[19], data[18], data[17], data[16] }, 0);
            var h = BitConverter.ToInt32(new[] { data[23], data[22], data[21], data[20] }, 0);
            if (w is < 1 or > 4096 || h is < 1 or > 4096)
            { reason = "PNG dimensions out of limits (max 4096x4096)"; return false; }
            return true;
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return true;
        reason = "only PNG, JPEG or WebP images are allowed";
        return false;
    }

    public static async Task<(long Id, AttachmentOut Dto)> SaveAttachment(
        IDbConnection c, string filesRoot, MessageRow msg, IFormFile f)
    {
        var dir = Path.Combine(filesRoot, "attachments", msg.Id.ToString());
        Directory.CreateDirectory(dir);
        var diskName = $"{Guid.NewGuid():N}_{SafeFileName(f.FileName)}";
        var abs = Path.Combine(dir, diskName);
        await using (var fs = File.Create(abs)) await f.CopyToAsync(fs);
        var name = SafeFileName(f.FileName);
        var type = TruncType(f.ContentType, 32);
        var id = c.ExecuteScalar<long>(
            "INSERT INTO attachments(message_id, file_name, path_to_file, file_type, file_size_bytes) " +
            "VALUES(@m, @n, @p, @t, @s); SELECT last_insert_rowid()",
            new { m = msg.Id, n = name, p = abs, t = type, s = f.Length });
        return (id, new AttachmentOut(id, msg.Id, name, type, f.Length));
    }

    public static AttachmentRow AttachmentById(IDbConnection c, long id) =>
        c.QueryFirstOrDefault<AttachmentRow>("SELECT * FROM attachments WHERE id=@i", new { i = id })
        ?? throw new HttpException(404, "вложение не найдено");

    public static string? AvatarPathOf(IDbConnection c, long userId) =>
        c.ExecuteScalar<string?>("SELECT path_to_avatar_file FROM users WHERE id=@u", new { u = userId });

    public static string AvatarAbsPath(string filesRoot, string rel) =>
        Path.Combine(filesRoot, "avatars", Path.GetFileName(rel));
}
