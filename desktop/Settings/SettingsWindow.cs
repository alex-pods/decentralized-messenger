using Avalonia.Controls;
using Avalonia.Input;

namespace MessengerDesktop.Settings;

public sealed partial class SettingsWindow : Window
{
    readonly ShellViewModel _vm = null!;
    bool _loading;
    bool _closed;

    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(ShellViewModel vm, string? section = null) : this()
    {
        _vm = vm;
        Opened += (_, _) =>
        {
            if (section != null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Control>(section)?.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
        };
        var prefs = AppPreferences.Current;
        ControlEnterLabel.Text = OperatingSystem.IsMacOS() ? "⌘ + Enter" : "Ctrl + Enter";
        TagSearch.IsChecked = vm.AllowTagSearch;
        BindChoice(SendEnter, prefs.SendMode == SendMode.Enter,
            () => AppPreferences.Update(p => p with { SendMode = SendMode.Enter }));
        BindChoice(SendControlEnter, prefs.SendMode == SendMode.CtrlEnter,
            () => AppPreferences.Update(p => p with { SendMode = SendMode.CtrlEnter }));
        BindChoice(DoubleNone, prefs.DoubleClick == DoubleClickAction.None, () => SetDoubleClick(DoubleClickAction.None));
        BindChoice(DoubleReply, prefs.DoubleClick == DoubleClickAction.Reply, () => SetDoubleClick(DoubleClickAction.Reply));
        BindChoice(DoubleReact, prefs.DoubleClick == DoubleClickAction.React, () => SetDoubleClick(DoubleClickAction.React));
        Reactions.Select(prefs.DoubleClickReaction);
        Reactions.IsVisible = prefs.DoubleClick == DoubleClickAction.React;
        Reactions.Picked += emoji =>
        {
            AppPreferences.Update(p => p with { DoubleClickReaction = emoji });
            Reactions.Select(emoji);
        };
        TagSearch.IsCheckedChanged += async (_, _) => await ChangeTagSearch();
        Done.Click += (_, _) => Close();
        Appearance.Click += async (_, _) => await new Appearance.AppearanceWindow(vm.ThemeApi, vm.CurrentUserId).ShowDialog(this);
        AppPreferences.Changed += ShowSaveStatus;
        ShowSaveStatus();
        Closed += (_, _) => { _closed = true; AppPreferences.Changed -= ShowSaveStatus; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    void ShowSaveStatus() => SaveStatus.Text = AppPreferences.SaveError;

    static void BindChoice(RadioButton radio, bool selected, Action chosen)
    {
        radio.IsChecked = selected;
        radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) chosen(); };
    }

    void SetDoubleClick(DoubleClickAction action)
    {
        AppPreferences.Update(p => p with { DoubleClick = action });
        Reactions.IsVisible = action == DoubleClickAction.React;
    }

    async Task ChangeTagSearch()
    {
        if (_loading) return;
        var wanted = TagSearch.IsChecked == true;
        TagSearch.IsEnabled = false;
        PrivacyStatus.Text = null;
        var error = await _vm.SetTagSearchAsync(wanted);
        if (_closed) return;
        if (error != null)
        {
            _loading = true;
            TagSearch.IsChecked = _vm.AllowTagSearch;
            _loading = false;
            PrivacyStatus.Text = error;
        }
        TagSearch.IsEnabled = true;
    }
}
