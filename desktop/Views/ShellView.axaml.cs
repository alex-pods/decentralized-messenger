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
        ChatList.AddHandler(InputElement.TappedEvent, OnChatTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => EnsureLogHooks();
        DetachedFromVisualTree += (_, _) => TeardownLogHooks();
        DetachedFromVisualTree += (_, _) => CancelSidebarSearch();
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
        _vm?.NavigateWorkspace(WorkspacePage.Settings);
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
            _vm.ResetWorkspace();
            _vm.ClosePinListCommand.Execute(null);
            _showChats = true;
            UpdateNavigation();
            ChatSearch.Focus();
            e.Handled = true;
        }
        if (e.Key == Key.Escape && _vm != null)
        {
            if (_vm.OverlayOpen) _vm.CloseOverlayCommand.Execute(null);
            else if (_vm.ShowingPinnedMessages) _vm.ClosePinListCommand.Execute(null);
            else if (_vm.Workspace != null) _vm.BackWorkspace();
            else if (_vm.HasComposeHint) _vm.CancelComposeCommand.Execute(null);
            else _vm.SelectedMessage = null;
            e.Handled = true;
        }
    }

    bool _showChats = true;
    long? _presentedChatId;
    const double ChatSidebarWidth = 300;

    void OnShowChats(object? sender, RoutedEventArgs e)
    {
        _showChats = true;
        UpdateNavigation();
    }

    void UpdateNavigation()
    {
        bool narrow = Bounds.Width < 780;
        UpdateOverlayLayout();
        var sidebarWidth = narrow ? 0 : ChatSidebarWidth;
        UpdateWorkspaceFrame(narrow, sidebarWidth);
        ShellGrid.ColumnDefinitions = narrow
            ? new ColumnDefinitions("*")
            : new ColumnDefinitions { new(sidebarWidth, GridUnitType.Pixel), new(1, GridUnitType.Star) };
        Grid.SetColumn(ChatSidebar, 0);
        Grid.SetColumn(ScreenBay, narrow ? 0 : 1);
        var showList = _showChats && _visiblePage == null;
        ChatSidebar.IsVisible = !narrow || showList;
        ScreenBay.IsVisible = !narrow || !showList;
        BackToChats.IsVisible = narrow;
    }

    void UpdateOverlayLayout()
    {
        var compactSheet = Bounds.Width < 480;
        var forwarding = _vm?.Overlay == "forward";
        var inset = compactSheet ? 12 : 20;
        var upperInset = forwarding && Bounds.Height >= 520 ? Math.Clamp(Bounds.Height * .16, 20, 144) : inset;
        OverlaySheet.Margin = new Thickness(inset, upperInset, inset, inset);
        OverlaySheet.Padding = new Thickness(compactSheet ? 16 : 20);
        OverlaySheet.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        OverlaySheet.VerticalAlignment = forwarding ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
        OverlaySheet.MaxWidth = _vm?.Overlay switch
        {
            "direct" => 384,
            "group" => 428,
            "forward" => 442,
            _ => 420
        };
        OverlaySheet.MaxHeight = Math.Clamp(Bounds.Height - OverlaySheet.Margin.Top - OverlaySheet.Margin.Bottom, 100, 680);
    }

    bool _filtering;

    void OnChatFilterChanged(object? sender, TextChangedEventArgs e) => UpdateSidebarSearch();

    void OnClearChatSearch(object? sender, RoutedEventArgs e)
    {
        ChatSearch.Clear();
        ChatSearch.Focus();
    }

    void OnChatSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(ChatSearch.Text))
            ChatSearch.Clear();
        else if (!string.IsNullOrEmpty(_sidebarQuery) && e.Key is Key.Down or Key.Enter)
        {
            if (HasSearchPreedit()) return;
            if (e.Key == Key.Enter) SubmitSidebarSearch();
            else SearchResultButtons().FirstOrDefault()?.Focus();
        }
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
        if (_vm is { } vm)
        {
            vm.ResetWorkspace();
            vm.ClosePinListCommand.Execute(null);
            vm.SelectedChat = chat;
        }
        _showChats = false;
        UpdateNavigation();
    }

    void OnChatTapped(object? sender, TappedEventArgs e)
    {
        if ((_visiblePage == null && (Bounds.Width >= 780 || !_showChats)) || e.Source is not Visual source) return;
        var row = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (row?.DataContext is ChatItem chat && ReferenceEquals(chat, _vm?.SelectedChat))
            OpenFromSearch(chat);
    }

    void FilterChats()
    {
        if (_vm == null) return;
        _filtering = true;
        try
        {
            if (!ChatList.Items.SequenceEqual(_vm.Chats)) ChatList.ItemsSource = _vm.Chats.ToArray();
            ChatList.SelectedItem = _vm.Chats.Contains(_vm.SelectedChat!) ? _vm.SelectedChat : null;
            UpdateSidebarResults();
        }
        finally { _filtering = false; }
    }

    void OnChatsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FilterChats();

    void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Overlay)) UpdateOverlayLayout();
        if (e.PropertyName is nameof(ShellViewModel.ShowingPinnedMessages) or nameof(ShellViewModel.Workspace))
            PresentWorkspace();
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
                else if (WorkspaceBack.IsEffectivelyVisible) WorkspaceBack.Focus();
                else ComposerInput.Focus();
            }, DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(ShellViewModel.SelectedChat))
        {
            FilterChats();
            var chatId = _vm?.SelectedChat?.Id;
            if (chatId != _presentedChatId) _showChats = chatId == null;
            _presentedChatId = chatId;
            UpdateNavigation();
        }
    }

    void Hook()
    {
        CancelSidebarSearch();
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
            UpdateSidebarSearch();
            _vm.ScrollToEnd += Scroll;
            _vm.ScrollToMessage += ScrollTo;
            PresentWorkspace();
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

    void OnTimelineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not { } vm || sender is not ListBox list) return;
        vm.SelectedMessage = list.SelectedItem as MessageItem;
        if (list.SelectedItem is GroupActivityLine)
            list.SetCurrentValue(ListBox.SelectedItemProperty, null);
    }

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
        if (_visiblePage == null && sender is ScrollViewer scroll && ReferenceEquals(_logScroll, scroll))
        {
            var gap = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
            var previousGap = gap - e.ExtentDelta.Y + e.ViewportDelta.Y + e.OffsetDelta.Y;
            var resized = Math.Abs(e.ViewportDelta.Y) > .5;
            if ((resized || e.ExtentDelta.Y > .5) && previousGap <= 16)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_visiblePage == null && ReferenceEquals(_logScroll, scroll) && (resized || _vm?.IsRevealingMessage != true))
                        scroll.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
        }
        Dispatcher.UIThread.Post(SyncPinFromViewport, DispatcherPriority.Background);
    }

    void SyncPinFromViewport()
    {
        if (_visiblePage != null || _vm == null || !_vm.HasPins || _vm.Messages.Count == 0) return;
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
            if (c.Content is not MessageItem message) continue;
            bestBottom = bottom;
            lastVisible = message;
        }

        if (lastVisible != null)
            _vm.SyncPinToVisible(lastVisible);
    }

    void Scroll()
    {
        var chatId = _vm?.SelectedChat?.Id;
        Dispatcher.UIThread.Post(() =>
        {
            if (_visiblePage != null || chatId == null || _vm?.SelectedChat?.Id != chatId || _vm.Timeline.Count == 0) return;
            _logScroll?.ScrollToEnd();
            SyncPinFromViewport();
        }, DispatcherPriority.Background);
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
