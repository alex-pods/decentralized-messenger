using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace MessengerDesktop.Appearance;

public sealed record ThemeFile(int SchemaVersion, ThemeSettings Theme);
public sealed record ThemeShare(string Id, string OwnerId, int Version, int SchemaVersion, ThemeSettings Theme);
public static class ThemeExchange
{
    public const int MaximumBytes = 16384;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Validate(ThemeSettings theme)
    {
        if (theme == null || string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Length > 64 || theme.Name.Any(char.IsControl))
            throw new InvalidDataException("Название темы: 1–64 символа.");
        foreach (var color in new[] { theme.Background, theme.Surface, theme.Text, theme.Accent })
            if (color == null || !Regex.IsMatch(color, @"\A#[0-9a-fA-F]{6}\z")) throw new InvalidDataException("Некорректный цвет в теме.");
    }
    public static ThemeSettings Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Файл темы больше 16 КБ.");
        var file = JsonSerializer.Deserialize<ThemeFile>(bytes, Json) ?? throw new InvalidDataException("Пустая тема.");
        if (file.SchemaVersion != 1) throw new InvalidDataException("Эта версия темы не поддерживается.");
        Validate(file.Theme); return file.Theme;
    }
    public static byte[] Export(ThemeSettings theme) { Validate(theme); return JsonSerializer.SerializeToUtf8Bytes(new ThemeFile(1, theme), Json); }
    public static async Task<byte[]> ReadBounded(Stream stream, CancellationToken cancellation = default)
    {
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            if (data.Length + count > MaximumBytes) throw new InvalidDataException("Тема больше 16 КБ.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
    public static async Task<ThemeShare> Download(Uri uri)
    {
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Нужна HTTPS-ссылка на тему.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // No session token is ever sent to an imported URL.
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = await ReadBounded(stream, timeout.Token);
        Parse(bytes);
        var share = JsonSerializer.Deserialize<ThemeShare>(bytes, Json)!;
        if (!Regex.IsMatch(share.Id ?? "", @"\A[a-f0-9]{32}\z") || share.Version < 1) throw new InvalidDataException("Неполная ссылка на тему.");
        return share;
    }
}
