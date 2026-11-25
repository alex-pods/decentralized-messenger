using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
                Expect(position.X >= -0.5 && position.X + section.Bounds.Width <= scroll.Bounds.Width + 0.5,
                    $"Wallpaper settings exceed compact viewport: x={position.X:0.##}, width={section.Bounds.Width:0.##}, viewport={scroll.Bounds.Width:0.##}");
                var slider = section.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "WallpaperBlur");
                var track = slider.GetVisualDescendants().OfType<Track>().Single();
                var thumb = track.Thumb!;
                var min = thumb.TranslatePoint(new Point(), slider)!.Value.X;
                Expect(track.Bounds.Width > 180 && min is >= 0 and <= 10 && min + thumb.Bounds.Width <= slider.Bounds.Width,
                    "Compact blur slider rail or left endpoint is clipped");
                ExpectRangeWithinSlot(slider);
                slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                Expect(slider.Value == 1 && WallpaperSettings.Current.Blur == 1, "Slider keyboard did not move one step");
                slider.Value = slider.Maximum;
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var slider = dialog.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "WallpaperBlur");
                var thumb = slider.GetVisualDescendants().OfType<Track>().Single().Thumb!;
                var max = thumb.TranslatePoint(new Point(), slider)!.Value.X + thumb.Bounds.Width;
                Expect(slider.Bounds.Width - max is >= 0 and <= 10, "Compact blur slider right endpoint is clipped");
                ExpectRangeWithinSlot(slider);
                slider.Value = 12;
            });
            await Shot(dialog, $"wallpaper-settings-compact-{mode}");
            await Dispatcher.UIThread.InvokeAsync(() => dialog.Height = 420);
            await Until(() => dialog.ClientSize.Height < 450, "Appearance window did not resize to its minimum height");
            var shortSlider = await Dispatcher.UIThread.InvokeAsync(() =>
                dialog.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "WallpaperBlur"));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var scroll = shortSlider.FindAncestorOfType<ScrollViewer>()!;
                var viewport = shortSlider.FindAncestorOfType<ScrollContentPresenter>()!;
                var sliderY = shortSlider.TranslatePoint(new Point(), viewport)!.Value.Y;
                scroll.Offset = new Vector(0, scroll.Offset.Y + sliderY - 90);
            });
            await Until(() =>
            {
                var viewport = shortSlider.FindAncestorOfType<ScrollContentPresenter>()!;
                var origin = shortSlider.TranslatePoint(new Point(), viewport)!.Value;
                return dialog.ClientSize.Height < 450 && origin.Y >= 0 && origin.Y + shortSlider.Bounds.Height <= viewport.Bounds.Height;
            }, "Blur control is not fully reachable above the footer in the short window");
            await Dispatcher.UIThread.InvokeAsync(() => ExpectRangeWithinSlot(shortSlider));
            await Task.Delay(150);
            await Shot(dialog, $"wallpaper-settings-short-{mode}");
            await AuditCompactColorEditor(dialog, mode);
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

    static async Task AuditCompactColorEditor(AppearanceWindow dialog, string mode)
    {
        var custom = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var section = dialog.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Создать свою тему"));
            section.IsExpanded = true;
            return section;
        });
        await Until(() => custom.GetVisualDescendants().OfType<ColorPicker>().Any(), "Custom colors are not visible");
        var picker = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var input = custom.GetVisualDescendants().OfType<ColorPicker>().Single(c => Equals(c.Content, "Акцент"));
            input.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            return input;
        });
        await Until(() => picker.GetVisualDescendants().OfType<ColorSlider>().Any(s => s.Bounds.Width > 100),
            "Inline theme editor did not open");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var slider = picker.GetVisualDescendants().OfType<ColorSlider>().Single();
            var viewport = slider.FindAncestorOfType<ScrollContentPresenter>()!;
            var x = slider.TranslatePoint(new Point(), viewport)!.Value.X;
            Expect(x >= -.5 && x + slider.Bounds.Width <= viewport.Bounds.Width + .5,
                "Hue slider runs outside the inline theme editor");
            ExpectRangeWithinSlot(slider);
            var hex = picker.GetVisualDescendants().OfType<TextBox>().Single();
            hex.Text = "#8855BB";
            hex.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent });
            Expect(ThemeSettings.Current.Accent == "#8855BB", "Editing HEX did not update the theme");
            var hue = slider.Value;
            slider.Value = (hue + 60) % 360;
            Expect(ThemeSettings.Current.Accent != "#8855BB", "Changing hue did not update the theme");
            ExpectRangeWithinSlot(slider);
            var scroll = slider.FindAncestorOfType<ScrollViewer>()!;
            var pickerTop = picker.TranslatePoint(new Point(), viewport)!.Value.Y;
            scroll.Offset = new Vector(0, scroll.Offset.Y + pickerTop - 6);
        });
        await Until(() =>
        {
            var hex = picker.GetVisualDescendants().OfType<TextBox>().Single();
            var viewport = hex.FindAncestorOfType<ScrollContentPresenter>()!;
            var hue = picker.GetVisualDescendants().OfType<ColorSlider>().Single();
            return new Control[] { hex, hue }.All(control =>
            {
                var origin = control.TranslatePoint(new Point(), viewport)!.Value;
                return origin.X >= 0 && origin.Y >= 0 && origin.Y + control.Bounds.Height <= viewport.Bounds.Height &&
                       origin.X + control.Bounds.Width <= viewport.Bounds.Width;
            });
        }, "Color editor controls do not fit above the footer at minimum window height");
        await Shot(dialog, $"appearance-color-editor-short-{mode}");
    }

    static void ExpectRangeWithinSlot(Slider slider)
    {
        var track = slider.GetVisualDescendants().OfType<Track>().Single();
        var rail = slider.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RangeRail");
        var handle = slider.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RangeHandle");
        var accent = Color.Parse(ThemeSettings.Current.Accent);
        Expect(rail.Bounds.Width >= track.Bounds.Width - 21 &&
               handle.Background is ISolidColorBrush handleBrush && handleBrush.Color == accent &&
               track.DecreaseButton!.Background is ISolidColorBrush fillBrush && fillBrush.Color == accent,
            $"{slider.GetType().Name} rail and handle do not match the current theme");
        foreach (var visual in new Control[] { track, track.Thumb! })
        {
            var origin = visual.TranslatePoint(new Point(), slider)!.Value;
            Expect(origin.X >= -0.5 && origin.Y >= -0.5 &&
                   origin.X + visual.Bounds.Width <= slider.Bounds.Width + 0.5 &&
                   origin.Y + visual.Bounds.Height <= slider.Bounds.Height + 0.5,
                $"{slider.GetType().Name} {visual.GetType().Name} paints outside its slot in the compact window");
        }
    }
}
