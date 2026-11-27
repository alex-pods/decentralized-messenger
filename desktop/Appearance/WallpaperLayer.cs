using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MessengerDesktop.Appearance;

// Separate non-interactive layer: blur never affects messages or controls.
public sealed class WallpaperLayer : Control
{
    Bitmap? _bitmap;
    (WallpaperSettings Settings, string? Background, string? Text, bool Dark)? _rendered;
    public WallpaperSettings? Preview { get; set; }
    public WallpaperLayer() { IsHitTestVisible = false; ClipToBounds = true; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WallpaperSettings.Changed += Refresh;
        ThemeSettings.Changed += Refresh;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WallpaperSettings.Changed -= Refresh;
        ThemeSettings.Changed -= Refresh;
        _bitmap?.Dispose(); _bitmap = null; _rendered = null;
        base.OnDetachedFromVisualTree(e);
    }

    void Refresh()
    {
        var settings = Preview ?? WallpaperSettings.Current;
        var theme = ThemeSettings.Current;
        var key = (settings, settings.Kind == "custom" ? null : theme.Background, settings.Kind == "custom" ? null : theme.Text,
            settings.Kind != "custom" && theme.Dark);
        if (_rendered != key)
        {
            _bitmap?.Dispose(); _bitmap = null; _rendered = key;
            try
            {
                if (settings.ImagePath is { } path) _bitmap = WallpaperRaster.Custom(path, settings.Blur);
                else if (settings.Kind != "none") _bitmap = WallpaperRaster.Pattern(settings.Kind, theme, settings.Blur);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var settings = Preview ?? WallpaperSettings.Current;
        var theme = ThemeSettings.Current;
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brush.Parse(theme.Background), bounds);
        if (_bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var source = new Rect(_bitmap.Size);
        using var clip = context.PushClip(bounds);
        if (settings.Kind == "custom")
        {
            var scale = Math.Max(Bounds.Width / source.Width, Bounds.Height / source.Height);
            var size = new Size(source.Width * scale, source.Height * scale);
            context.DrawImage(_bitmap, source, new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height));
            context.FillRectangle(new SolidColorBrush(Color.Parse(theme.Background), theme.Dark ? .72 : .52), bounds);
        }
        else
            for (double y = 0; y < Bounds.Height; y += source.Height)
            for (double x = 0; x < Bounds.Width; x += source.Width)
                context.DrawImage(_bitmap, source, new Rect(x, y, source.Width, source.Height));
    }
}
