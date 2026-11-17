using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MessengerDesktop.Appearance;

public sealed class AppearanceWindow : Window
{
    public AppearanceWindow()
    {
        Title = "Оформление";
        Width = 420;
        Height = 590;
        MinWidth = 340;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var original = ThemeSettings.Current;
        bool saved = false;
        var presets = new ComboBox { ItemsSource = ThemeSettings.Presets.Select(p => p.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var dark = new CheckBox { Content = "Тёмные системные элементы", IsChecked = original.Dark };
        var background = new TextBox { Text = original.Background };
        var surface = new TextBox { Text = original.Surface };
        var text = new TextBox { Text = original.Text };
        var accent = new TextBox { Text = original.Accent };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var preview = new Button { Content = "Предпросмотр" };
        var save = new Button { Content = "Сохранить", Classes = { "primary" } };
        var cancel = new Button { Content = "Отмена" };
        bool Apply()
        {
            foreach (var field in new[] { background, surface, text, accent })
            {
                if (field.Text is not { Length: 7 } value || value[0] != '#' || !Color.TryParse(value, out _))
                {
                    error.Text = "Введите цвет в формате #RRGGBB.";
                    return false;
                }
            }
            ThemeSettings.Apply(new("Своя тема", dark.IsChecked == true, background.Text!, surface.Text!, text.Text!, accent.Text!));
            error.Text = "";
            return true;
        }
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedIndex < 0) return;
            var value = ThemeSettings.Presets[presets.SelectedIndex];
            background.Text = value.Background;
            surface.Text = value.Surface;
            text.Text = value.Text;
            accent.Text = value.Accent;
            dark.IsChecked = value.Dark;
            ThemeSettings.Apply(value);
        };
        preview.Click += (_, _) => Apply();
        save.Click += (_, _) =>
        {
            if (!Apply()) return;
            try { ThemeSettings.Save(); saved = true; Close(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error.Text = "Не удалось сохранить оформление. Проверьте доступ к папке настроек."; }
        };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => { if (!saved) ThemeSettings.Apply(original); };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "Оформление", FontSize = 24, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(presets);
        panel.Children.Add(dark);
        foreach (var entry in new[] { ("Фон приложения", background), ("Фон переписки и панелей", surface), ("Основной текст", text), ("Акцент", accent) })
        {
            panel.Children.Add(new TextBlock { Text = entry.Item1, FontSize = 13 });
            panel.Children.Add(entry.Item2);
        }
        panel.Children.Add(error);
        panel.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel };
    }
}
