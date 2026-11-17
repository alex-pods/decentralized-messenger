using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessengerDesktop.Appearance;

namespace MessengerDesktop;

public static class ThemeExchangeCheck
{
    public static int Run(string server)
    {
        try { Check(server).GetAwaiter().GetResult(); Console.WriteLine("PASS theme file round-trip, validation, publish, import, update, deletion"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }

    static async Task Check(string server)
    {
        var theme = ThemeSettings.Presets[1] with { Name = "Theme exchange check" };
        var bytes = ThemeExchange.Export(theme);
        if (ThemeExchange.Parse(bytes) != theme) throw new Exception("File round-trip lost theme properties");
        foreach (var invalid in new[] { "null", "{}", "{\"schemaVersion\":2}", Encoding.UTF8.GetString(bytes).Replace("#191C20", "#191C20\\n") })
        {
            try { ThemeExchange.Parse(Encoding.UTF8.GetBytes(invalid)); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { continue; }
            throw new Exception("Invalid theme was accepted");
        }
        try { await ThemeExchange.ReadBounded(new MemoryStream(new byte[ThemeExchange.MaximumBytes + 1])); throw new Exception("Oversized theme was accepted"); }
        catch (InvalidDataException) { }
        using var api = new MessengerClient(server);
        var tag = "theme" + Guid.NewGuid().ToString("N")[..9];
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await api.Register(tag, password); await api.Login(tag, password);
        var shared = (await api.Send<ThemeShare>(HttpMethod.Post, "themes", new ThemeFile(1, theme), default))!;
        var path = "themes/" + shared.Id;
        try
        {
            var link = new Uri(api.ServerBase + "/" + path);
            var imported = link.Scheme == "https" ? await ThemeExchange.Download(link) : (await api.Send<ThemeShare>(HttpMethod.Get, path, null, default))!;
            if (imported.Theme != theme) throw new Exception("Shared import differs from original");
            var updated = (await api.Send<ThemeShare>(HttpMethod.Patch, path, new { version = shared.Version, theme = new { accent = "#8FB6ED" } }, default))!;
            if (updated.Version != 2 || updated.Theme.Accent != "#8FB6ED") throw new Exception("Shared patch failed");
            if (link.Scheme == "https" && (await ThemeExchange.Download(link)).Theme != updated.Theme) throw new Exception("Public link did not update");
        }
        finally { await api.Send(HttpMethod.Delete, path, null, default); }
    }
}
