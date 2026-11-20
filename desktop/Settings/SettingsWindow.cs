using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MessengerDesktop.Settings;

/// <summary>Окно настроек: приватность, отправка сообщений, двойной клик. Всё применяется сразу.</summary>
public sealed class SettingsWindow : Window
{
    readonly ShellViewModel _vm;
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly CheckBox _tagSearch = new() { Content = "Находить меня по тегу" };
    readonly WrapPanel _reactions = new() { Orientation = Orientation.Horizontal };
    bool _loading;
    bool _closed;

    public SettingsWindow(ShellViewModel vm)
    {
        _vm = vm;
        Title = "Настройки";
        Width = 520;
        Height = 640;
        MinWidth = 360;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 24) };
        heading.Children.Add(new TextBlock { Text = "Настройки", FontSize = 20, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Изменения применяются сразу.", Classes = { "secondary" } });
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("DangerBrush"));
        var close = new Button { Content = "Закрыть", Classes = { "primary" }, Padding = new Thickness(24, 10), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 20, 0, 0), Children = { _status, close } };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var prefs = AppPreferences.Current;
        var body = new StackPanel { Spacing = 28 };

        // --- Приватность ---
        _tagSearch.IsChecked = vm.AllowTagSearch;
        _tagSearch.IsCheckedChanged += async (_, _) => await ChangeTagSearch();
        body.Children.Add(Section("Приватность", "Если выключить, другие не смогут найти вас в поиске по тегу.", _tagSearch));

        // --- Отправка ---
        body.Children.Add(Section("Отправка сообщений", "Как отправлять сообщение из поля ввода.",
            Choice("send", "Enter — отправить, Shift+Enter — новая строка", prefs.SendMode == SendMode.Enter,
                () => AppPreferences.Update(p => p with { SendMode = SendMode.Enter })),
            Choice("send", "Ctrl+Enter (⌘+Enter на macOS) — отправить, Enter — новая строка", prefs.SendMode == SendMode.CtrlEnter,
                () => AppPreferences.Update(p => p with { SendMode = SendMode.CtrlEnter }))));

        // --- Двойной клик ---
        BuildReactionPicker();
        _reactions.IsEnabled = prefs.DoubleClick == DoubleClickAction.React;
        body.Children.Add(Section("Двойной клик по сообщению", "Что произойдёт, если дважды щёлкнуть по сообщению.",
            Choice("dbl", "Ничего", prefs.DoubleClick == DoubleClickAction.None, () => SetDoubleClick(DoubleClickAction.None)),
            Choice("dbl", "Поставить реакцию", prefs.DoubleClick == DoubleClickAction.React, () => SetDoubleClick(DoubleClickAction.React)),
            _reactions,
            Choice("dbl", "Ответить на сообщение", prefs.DoubleClick == DoubleClickAction.Reply, () => SetDoubleClick(DoubleClickAction.Reply))));

        // --- Оформление ---
        var appearance = new Button { Content = "Открыть оформление…", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Left };
        appearance.Click += async (_, _) => await new Appearance.AppearanceWindow(_vm.ThemeApi, _vm.CurrentUserId).ShowDialog(this);
        body.Children.Add(Section("Оформление", "Тема, цвет акцента и обмен темами.", appearance));

        layout.Children.Add(new ScrollViewer
        {
            Content = body,
            Padding = new Thickness(0, 0, 8, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        Content = layout;
        Closed += (_, _) => _closed = true;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    static StackPanel Section(string title, string hint, params Control[] content)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        section.Children.Add(new TextBlock { Text = hint, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        foreach (var control in content) section.Children.Add(control);
        return section;
    }

    static RadioButton Choice(string group, string label, bool selected, Action chosen)
    {
        var radio = new RadioButton
        {
            GroupName = group,
            IsChecked = selected,
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }
        };
        radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) chosen(); };
        return radio;
    }

    void SetDoubleClick(DoubleClickAction action)
    {
        AppPreferences.Update(p => p with { DoubleClick = action });
        _reactions.IsEnabled = action == DoubleClickAction.React;
    }

    void BuildReactionPicker()
    {
        _reactions.Children.Clear();
        _reactions.Margin = new Thickness(28, 0, 0, 4);
        foreach (var emoji in ReactionCatalog.Main.Concat(ReactionCatalog.More))
        {
            var selected = emoji == AppPreferences.Current.DoubleClickReaction;
            var button = new Button
            {
                Width = 40,
                Height = 40,
                Padding = new Thickness(7),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Content = new ReactionIcon { Emoji = emoji, Width = 22, Height = 22 }
            };
            button.Bind(BorderBrushProperty, new DynamicResourceExtension(selected ? "AccentBrush" : "LineBrush"));
            if (selected) button.Bind(BackgroundProperty, new DynamicResourceExtension("AccentFill"));
            ToolTip.SetTip(button, emoji);
            AutomationProperties.SetName(button, "Реакция " + emoji + (selected ? ", выбрано" : ""));
            button.Click += (_, _) =>
            {
                AppPreferences.Update(p => p with { DoubleClickReaction = emoji });
                BuildReactionPicker();
            };
            _reactions.Children.Add(button);
        }
    }

    async Task ChangeTagSearch()
    {
        if (_loading) return;
        var wanted = _tagSearch.IsChecked == true;
        _tagSearch.IsEnabled = false;
        _status.Text = "";
        var error = await _vm.SetTagSearchAsync(wanted);
        if (_closed) return;
        if (error != null)
        {
            _loading = true;
            _tagSearch.IsChecked = !wanted;
            _loading = false;
            _status.Text = error;
        }
        _tagSearch.IsEnabled = true;
    }
}
