using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

/// <summary>Личные закрепы: отдельный файл для пары сервер/пользователь. Сеть не используется.</summary>
public static class LocalPinStore
{
    public sealed record Entry(string ChatId, string MessageId, string Text, string? SenderId = null,
        DateTimeOffset? SentAt = null, long? Sequence = null);
    internal static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "messenger-desktop", "pins");

    internal static string ConvertedDirectoryPath => Path.Combine(DirectoryPath, "migrated-numeric");

    static string FilePath(string server, string userId, string? directory = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.TrimEnd('/') + ":" + userId)));
        return Path.Combine(directory ?? DirectoryPath, key + ".json");
    }

    public static List<Entry> Load(string server, string userId)
    {
        var path = FilePath(server, userId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    internal static List<Entry>? LoadConverted(string server, string userId)
    {
        var path = FilePath(server, userId, ConvertedDirectoryPath);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))
            : null;
    }

    public static void Save(string server, string userId, List<Entry> entries)
    {
        var path = FilePath(server, userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
