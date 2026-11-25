using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace MessengerDesktop.Appearance;

// Wallpapers belong to this installation. Shared theme documents contain colors only.
public sealed record WallpaperSettings(string Kind = "none", string? ImageId = null, double Blur = 0)
{
    public static WallpaperSettings Current { get; private set; } = new();
    public static event Action? Changed;
    internal static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "messenger-desktop", "wallpapers");
    static string SettingsPath => Path.Combine(DirectoryPath, "wallpaper.json");
    public static readonly (string Id, string Label)[] Presets =
        [("none", "Без фона"), ("topography", "Рельеф"), ("endless-clouds", "Волны"), ("jupiter", "Орбиты")];
    internal const int MaxBytes = 20 * 1024 * 1024;

    static bool ValidId(string? value) => value != null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");
    internal string? ImagePath => Kind == "custom" && ValidId(ImageId) ? Path.Combine(DirectoryPath, ImageId + ".png") : null;

    internal static WallpaperSettings Normalize(WallpaperSettings value)
    {
        if (!double.IsFinite(value.Blur) || value.Blur < 0 || value.Blur > 20) value = value with { Blur = 0 };
        if (value.Kind == "custom") return value.ImagePath is { } path && File.Exists(path) ? value : new();
        return Presets.Any(p => p.Id == value.Kind) ? value with { ImageId = null } : new();
    }

    public static void Apply(WallpaperSettings value)
    {
        Current = Normalize(value);
        Changed?.Invoke();
    }

    public static void Restore() => Apply(Read());

    internal static WallpaperSettings Read()
    {
        try { return Normalize(JsonSerializer.Deserialize<WallpaperSettings>(File.ReadAllText(SettingsPath)) ?? new()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal static void Save(WallpaperSettings? value = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(value ?? Current)));
        File.Move(temporary, SettingsPath, true);
    }

    internal static async Task<WallpaperSettings> Import(Stream source, CancellationToken cancellation = default)
    {
        // Bound even streams without Length. Validate dimensions before allocating decoded pixels.
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
        {
            if (bytes.Length + read > MaxBytes) throw new InvalidDataException("Выберите изображение размером до 20 МБ.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
        return await Task.Run(() => ImportBytes(bytes.ToArray()), cancellation);
    }

    static WallpaperSettings ImportBytes(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("Не удалось открыть изображение. Поддерживаются PNG, JPEG и WebP.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > 16000 || info.Height > 16000 || (long)info.Width * info.Height > 32_000_000)
            throw new InvalidDataException("Изображение слишком большое. Максимум — 32 мегапикселя.");
        using var decoded = SKBitmap.Decode(codec);
        if (decoded == null) throw new InvalidDataException("Файл изображения повреждён.");
        // Apply the camera's EXIF orientation before re-encoding and removing metadata.
        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? info.Height : info.Width, height = swap ? info.Width : info.Height;
        var ratio = Math.Min(1d, 2048d / Math.Max(width, height));
        using var output = new SKBitmap(Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)), SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)output.Width / width, (float)output.Height / height);
            switch (codec.EncodedOrigin)
            {
                case SKEncodedOrigin.TopRight: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft: canvas.Translate(0, height); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.RightTop: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: canvas.Translate(width, height); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.LeftBottom: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
            }
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(decoded, 0, 0, paint);
        }
        using var encoded = output.Encode(SKEncodedImageFormat.Png, 100);
        var normalized = encoded.ToArray();
        var id = Convert.ToHexString(SHA256.HashData(normalized)).ToLowerInvariant();
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, id + ".png");
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, normalized);
            File.Move(temporary, path, true);
        }
        return new("custom", id);
    }

    internal static void Prune()
    {
        // Only files produced by this importer, never original user files or arbitrary paths.
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            var saved = Read().ImageId;
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.png"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (ValidId(id) && id != Current.ImageId && id != saved) File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry on the next apply. */ }
    }
}
