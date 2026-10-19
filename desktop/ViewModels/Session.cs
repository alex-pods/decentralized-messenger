using System.Text.Json;

namespace MessengerDesktop;

public sealed class Navigator
{
    public Navigator(Avalonia.Controls.Window window) => Window = window;
    public Avalonia.Controls.Window Window { get; }
    public void Go(object page) => Window.DataContext = page;
}

public static class SessionStore
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "session.json");

    public sealed class Saved
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
    }

    public static Saved? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved is null || string.IsNullOrWhiteSpace(saved.Server) || string.IsNullOrWhiteSpace(saved.Token))
                return null;
            return saved;
        }
        catch { return null; }
    }

    public static void Save(string server, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Server = server, Token = token }));
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* нет файла — не страшно */ }
    }
}
