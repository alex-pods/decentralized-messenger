using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MessengerDesktop.Appearance;

public sealed partial class AppearanceEditor : UserControl
{
    readonly ThemeSettings _original = ThemeSettings.Current;
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _name = new() { Watermark = "Название темы", MaxLength = 64 };
    readonly CheckBox _dark = new() { Content = "Тёмные кнопки и меню" };
    readonly TextBlock _activeAccent = new() { Classes = { "caption" } };
    readonly ColorPicker[] _colors = new ColorPicker[4];
    readonly List<Button> _cards = new();
    readonly List<Button> _accentChips = new();
    bool _loading;
    bool _saved;
    bool _closed;

    internal event Action? CloseRequested;
    Avalonia.Platform.Storage.IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)!.StorageProvider;
    Avalonia.Input.Platform.IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    public AppearanceEditor(MessengerClient? api = null, long? owner = null, bool standalone = false)
    {
        _api = api; _owner = owner;
        var layout = new DockPanel { Margin = standalone ? new Thickness(24) : default };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var title = new Border { Background = Brushes.Transparent, Child = new TextBlock { Text = "Оформление",
            FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } };
        title.PointerPressed += (_, e) => Views.MainWindow.DragFrom(title, e);
        if (standalone) heading.Children.Add(title);
        var preview = ConversationPreview();
        preview.Name = "ThemePreview";
        preview.IsVisible = false;
        preview.Margin = new Thickness(0, 0, 0, 8);
        var previewToggle = new ToggleButton { Name = "ToggleThemePreview", Content = "Предпросмотр", Padding = new Thickness(8, 4), Margin = new Thickness(4, 0, 6, 0) };
        Avalonia.Automation.AutomationProperties.SetName(previewToggle, "Предпросмотр переписки");
        ToolTip.SetTip(previewToggle, "Предпросмотр переписки");
        previewToggle.IsCheckedChanged += (_, _) => preview.IsVisible = previewToggle.IsChecked == true;
        Grid.SetColumn(previewToggle, 1);
        heading.Children.Add(previewToggle);
        var close = new DismissButton();
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 2);
        if (standalone) heading.Children.Add(close);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        DockPanel.SetDock(preview, Dock.Top);
        layout.Children.Add(preview);

        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        var footerActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var cancel = new Button { Content = "Отмена", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var save = new Button { Content = "Применить", Classes = { "themeCommit" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Click += (_, _) => CloseRequested?.Invoke();
        save.Click += (_, _) => Save();
        footer.Children.Add(_error);
        _error.IsVisible = false;
        _error.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _error.IsVisible = !string.IsNullOrWhiteSpace(_error.Text);
        };
        footerActions.Children.Add(cancel);
        Grid.SetColumn(save, 2);
        footerActions.Children.Add(save);
        footer.Children.Add(footerActions);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var body = new StackPanel { Spacing = 15, HorizontalAlignment = HorizontalAlignment.Left };
        var choices = new UniformGrid { Columns = 2 };
        foreach (var preset in ThemeSettings.Presets)
        {
            var card = PresetCard(preset);
            _cards.Add(card);
            choices.Children.Add(card);
        }
        body.Children.Add(choices);
        body.Children.Add(WallpaperControls());
        var accents = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (hex, label) in new[] { ("#33755C", "Зелёный"), ("#3867B2", "Синий"), ("#87613B", "Песочный"), ("#9B5671", "Розовый"), ("#675BA2", "Фиолетовый"), ("#56606B", "Графит") })
        {
            var choice = Color.Parse(hex);
            var tick = new Glyph { Classes = { "swatchCheck" }, Width = 14, Height = 14, StrokeWidth = 2,
                Data = (Geometry)Application.Current!.FindResource("IcCheck")!,
                Foreground = ThemeSettings.Contrast(choice, Colors.White) >= ThemeSettings.Contrast(choice, Colors.Black)
                    ? Brushes.White : Brushes.Black };
            var swatch = new Button { Tag = hex, Classes = { "themeAccent" }, Width = 37, Height = 37, Padding = new Thickness(5), Margin = new Thickness(0, 0, 6, 0),
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(choice), Child = tick } };
            Avalonia.Automation.AutomationProperties.SetName(swatch, label + " акцент");
            ToolTip.SetTip(swatch, label + " акцент");
            swatch.Click += (_, _) => _colors[3].Color = choice;
            _accentChips.Add(swatch);
            accents.Children.Add(swatch);
        }
        body.Children.Add(new StackPanel { Spacing = 8, Children =
        {
            new TextBlock { Text = "Цвет акцента", FontWeight = FontWeight.Medium },
            accents, _activeAccent
        } });
        var custom = new StackPanel { Spacing = 9, Margin = new Thickness(0, 12, 0, 0) };
        custom.Children.Add(_name);
        _name.TextChanged += (_, _) => PreviewCustom();
        custom.Children.Add(_dark);
        _dark.IsCheckedChanged += (_, _) => PreviewCustom();
        string[] labels = ["Фон приложения", "Панели и окна", "Текст", "Акцент"];
        for (int i = 0; i < _colors.Length; i++)
        {
            var picker = new ColorPicker { Classes = { "themePicker" }, Content = labels[i], IsAlphaEnabled = false };
            Avalonia.Automation.AutomationProperties.SetName(picker, labels[i]);
            _colors[i] = picker;
            picker.ColorChanged += (_, _) => PreviewCustom();
            custom.Children.Add(picker);
        }
        body.Children.Add(new Expander { Header = "Создать свою тему", Content = custom, Classes = { "themeDesigner" },
            HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) });
        body.Children.Add(SharingControls());
        var scroll = new ScrollViewer { Content = body, ClipToBounds = true, AllowAutoHide = false,
            Classes = { "dialogScroll" },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var scrollGutter = (double)Application.Current!.FindResource("DialogScrollBarWidth")!;
        SizeChanged += (_, e) =>
        {
            var compactHeight = e.NewSize.Height <= 460;
            if (standalone) layout.Margin = new Thickness(24, compactHeight ? 12 : 24);
            heading.Margin = new Thickness(0, 0, 0, compactHeight ? 8 : 12);
            footer.Margin = new Thickness(0, compactHeight ? 8 : 12, 0, 0);
        };
        scroll.SizeChanged += (_, e) =>
        {
            body.Width = Math.Max(0, e.NewSize.Width - scrollGutter);
            choices.Columns = body.Width >= 452 ? 4 : 2;
            _wallpaperChoices.Columns = choices.Columns;
        };
        layout.Children.Add(scroll);
        Content = layout;
        Load(_original);
        DetachedFromVisualTree += (_, _) => EndEditing();
    }

    internal void EndEditing()
    {
        if (_closed) return;
        _closed = true;
        if (!_saved) { ThemeSettings.Apply(_original); WallpaperSettings.Apply(_originalWallpaper); }
        WallpaperSettings.Prune();
    }

    Button PresetCard(ThemeSettings preset)
    {
        var swatches = new UniformGrid { Columns = 3 };
        foreach (var tone in new[] { preset.Surface, preset.Text, preset.Accent })
            swatches.Children.Add(new Border { Background = Brush.Parse(tone), CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0) });
        var thumbnail = new Border
        {
            Background = Brush.Parse(preset.Background),
            CornerRadius = new CornerRadius(5),
            Height = 36,
            Padding = new Thickness(6, 8),
            Child = swatches
        };
        var card = new Button
        {
            Tag = preset.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 8, 8),
            Content = new StackPanel { Spacing = 4, Children = { thumbnail, new TextBlock { Text = preset.Name, HorizontalAlignment = HorizontalAlignment.Center } } }
        };
        card.Click += (_, _) => { Load(preset); ThemeSettings.Apply(preset); };
        return card;
    }

    static Border ConversationPreview()
    {
        Border Message(string text, bool mine)
        {
            var bubble = new Border { Padding = new Thickness(7, 3), CornerRadius = new CornerRadius(6),
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis } };
            bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension(mine ? "BubbleMine" : "BubbleTheirs"));
            return bubble;
        }
        var incoming = Message("Созвонимся?", false);
        incoming.Margin = new Thickness(0, 0, 4, 0);
        var outgoing = Message("Да, в семь.", true);
        Grid.SetColumn(outgoing, 1);
        var messages = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(6),
            Children = { incoming, outgoing } };
        var preview = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = new Panel { Children = { new WallpaperLayer(), messages } } };
        preview.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ChassisBrush"));
        preview.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("LineBrush"));
        return preview;
    }

    void Load(ThemeSettings theme)
    {
        _loading = true;
        _name.Text = theme.Name;
        _dark.IsChecked = theme.Dark;
        string[] values = [theme.Background, theme.Surface, theme.Text, theme.Accent];
        for (int i = 0; i < _colors.Length; i++) _colors[i].Color = Color.Parse(values[i]);
        MarkThemeChoices(theme);
        _loading = false;
        UpdateContrastWarning();
    }

    void PreviewCustom()
    {
        if (_closed || _loading || _colors.Any(c => c == null)) return;
        static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var next = new ThemeSettings(
            string.IsNullOrWhiteSpace(_name.Text) ? "Своя тема" : _name.Text.Trim(),
            _dark.IsChecked == true, Hex(_colors[0].Color), Hex(_colors[1].Color), Hex(_colors[2].Color), Hex(_colors[3].Color));
        if (next == ThemeSettings.Current) return;
        ThemeSettings.Apply(next);
        MarkThemeChoices(next);
        UpdateContrastWarning();
    }

    void MarkThemeChoices(ThemeSettings theme)
    {
        var basePreset = ThemeSettings.Presets.FirstOrDefault(preset => preset.Dark == theme.Dark &&
            string.Equals(preset.Background, theme.Background, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(preset.Surface, theme.Surface, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(preset.Text, theme.Text, StringComparison.OrdinalIgnoreCase));
        foreach (var card in _cards)
        {
            var selected = Equals(card.Tag, basePreset?.Name);
            card.Classes.Set("themePreset", true);
            card.Classes.Set("active", selected);
        }
        string? label = null;
        foreach (var chip in _accentChips)
        {
            var selected = string.Equals((string?)chip.Tag, theme.Accent, StringComparison.OrdinalIgnoreCase);
            chip.Classes.Set("active", selected);
            var name = ToolTip.GetTip(chip)?.ToString() ?? theme.Accent;
            Avalonia.Automation.AutomationProperties.SetName(chip, selected ? name + ", выбран" : name);
            if (selected) label = name;
        }
        _activeAccent.Text = label is null ? $"Свой цвет · {theme.Accent}" : $"{label} · {theme.Accent}";
    }

    void UpdateContrastWarning()
    {
        _error.Text = ThemeSettings.Contrast(_colors[2].Color, _colors[0].Color) < 4.5 || ThemeSettings.Contrast(_colors[2].Color, _colors[1].Color) < 4.5
            ? "Текст может быть плохо различим. Увеличьте разницу между цветом текста и фона." : "";
    }

    void Save()
    {
        try
        {
            ThemeExchange.Validate(ThemeSettings.Current);
            if (_importing) { _error.Text = "Дождитесь загрузки изображения."; return; }
            var previous = WallpaperSettings.Read();
            WallpaperSettings.Save();
            try { ThemeSettings.Save(); }
            catch { WallpaperSettings.Save(previous); throw; }
            _saved = true;
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _error.Text = ex is InvalidDataException ? ex.Message : "Не удалось сохранить тему.";
        }
    }
}
