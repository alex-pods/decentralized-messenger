using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;

using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MessengerDesktop.Settings;

namespace MessengerDesktop.Views;

public partial class ShellView : UserControl
{
    ShellViewModel? _vm;
    ListBox? _log;
    ScrollViewer? _logScroll;
    bool _listHooked;

    public ShellView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateNavigation();
        AttachedToVisualTree += (_, _) => UpdateNavigation();
        DataContextChanged += (_, _) => Hook();
        ChatList.SelectionChanged += (_, _) =>
        {
            if (_filtering || ChatList.SelectedItem is not ChatItem chat) return;
            OpenFromSearch(chat);
        };
        AttachedToVisualTree += (_, _) => EnsureLogHooks();
        DetachedFromVisualTree += (_, _) => TeardownLogHooks();
        this.FindControl<ListBox>("Log")?.AddHandler(InputElement.PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
        ComposerInput.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => { AppPreferences.Changed += UpdateSendHint; UpdateSendHint(); };
        DetachedFromVisualTree += (_, _) => AppPreferences.Changed -= UpdateSendHint;
    }

    void UpdateSendHint() => ToolTip.SetTip(SendButton,
        AppPreferences.Current.SendMode == SendMode.Enter ? "Отправить · Enter" : "Отправить · Ctrl+Enter / ⌘Enter");

