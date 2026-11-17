using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

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
            if (_vm != null) _vm.SelectedChat = chat;
            _showChats = false;
            UpdateNavigation();
        };
        AttachedToVisualTree += (_, _) => EnsureLogHooks();
        DetachedFromVisualTree += (_, _) => TeardownLogHooks();
        this.FindControl<ListBox>("Log")?.AddHandler(InputElement.PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
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
        OverlaySheet.MaxHeight = Math.Max(220, Bounds.Height - 40);
        ShellGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "320,*");
        Grid.SetColumn(ChatSidebar, 0);
        Grid.SetColumn(ScreenBay, narrow ? 0 : 1);
        ChatSidebar.IsVisible = !narrow || _showChats;
        ScreenBay.IsVisible = !narrow || !_showChats;
        BackToChats.IsVisible = narrow;
    }

    bool _filtering;

    void OnChatFilterChanged(object? sender, TextChangedEventArgs e) => FilterChats();

    void FilterChats()
    {
        if (_vm == null) return;
        _filtering = true;
        try
        {
            var query = ChatSearch.Text?.Trim() ?? "";
            var visible = _vm.Chats.Where(chat => chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            ChatList.ItemsSource = visible;
            ChatList.SelectedItem = visible.Contains(_vm.SelectedChat) ? _vm.SelectedChat : null;
            ChatEmpty.IsVisible = visible.Length == 0;
            ChatEmpty.Text = query.Length == 0 ? "Здесь появятся ваши разговоры" : "Чаты не найдены";
        }
        finally { _filtering = false; }
    }

    void OnChatsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FilterChats();

    void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.OverlayOpen))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm?.OverlayOpen == true)
                {
                    var target = OverlaySheet.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
                    if (target != null) target.Focus();
                    else OverlaySheet.GetVisualDescendants().OfType<Button>().FirstOrDefault(c => c.IsEffectivelyVisible)?.Focus();
                }
                else ComposerInput.Focus();
            }, DispatcherPriority.Loaded);
        }
        if (e.PropertyName == nameof(ShellViewModel.SelectedChat))
        {
            FilterChats();
            if (_vm?.HasChat == true) _showChats = false;
            UpdateNavigation();
        }
    }

    async void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await new Appearance.AppearanceWindow().ShowDialog(owner);
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
        SyncPinFromViewport();
    }

    void SyncPinFromViewport()
    {
        if (_vm == null || !_vm.HasPins || _vm.Messages.Count == 0) return;
        var list = _log ?? this.FindControl<ListBox>("Log");
        if (list == null) return;

        MessageItem? lastVisible = null;
        var bestBottom = double.MinValue;
        var viewH = list.Bounds.Height;
        foreach (var container in list.GetRealizedContainers())
        {
            if (container is not Control c) continue;
            var topLeft = c.TranslatePoint(new Point(0, 0), list);
            if (topLeft == null) continue;
            var y = topLeft.Value.Y;
            var bottom = y + c.Bounds.Height;
            if (bottom <= 0 || y >= viewH) continue;
            if (bottom <= bestBottom) continue;
            var idx = list.IndexFromContainer(container);
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

    void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: ReactionChip chip } && DataContext is ShellViewModel vm)
            vm.ToggleChipCommand.Execute(chip);
        e.Handled = true;   // ListBox не увидит нажатие и не выберет сообщение
    }
    
    void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control src) return;
        if (src.FindAncestorOfType<ScrollBar>() != null) return;

        bool isBubble = false;
        for (Control? v = src; v != null; v = v.GetVisualParent() as Control)
        {
            if (v is Border b && b.Classes.Contains("bubble")) 
            { 
                isBubble = true; 
                break; 
            }
            if (v is ListBoxItem) break; // дошли до строки, пузырь не найден
        }

        if (isBubble) return;

        if (DataContext is ShellViewModel vm)
            vm.SelectedMessage = null;

        e.Handled = true;
    }
}
