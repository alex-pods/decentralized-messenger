using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MessengerDesktop.Appearance;

public sealed partial class AppearanceWindow
{
    readonly WallpaperSettings _originalWallpaper = WallpaperSettings.Current;
    readonly List<Button> _wallpaperCards = new();
    readonly Slider _blur = new() { Name = "WallpaperBlur", Classes = { "messengerRange" },
        Minimum = 0, Maximum = 20, TickFrequency = 1, SmallChange = 1, IsSnapToTickEnabled = true };
    readonly TextBlock _blurLabel = new() { Classes = { "secondary" }, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _wallpaperStatus = new() { Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap };
    bool _importing;

    Control WallpaperControls()
    {
        var section = new StackPanel { Name = "WallpaperSection", Spacing = 12 };
        section.Children.Add(new TextBlock { Text = "Фон переписки", FontSize = 16, FontWeight = FontWeight.SemiBold });
        var choices = new WrapPanel();
        foreach (var (id, label) in WallpaperSettings.Presets)
        {
            var thumb = new Border { Height = 62, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Child = new WallpaperLayer { Preview = new(id) } };
            var card = new Button { Name = "Wallpaper_" + id.Replace('-', '_'), Tag = id, Width = 108,
                Padding = new Thickness(5), Margin = new Thickness(0, 0, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { thumb, new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center } } } };
            Avalonia.Automation.AutomationProperties.SetName(card, label);
            card.Click += (_, _) => SelectWallpaper(new(id, Blur: WallpaperSettings.Current.Blur));
            _wallpaperCards.Add(card);
            choices.Children.Add(card);
        }
        section.Children.Add(choices);
        var choose = new Button { Name = "ChooseWallpaper", Content = "Выбрать изображение…", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += async (_, _) =>
        {
            if (_importing) return;
            _importing = true; choose.IsEnabled = false;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Фон переписки", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Изображения") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"], MimeTypes = ["image/png", "image/jpeg", "image/webp"] }]
                });
                if (files.Count == 0 || _closed) return;
                using var file = files[0];
                await using var stream = await file.OpenReadAsync();
                var next = await WallpaperSettings.Import(stream);
                if (!_closed) { SelectWallpaper(next with { Blur = WallpaperSettings.Current.Blur }); _error.Text = ""; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { if (!_closed) _error.Text = ex is InvalidDataException ? ex.Message : "Не удалось загрузить фон. Попробуйте другое изображение."; }
            finally { _importing = false; choose.IsEnabled = true; if (_closed) WallpaperSettings.Prune(); }
        };
        section.Children.Add(choose);
        section.Children.Add(_wallpaperStatus);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = "Размытие", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_blurLabel, 1); row.Children.Add(_blurLabel);
        Avalonia.Automation.AutomationProperties.SetName(_blur, "Размытие фона");
        _blur.ValueChanged += (_, _) =>
        {
            if (!_closed && !_loadingWallpaper) SelectWallpaper(WallpaperSettings.Current with { Blur = _blur.Value });
        };
        var blurControl = new Border { Name = "WallpaperBlurField", Padding = new Thickness(12, 9, 12, 2), CornerRadius = new CornerRadius(8),
            Child = new StackPanel { Spacing = 2, Children = { row, _blur } } };
        blurControl.Bind(Border.BackgroundProperty, new DynamicResourceExtension("FieldFill"));
        section.Children.Add(blurControl);
        section.Children.Add(new TextBlock { Text = "PNG, JPEG или WebP · до 20 МБ. Фон сохраняется на этом устройстве и не входит в ссылку на цветовую тему.",
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        section.Children.Add(new TextBlock { Text = "Узоры: Hero Patterns · Steve Schoger · CC BY 4.0", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        RefreshWallpaperControls();
        return section;
    }

    bool _loadingWallpaper;
    internal void SelectWallpaper(WallpaperSettings settings)
    {
        WallpaperSettings.Apply(settings);
        RefreshWallpaperControls();
    }

    void RefreshWallpaperControls()
    {
        _loadingWallpaper = true;
        var current = WallpaperSettings.Current;
        foreach (var card in _wallpaperCards)
        {
            var thumbnail = (Border)((StackPanel)card.Content!).Children[0];
            var selected = Equals(card.Tag, current.Kind);
            thumbnail.BorderThickness = new Thickness(selected ? 2 : 1);
            thumbnail.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(selected ? "AccentBrush" : "LineBrush"));
        }
        _blur.Value = current.Blur;
        _blur.IsEnabled = current.Kind != "none";
        _blurLabel.Text = current.Blur == 0 ? "Без размытия" : $"{current.Blur:0} / 20";
        _wallpaperStatus.Text = current.Kind == "custom" ? "Ваше изображение · сохранена копия" : "Можно поставить своё фото или рисунок.";
        _loadingWallpaper = false;
    }
}
