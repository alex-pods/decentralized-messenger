using System.Globalization;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace MessengerDesktop.Appearance;

internal static class WallpaperRaster
{
    // Render static backgrounds once per selection, including in software screenshots.
    internal static Bitmap Custom(string path, double blur)
    {
        if (blur == 0) return new Bitmap(path);
        using var original = SKBitmap.Decode(path) ?? throw new InvalidDataException("Изображение повреждено.");
        var sigma = (float)(blur * original.Width / 900);
        var padding = (int)Math.Ceiling(sigma * 3);
        using var padded = new SKBitmap(original.Width + padding * 2, original.Height + padding * 2);
        using (var edgeCanvas = new SKCanvas(padded))
        {
            using var shader = SKShader.CreateBitmap(original, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(padding, padding));
            using var edgePaint = new SKPaint { Shader = shader };
            edgeCanvas.DrawRect(0, 0, padded.Width, padded.Height, edgePaint);
        }
        using var output = new SKBitmap(original.Width, original.Height);
        using var canvas = new SKCanvas(output);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(padded, -padding, -padding, paint);
        return Snapshot(output);
    }

    internal static Bitmap Pattern(string kind, ThemeSettings theme, double blur)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://MessengerDesktop/Assets/Wallpapers/{kind}.svg"));
        var svg = XElement.Load(stream);
        var scale = kind == "topography" ? .75f : 1.5f;
        var width = (int)(float.Parse(svg.Attribute("width")!.Value, CultureInfo.InvariantCulture) * scale);
        var height = (int)(float.Parse(svg.Attribute("height")!.Value, CultureInfo.InvariantCulture) * scale);
        // Adjacent tiles supply pixels across the seam when blurred.
        using var source = new SKBitmap(width * 3, height * 3);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColor.Parse(theme.Background));
            using var ink = new SKPaint { Color = SKColor.Parse(theme.Text).WithAlpha(theme.Dark ? (byte)22 : (byte)17), IsAntialias = true };
            foreach (var element in svg.Descendants().Where(e => e.Name.LocalName == "path"))
            {
                using var path = SKPath.ParseSvgPathData(element.Attribute("d")!.Value);
                for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    canvas.Save(); canvas.Translate(x * width, y * height); canvas.Scale(scale);
                    canvas.DrawPath(path, ink); canvas.Restore();
                }
            }
        }
        using var output = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(output))
        {
            using var filter = blur > 0 ? SKImageFilter.CreateBlur((float)blur, (float)blur, SKShaderTileMode.Clamp) : null;
            using var paint = new SKPaint { ImageFilter = filter };
            canvas.DrawBitmap(source, -width, -height, paint);
        }
        return Snapshot(output);
    }

    static Bitmap Snapshot(SKBitmap bitmap)
    {
        using var pixels = bitmap.Copy(SKColorType.Bgra8888);
        var result = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var target = result.Lock();
        var row = new byte[pixels.Width * 4];
        for (var y = 0; y < pixels.Height; y++)
        {
            Marshal.Copy(pixels.GetPixels() + y * pixels.RowBytes, row, 0, row.Length);
            Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
        }
        return result;
    }
}
