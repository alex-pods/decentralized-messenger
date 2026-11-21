using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessengerDesktop;

/// <summary>Личные закрепы: отдельный файл для пары сервер/пользователь. Сеть не используется.</summary>
public static class LocalPinStore
{
    public sealed record Entry(long ChatId, long MessageId, string Text, long? SenderId = null, DateTimeOffset? SentAt = null);

    static string FilePath(string server, long userId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.TrimEnd('/') + ":" + userId)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "messenger-desktop", "pins", key + ".json");
    }

    public static List<Entry> Load(string server, long userId)
    {
        var path = FilePath(server, userId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public static void Save(string server, long userId, List<Entry> entries)
    {
        var path = FilePath(server, userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
