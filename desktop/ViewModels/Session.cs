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
    static string FilePath => Preview.Server != null
        ? Path.Combine(Preview.OutDir, "test-session.json")
        : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "messenger-desktop", "session.json");

    public sealed class Saved
    {
        public string Server { get; set; } = "";
        public string Token { get; set; } = "";
        public string? ServerFingerprint { get; set; }
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

    internal static string? GetFingerprint(Saved saved) => saved.ServerFingerprint
        ?? ServerTrustStore.Lookup(MessengerClient.NormalizeServerAddress(saved.Server));

    internal static MessengerClient ConnectSaved(Saved saved, string address)
    {
        address = MessengerClient.NormalizeServerAddress(address);
        var original = MessengerClient.NormalizeServerAddress(saved.Server);
        var fingerprint = GetFingerprint(saved);
        if (fingerprint == null && !string.Equals(original, address, StringComparison.Ordinal))
            throw new ServerIdentityException("Сначала подтвердите ключ по прежнему адресу, чтобы перенести сохранённый аккаунт.");
        return new MessengerClient(address, saved.Token, expectedFingerprint: fingerprint);
    }

    public static void Save(MessengerClient api)
    {
        ServerTrustStore.WriteJson(FilePath, new Saved
        {
            Server = api.ServerBase,
            Token = api.Token ?? throw new InvalidOperationException("Missing session token."),
            ServerFingerprint = api.ServerFingerprint ?? throw new InvalidOperationException("Missing server identity.")
        });
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* нет файла — не страшно */ }
    }
}