    void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        if (ComposerInput.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText))) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // Enter-режим: Enter и Ctrl+Enter отправляют, Shift+Enter — перенос строки.
        // Ctrl+Enter-режим: отправляет только Ctrl/⌘+Enter, обычный Enter — перенос строки.
        var send = AppPreferences.Current.SendMode == SendMode.Enter ? !shift && !alt : ctrl && !shift && !alt;
        if (!send) return;
        e.Handled = true;
        if (_vm.CanSendDraft && _vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm != null && TopLevel.GetTopLevel(this) is Window owner)
            SettingsWindow.Open(_vm, owner);
    }

    void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.OverlayOpen != true || e.Source != sender ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _vm.CloseOverlayCommand.Execute(null);
        e.Handled = true;
    }

    void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _vm?.OverlayOpen == false)
        {
            _showChats = true;
            UpdateNavigation();
            ChatSearch.Focus();
            e.Handled = true;
        }
        if (e.Key == Key.Escape && _vm != null)
        {
            if (_vm.OverlayOpen) _vm.CloseOverlayCommand.Execute(null);
            else if (_vm.ShowingPinnedMessages) _vm.ClosePinListCommand.Execute(null);
            else if (_vm.HasComposeHint) _vm.CancelComposeCommand.Execute(null);
            else _vm.SelectedMessage = null;
            e.Handled = true;
        }
    }

    bool _showChats = true;

    void OnShowChats(object? sender, RoutedEventArgs e)
    {
        _showChats = true;
        UpdateNavigation();
    }

    void UpdateNavigation()
    {
        bool narrow = Bounds.Width < 780;
        OverlaySheet.MaxHeight = Math.Clamp(Bounds.Height - 40, 220, 680);
        ShellGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "344,*");
        Grid.SetColumn(ChatSidebar, 0);
        Grid.SetColumn(ScreenBay, narrow ? 0 : 1);
        ChatSidebar.IsVisible = !narrow || _showChats;
        ScreenBay.IsVisible = !narrow || !_showChats;
        BackToChats.IsVisible = narrow;
    }

    bool _filtering;

    void OnChatFilterChanged(object? sender, TextChangedEventArgs e) => FilterChats();

    void OnClearChatSearch(object? sender, RoutedEventArgs e)
    {
        ChatSearch.Clear();
        ChatSearch.Focus();
    }

    void OnChatSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(ChatSearch.Text))
            ChatSearch.Clear();
        else if (e.Key == Key.Down && ChatList.Items.Count > 0)
        {
            ChatList.ScrollIntoView(0);
            if (ChatList.ContainerFromIndex(0) is not { } row) return;
            row.Focus();
        }
        else if (e.Key == Key.Enter && ChatList.Items.FirstOrDefault() is ChatItem first)
            OpenFromSearch(first);
        else return;
        e.Handled = true;
    }

    void OpenFromSearch(ChatItem chat)
    {
        if (_vm != null) _vm.SelectedChat = chat;
        _showChats = false;
        UpdateNavigation();
    }

    static string NormalizeSearch(string value) => value.ToLowerInvariant().Replace('ё', 'е');

    void FilterChats()
    {
        if (_vm == null) return;
        _filtering = true;
        try
        {
            var query = ChatSearch.Text?.Trim() ?? "";
            var tagOnly = query.StartsWith('@');
            var terms = NormalizeSearch(query.TrimStart('@')).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var visible = _vm.Chats.Where(chat =>
            {
                var tag = NormalizeSearch(chat.Tag ?? "");
                var title = tagOnly ? "" : NormalizeSearch(chat.Title);
                return (!tagOnly || tag.Length > 0) &&
                       terms.All(term => title.Contains(term, StringComparison.Ordinal) ||
                                         tag.Contains(term, StringComparison.Ordinal));
            }).ToArray();
            ChatList.ItemsSource = visible;
            ChatList.SelectedItem = visible.Contains(_vm.SelectedChat) ? _vm.SelectedChat : null;
            ChatEmpty.IsVisible = visible.Length == 0;
            ChatEmpty.Text = query.Length == 0 ? "Здесь появятся ваши разговоры" : "По этому запросу нет чатов";
            ClearChatSearch.IsVisible = !string.IsNullOrEmpty(ChatSearch.Text);
        }
        finally { _filtering = false; }
    }

    void OnChatsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FilterChats();

    void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ShowingPinnedMessages))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm?.ShowingPinnedMessages == true) PinnedMessages.FindControl<Button>("BackFromPinnedMessages")!.Focus();
                else if (OpenPinnedMessages.IsEffectivelyVisible) OpenPinnedMessages.Focus();
            }, DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(ShellViewModel.OverlayOpen))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm?.OverlayOpen == true)
                {
                    var target = _vm.Overlay is "direct" or "group" or "forward"
                        ? OverlaySheet.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
                        : null;
                    if (target != null) target.Focus();
                    else OverlaySheet.GetVisualDescendants().OfType<Button>().FirstOrDefault(c => c.IsEffectivelyVisible)?.Focus();
                    OverlayScroll.ScrollToHome();
                }
                else ComposerInput.Focus();
            }, DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(ShellViewModel.SelectedChat))
        {
            FilterChats();
            _showChats = _vm?.HasChat != true;
            UpdateNavigation();
        }
    }

    void Hook()
    {
        if (_vm != null)
        {
            _vm.Chats.CollectionChanged -= OnChatsChanged;
            _vm.PropertyChanged -= OnShellPropertyChanged;
            _vm.ScrollToEnd -= Scroll;
            _vm.ScrollToMessage -= ScrollTo;
        }
        _vm = DataContext as ShellViewModel;
        if (_vm != null)
        {
            _vm.Chats.CollectionChanged += OnChatsChanged;
            _vm.PropertyChanged += OnShellPropertyChanged;
            FilterChats();
            _vm.ScrollToEnd += Scroll;
            _vm.ScrollToMessage += ScrollTo;
        }
        EnsureLogHooks();
    }

    void EnsureLogHooks()
    {
        _log ??= this.FindControl<ListBox>("Log");
        if (_log == null) return;

        if (!_listHooked)
        {
            // ScrollViewer внутри шаблона пересоздаётся при IsVisible/HasChat —
            // bubbling с ListBox живёт стабильно.
            _log.AddHandler(ScrollViewer.ScrollChangedEvent, OnLogScrollChanged, RoutingStrategies.Bubble, handledEventsToo: true);
            _log.TemplateApplied += OnLogTemplateApplied;
            _listHooked = true;
        }

        BindLogScrollViewer();
    }

    void OnLogTemplateApplied(object? sender, TemplateAppliedEventArgs e) => BindLogScrollViewer();

    void BindLogScrollViewer()
    {
        if (_log == null) return;
        var sv = _log.FindDescendantOfType<ScrollViewer>();
        if (ReferenceEquals(sv, _logScroll)) return;
        if (_logScroll != null)
            _logScroll.ScrollChanged -= OnLogScrollChanged;
        _logScroll = sv;
        if (_logScroll != null)
            _logScroll.ScrollChanged += OnLogScrollChanged;
    }

    void TeardownLogHooks()
    {
        if (_logScroll != null)
        {
            _logScroll.ScrollChanged -= OnLogScrollChanged;
            _logScroll = null;
        }
        if (_listHooked && _log != null)
        {
            _log.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnLogScrollChanged);
            _log.TemplateApplied -= OnLogTemplateApplied;
            _listHooked = false;
        }
        _log = null;
    }

    void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Offset не менялся (только extent) — тоже синхронизируем после догрузки истории.
        Dispatcher.UIThread.Post(SyncPinFromViewport, DispatcherPriority.Background);
    }

    void SyncPinFromViewport()
    {
        if (_vm == null || !_vm.HasPins || _vm.Messages.Count == 0) return;
        var list = _log ?? this.FindControl<ListBox>("Log");
        if (list == null) return;

        MessageItem? lastVisible = null;
        var bestBottom = double.MinValue;
        var viewport = _logScroll?.FindDescendantOfType<ScrollContentPresenter>();
        if (viewport == null) return;
        var viewH = viewport.Bounds.Height;
        foreach (var c in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var topLeft = c.TranslatePoint(new Point(0, 0), viewport);
            if (topLeft == null) continue;
            var y = topLeft.Value.Y;
            var bottom = y + c.Bounds.Height;
            if (bottom <= 0 || y >= viewH) continue;
            if (bottom <= bestBottom) continue;
            var idx = list.IndexFromContainer(c);
            if (idx < 0 || idx >= _vm.Messages.Count) continue;
            bestBottom = bottom;
            lastVisible = _vm.Messages[idx];
        }

        if (lastVisible != null)
            _vm.SyncPinToVisible(lastVisible);
    }

    void Scroll()
    {
        if (_vm == null || _vm.Messages.Count == 0) return;
        if (this.FindControl<ListBox>("Log") is { } list)
            list.ScrollIntoView(_vm.Messages[^1]);
        Dispatcher.UIThread.Post(SyncPinFromViewport, DispatcherPriority.Background);
    }

    void ScrollTo(MessageItem item)
    {
        if (this.FindControl<ListBox>("Log") is not { } list) return;
        if (!list.Items.Contains(item)) return;
        try
        {
            list.ScrollIntoView(item);
            list.InvalidateMeasure();
            list.InvalidateArrange();
        }
        catch { /* панель ещё не догнала новую страницу */ }
        Dispatcher.UIThread.Post(SyncPinFromViewport, DispatcherPriority.Background);
    }

    void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => MainWindow.DragFrom(this, e);

    void OnChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ReactionChip chip } && DataContext is ShellViewModel vm)
            vm.ToggleChipCommand.Execute(chip);
        e.Handled = true;   // ListBox не увидит нажатие и не выберет сообщение
    }
    
    void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control src) return;
        if (src is ScrollBar || src.FindAncestorOfType<ScrollBar>() != null) return;

        Border? bubble = null;
        var onChip = false;
        for (Control? v = src; v != null; v = v.GetVisualParent() as Control)
        {
            if (v is Button) return; // Do not swallow message-menu or reaction button clicks in the tunnel.
            if (v is Border b && b.Classes.Contains("reaction")) onChip = true;
            if (v is Border bb && bb.Classes.Contains("bubble"))
            {
                bubble = bb;
                break;
            }
            if (v is ListBoxItem) break; // дошли до строки, пузырь не найден
        }

        if (bubble != null)
        {
            // Двойной клик по самому сообщению (не по чипу реакции).
            if (e.ClickCount == 2 && !onChip && bubble.DataContext is MessageItem message
                && _vm is { OverlayOpen: false } && _vm.ActivateMessage(message))
                Dispatcher.UIThread.Post(() => ComposerInput.Focus(), DispatcherPriority.Input);
            return;
        }


        if (DataContext is ShellViewModel vm)
            vm.SelectedMessage = null;

        e.Handled = true;
    }
}
