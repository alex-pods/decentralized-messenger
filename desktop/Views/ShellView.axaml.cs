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
        ChassisWidth.Track(this);
        DataContextChanged += (_, _) => Hook();
        AttachedToVisualTree += (_, _) => EnsureLogHooks();
        DetachedFromVisualTree += (_, _) => TeardownLogHooks();
        this.FindControl<ListBox>("Log")?.AddHandler(InputElement.PointerPressedEvent, OnLogPressed, RoutingStrategies.Tunnel);
    }

    void Hook()
    {
        if (_vm != null)
        {
            _vm.ScrollToEnd -= Scroll;
            _vm.ScrollToMessage -= ScrollTo;
        }
        _vm = DataContext as ShellViewModel;
        if (_vm != null)
        {
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
    async void OnChooseAvatarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение для аватара",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"]
                }
            ]
        });
        if (files.Count > 0)
            await UploadAvatarFromPath(files[0].Path.LocalPath);
    }

    void OnAvatarDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.GetFiles()?.Any(file => file.Path.IsFile) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnAvatarDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.FirstOrDefault(item => item.Path.IsFile);
        if (file != null)
            await UploadAvatarFromPath(file.Path.LocalPath);
        e.Handled = true;
    }

    Task UploadAvatarFromPath(string path) =>
        _vm?.UploadAvatarAsync(path) ?? Task.CompletedTask;
}
