using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MessengerDesktop.Appearance;

public sealed class AppearanceWindow : Window
{
    readonly ThemeSettings _original = ThemeSettings.Current;
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _name = new() { Watermark = "Название темы" };
    readonly CheckBox _dark = new() { Content = "Тёмное оформление элементов" };
    readonly ColorPicker[] _colors = new ColorPicker[4];
    readonly List<Button> _cards = new();
    bool _loading;
    bool _saved;
    bool _closed;

    public AppearanceWindow()
    {
        Title = "Оформление";
        Width = 540;
        Height = 660;
        MinWidth = 360;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 24) };
        heading.Children.Add(new TextBlock { Text = "Оформление", FontSize = 20, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Выберите тему или создайте свою.", Classes = { "secondary" } });
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Применить", Classes = { "primary" }, Padding = new Thickness(24, 10) };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(save, 2);
        footer.Children.Add(_error);
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var body = new StackPanel { Spacing = 24 };
        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var preset in ThemeSettings.Presets)
        {
            var card = PresetCard(preset);
            _cards.Add(card);
            choices.Children.Add(card);
        }
        body.Children.Add(choices);
        body.Children.Add(ConversationPreview());
        var custom = new StackPanel { Spacing = 14, Margin = new Thickness(0, 14, 0, 0) };
        custom.Children.Add(_name);
        _name.TextChanged += (_, _) => PreviewCustom();
        custom.Children.Add(_dark);
        _dark.IsCheckedChanged += (_, _) => PreviewCustom();
        string[] labels = ["Фон", "Поверхности", "Текст", "Акцент"];
        for (int i = 0; i < _colors.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
            var picker = new ColorPicker { IsAlphaEnabled = false, Width = 150 };
            _colors[i] = picker;
            picker.ColorChanged += (_, _) => PreviewCustom();
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            custom.Children.Add(row);
        }
        body.Children.Add(new Expander { Header = "Создать свою тему", Content = custom, HorizontalAlignment = HorizontalAlignment.Stretch });
        layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        Load(_original);
        Closed += (_, _) => { _closed = true; if (!_saved) ThemeSettings.Apply(_original); };
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
            Width = 108,
            Padding = new Thickness(5),
            Margin = new Thickness(0, 0, 8, 8),
            Content = new StackPanel { Spacing = 9, Children = { thumbnail, new TextBlock { Text = preset.Name, HorizontalAlignment = HorizontalAlignment.Center } } }
        };
        card.Click += (_, _) => { Load(preset); ThemeSettings.Apply(preset); };
        return card;
    }

    static Border ConversationPreview()
    {
        var incoming = new Border
        {
            Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = "Встретимся завтра?", TextWrapping = TextWrapping.Wrap }
        };
        incoming.Bind(Border.BackgroundProperty, new DynamicResourceExtension("RaisedBrush"));
        var outgoing = new Border
        {
            Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = "Да, в семь. До встречи!", TextWrapping = TextWrapping.Wrap }
        };
        outgoing.Bind(Border.BackgroundProperty, new DynamicResourceExtension("BubbleMine"));
        var preview = new Border
        {
            Padding = new Thickness(22), CornerRadius = new CornerRadius(8),
            Child = new StackPanel { Spacing = 16, Children =
            {
                new TextBlock { Text = "Так будет выглядеть переписка", Classes = { "caption" } },
                incoming, outgoing
            }}
        };
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
        _error.Text = ThemeSettings.Contrast(_colors[2].Color, _colors[0].Color) < 4.5 || ThemeSettings.Contrast(_colors[2].Color, _colors[1].Color) < 4.5
            ? "Текст может быть плохо различим. Увеличьте разницу между цветом текста и фона." : "";
    }

    void Save()
    {
        try
        {
            ThemeSettings.Save();
            _saved = true;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = "Не удалось сохранить тему.";
        }
    }
}
