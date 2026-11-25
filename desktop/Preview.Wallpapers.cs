using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;
using SkiaSharp;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditWallpapers(Window window, ShellViewModel vm)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.CloseOverlayCommand.Execute(null);
            vm.SelectedChat = vm.Chats.First(c => c.Title.StartsWith("Максим"));
            window.WindowState = WindowState.Normal; Resize(window, 1180); window.Height = 780;
        });
        await Until(() => !vm.Busy && vm.Messages.Count > 3, "Wallpaper conversation did not load");

        // Use a deterministic synthetic photo-like raster; no user's private pictures in tests/artifacts.
        using var bitmap = new SKBitmap(2400, 1600);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColor.Parse("#c4d4dc"));
            using var paint = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#f6e5b9") };
            canvas.DrawCircle(1770, 390, 170, paint);
            using var mountains = new SKPath();
            mountains.MoveTo(0, 1000); mountains.LineTo(500, 460); mountains.LineTo(1200, 1130);
            mountains.LineTo(1750, 640); mountains.LineTo(2400, 1100); mountains.LineTo(2400, 1600);
            mountains.LineTo(0, 1600); mountains.Close();
            paint.Color = SKColor.Parse("#647c87"); canvas.DrawPath(mountains, paint);
            paint.Color = SKColor.Parse("#344d59"); canvas.DrawOval(1100, 1910, 1900, 780, paint);
        }
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var originalFile = Path.Combine(OutDir, "wallpaper-test-source.png");
        File.WriteAllBytes(originalFile, encoded.ToArray());
        WallpaperSettings imported;
        using (var stream = File.OpenRead(originalFile)) imported = await WallpaperSettings.Import(stream);
        File.Delete(originalFile);
        Expect(imported.ImagePath != null && File.Exists(imported.ImagePath), "Wallpaper depends on deleted source file");
        using (var normalized = SKBitmap.Decode(imported.ImagePath))
            Expect(normalized.Width == 2048 && normalized.Height <= 2048, "Wallpaper was not downsampled");
        foreach (var bytes in new[] { new byte[] { 1, 2, 3 }, new byte[WallpaperSettings.MaxBytes + 1] })
        {
            try { using var stream = new MemoryStream(bytes); await WallpaperSettings.Import(stream); throw new Exception("Invalid wallpaper accepted"); }
            catch (InvalidDataException) { }
        }
        Expect(WallpaperSettings.Normalize(new("custom", "../outside", 10)).Kind == "none", "Wallpaper path traversal accepted");
        Expect(WallpaperSettings.Normalize(new("topography", Blur: double.NaN)).Blur == 0, "Invalid wallpaper blur accepted");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var sharp = WallpaperRaster.Custom(imported.ImagePath!, 0);
            using var blurred = WallpaperRaster.Custom(imported.ImagePath!, 12);
            using var sharpBytes = new MemoryStream(); using var blurBytes = new MemoryStream();
            sharp.Save(sharpBytes); blurred.Save(blurBytes);
            Expect(!sharpBytes.ToArray().SequenceEqual(blurBytes.ToArray()), "Blur did not change raster pixels");
            using var result = SKBitmap.Decode(blurBytes.ToArray());
            Expect(result.GetPixel(0, 0).Alpha == 255 && result.GetPixel(result.Width - 1, result.Height - 1).Alpha == 255,
                "Blur created translucent image edges");
        });

        await Dispatcher.UIThread.InvokeAsync(() => { WallpaperSettings.Apply(imported with { Blur = 12 }); WallpaperSettings.Save(); WallpaperSettings.Apply(new()); WallpaperSettings.Restore(); });
        Expect(WallpaperSettings.Current == imported with { Blur = 12 }, "Wallpaper persistence failed");
        foreach (var theme in ThemeSettings.Presets.Take(2))
        {
            var mode = theme.Dark ? "dark" : "light";
            await Dispatcher.UIThread.InvokeAsync(() => ThemeSettings.Apply(theme));
            foreach (var preset in WallpaperSettings.Presets.Skip(1))
            {
                await Dispatcher.UIThread.InvokeAsync(() => WallpaperSettings.Apply(new(preset.Id)));
                await Task.Delay(100);
                await Shot(window, $"wallpaper-{preset.Id}-{mode}");
            }
            foreach (var blur in new[] { 0, 12 })
            {
                await Dispatcher.UIThread.InvokeAsync(() => WallpaperSettings.Apply(imported with { Blur = blur }));
                await Task.Delay(150);
                await Shot(window, $"wallpaper-custom-{blur}-{mode}");
                var layer = await Dispatcher.UIThread.InvokeAsync(() => window.GetVisualDescendants().OfType<WallpaperLayer>().First());
                await Dispatcher.UIThread.InvokeAsync(() => Expect(layer.Effect == null, "Blur affects controls"));
            }
            var dialog = await Dispatcher.UIThread.InvokeAsync(() => { var next = new AppearanceWindow(); next.Show(window); return next; });
            await Task.Delay(120);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Wallpaper_topography").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "WallpaperBlur").Value = 0;
                var section = dialog.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "WallpaperSection");
                dialog.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, section.Bounds.Y);
            });
            await Task.Delay(150);
            await Shot(dialog, $"wallpaper-settings-{mode}");
            await Dispatcher.UIThread.InvokeAsync(() => dialog.Width = 360);
            await Task.Delay(200);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var section = dialog.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "WallpaperSection");
                var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroll.Offset = new Vector(0, section.Bounds.Y);
                var position = section.TranslatePoint(new Point(), scroll)!.Value;
                Expect(position.X >= 0 && position.X + section.Bounds.Width <= scroll.Bounds.Width, "Wallpaper settings exceed compact viewport");
            });
            await Shot(dialog, $"wallpaper-settings-compact-{mode}");
            await Dispatcher.UIThread.InvokeAsync(dialog.Close);
            Expect(WallpaperSettings.Current == imported with { Blur = 12 }, "Cancel did not restore wallpaper and blur");
        }
        var saveDialog = await Dispatcher.UIThread.InvokeAsync(() => { var next = new AppearanceWindow(); next.Show(window); next.SelectWallpaper(new("jupiter", Blur: 3)); return next; });
        await Dispatcher.UIThread.InvokeAsync(() => saveDialog.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "Применить")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Expect(WallpaperSettings.Read() == new WallpaperSettings("jupiter", Blur: 3), "Apply did not persist wallpaper");
        Expect(!File.Exists(imported.ImagePath), "Unused imported wallpaper was not pruned");
        await Dispatcher.UIThread.InvokeAsync(() => { WallpaperSettings.Apply(new()); WallpaperSettings.Save(); ThemeSettings.Apply(ThemeSettings.Presets[0]); });
        Console.WriteLine("PASS wallpapers: built-ins, custom import, limits, local copy, downsampling, persistence, cancel/apply, cleanup, blur, light/dark and compact settings");
    }
}
