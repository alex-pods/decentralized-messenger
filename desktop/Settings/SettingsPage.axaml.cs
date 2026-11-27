using Avalonia.Controls;
using Avalonia.Threading;

namespace MessengerDesktop.Settings;

public sealed partial class SettingsPage : UserControl
{
    readonly ShellViewModel _vm = null!;
    bool _loading;
    bool _opened;

    public SettingsPage() => InitializeComponent();

    public SettingsPage(ShellViewModel vm, string? section = null) : this()
    {
        _vm = vm;
        DataContext = vm;
        AttachedToVisualTree += (_, _) =>
        {
            AppPreferences.Changed += ShowSaveStatus;
            ShowSaveStatus();
            if (!_opened && section != null)
                Dispatcher.UIThread.Post(() => this.FindControl<Control>(section)?.BringIntoView(), DispatcherPriority.Loaded);
            _opened = true;
        };
        DetachedFromVisualTree += (_, _) => AppPreferences.Changed -= ShowSaveStatus;
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
        CurrentReaction.Emoji = prefs.DoubleClickReaction;
        ReactionChoices.IsVisible = prefs.DoubleClick == DoubleClickAction.React;
        Reactions.Picked += emoji =>
        {
            AppPreferences.Update(p => p with { DoubleClickReaction = emoji });
            Reactions.Select(emoji);
            CurrentReaction.Emoji = emoji;
        };
        TagSearch.IsCheckedChanged += async (_, _) => await ChangeTagSearch();
        Appearance.Click += (_, _) => vm.NavigateWorkspace(WorkspacePage.Appearance);
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
        ReactionChoices.IsVisible = action == DoubleClickAction.React;
        if (!ReactionChoices.IsVisible) ReactionChoices.IsExpanded = false;
    }

    async Task ChangeTagSearch()
    {
        if (_loading) return;
        var wanted = TagSearch.IsChecked == true;
        TagSearch.IsEnabled = false;
        PrivacyStatus.Text = null;
        var error = await _vm.SetTagSearchAsync(wanted);
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
