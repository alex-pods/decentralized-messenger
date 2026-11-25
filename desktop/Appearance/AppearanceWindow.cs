using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MessengerDesktop.Appearance;

public sealed partial class AppearanceWindow : Window
{
    readonly ThemeSettings _original = ThemeSettings.Current;
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _name = new() { Watermark = "Название темы", MaxLength = 64 };
    readonly CheckBox _dark = new() { Content = "Тёмное оформление элементов" };
    readonly ColorPicker[] _colors = new ColorPicker[4];
    readonly List<Button> _cards = new();
    bool _loading;
    bool _saved;
    bool _closed;

    public AppearanceWindow(MessengerClient? api = null, long? owner = null)
    {
        _api = api; _owner = owner;
        Title = "Оформление";
        Width = 540;
        Height = 660;
        MinWidth = 360;
        MinHeight = 420;
        SystemDecorations = SystemDecorations.BorderOnly;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
        var title = new Border { Background = Brushes.Transparent, Child = new TextBlock { Text = "Оформление",
            FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } };
        title.PointerPressed += (_, e) => Views.MainWindow.DragFrom(title, e);
        heading.Children.Add(title);
        var close = new DismissButton();
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        heading.Children.Add(close);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 20, 0, 0) };
        var footerActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Применить", Classes = { "primary" }, Padding = new Thickness(24, 10) };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        footer.Children.Add(_error);
        _error.IsVisible = false;
        _error.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) _error.IsVisible = !string.IsNullOrWhiteSpace(_error.Text);
        };
        footerActions.Children.Add(cancel);
        footerActions.Children.Add(save);
        footer.Children.Add(footerActions);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var body = new StackPanel { Spacing = 20, HorizontalAlignment = HorizontalAlignment.Left };
        var choices = new UniformGrid { Columns = 2 };
        foreach (var preset in ThemeSettings.Presets)
        {
            var card = PresetCard(preset);
            _cards.Add(card);
            choices.Children.Add(card);
        }
        body.Children.Add(choices);
        body.Children.Add(ConversationPreview());
        body.Children.Add(WallpaperControls());
        var accents = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (hex, label) in new[] { ("#33755C", "Зелёный"), ("#3867B2", "Синий"), ("#87613B", "Песочный"), ("#9B5671", "Розовый"), ("#675BA2", "Фиолетовый"), ("#56606B", "Графит") })
        {
            var swatch = new Button { Width = 38, Height = 38, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0),
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush.Parse(hex) } };
            Avalonia.Automation.AutomationProperties.SetName(swatch, label + " акцент");
            ToolTip.SetTip(swatch, label + " акцент");
            swatch.Click += (_, _) => _colors[3].Color = Color.Parse(hex);
            accents.Children.Add(swatch);
        }
        body.Children.Add(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Цвет акцента", FontWeight = FontWeight.Medium }, accents } });
        var custom = new StackPanel { Spacing = 9, Margin = new Thickness(0, 12, 0, 0) };
        custom.Children.Add(_name);
        _name.TextChanged += (_, _) => PreviewCustom();
        custom.Children.Add(_dark);
        _dark.IsCheckedChanged += (_, _) => PreviewCustom();
        string[] labels = ["Фон", "Поверхности", "Текст", "Акцент"];
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
        scroll.SizeChanged += (_, e) =>
        {
            body.Width = Math.Max(0, e.NewSize.Width - scrollGutter);
            choices.Columns = body.Width >= 452 ? 4 : 2;
        };
        layout.Children.Add(scroll);
        Content = layout;
        Load(_original);
        Closed += (_, _) =>
        {
            _closed = true;
            if (!_saved) { ThemeSettings.Apply(_original); WallpaperSettings.Apply(_originalWallpaper); }
            WallpaperSettings.Prune();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    Button PresetCard(ThemeSettings preset)
    {
        var thumbnail = new Border
        {
            Background = Brush.Parse(preset.Background),
            CornerRadius = new CornerRadius(8),
            Height = 64,
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Border { Background = Brush.Parse(preset.Surface), Height = 14, Width = 50, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left },
                    new Border { Background = Brush.Parse(preset.Accent), Height = 14, Width = 40, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right }
                }
            }
        };
        var card = new Button
        {
            Tag = preset.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 8, 8),
            Content = new StackPanel { Spacing = 9, Children = { thumbnail, new TextBlock { Text = preset.Name, HorizontalAlignment = HorizontalAlignment.Center } } }
        };
        card.Click += (_, _) => { Load(preset); ThemeSettings.Apply(preset); };
        return card;
    }

    static Border ConversationPreview()
    {
        Border Message(string text, bool mine)
        {
            var bubble = new Border { Padding = new Thickness(12, 9), CornerRadius = new CornerRadius(10),
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
            bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension(mine ? "BubbleMine" : "RaisedBrush"));
            return bubble;
        }
        var preview = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Child = new Panel { Children = { new WallpaperLayer(), new StackPanel { Margin = new Thickness(20), Spacing = 12,
                Children = { Message("Встретимся завтра?", false), Message("Да, в семь. До встречи!", true) } } } } };
        preview.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ChassisBrush"));
        return preview;
    }

    void Load(ThemeSettings theme)
    {
        _loading = true;
        _name.Text = theme.Name;
        _dark.IsChecked = theme.Dark;
        string[] values = [theme.Background, theme.Surface, theme.Text, theme.Accent];
        for (int i = 0; i < _colors.Length; i++) _colors[i].Color = Color.Parse(values[i]);
        foreach (var card in _cards)
        {
            var selected = ThemeSettings.Presets.Contains(theme) && Equals(card.Tag, theme.Name);
            var content = (StackPanel)card.Content!;
            var thumbnail = (Border)content.Children[0];
            thumbnail.BorderThickness = new Thickness(selected ? 2 : 0);
            thumbnail.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"));
            ((TextBlock)content.Children[1]).Text = (selected ? "✓ " : "") + card.Tag;
            card.Bind(BorderBrushProperty, new DynamicResourceExtension("AccentBrush"));
        }
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
        foreach (var card in _cards)
        {
            var content = (StackPanel)card.Content!;
            ((Border)content.Children[0]).BorderThickness = new Thickness(0);
            ((TextBlock)content.Children[1]).Text = card.Tag?.ToString();
        }
        UpdateContrastWarning();
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
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _error.Text = ex is InvalidDataException ? ex.Message : "Не удалось сохранить тему.";
        }
    }
}
