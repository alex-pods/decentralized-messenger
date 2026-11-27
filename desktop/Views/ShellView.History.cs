using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    const double HistoryThreshold = 180;
    bool _historyCheckQueued;
    bool _loadingOlder;
    bool _restoringHistory;
    bool _scrolling;
    bool _followLatest = true;
    int _viewportGeneration;
    HistoryAnchor? _historyAnchor;

    sealed record HistoryAnchor(TimelineItem Item, double Y, long ChatId);

    void ResetHistoryViewport()
    {
        ++_viewportGeneration;
        _loadingOlder = false;
        _restoringHistory = false;
        _historyAnchor = null;
        _followLatest = true;
    }

    bool IsNearBottom() => _logScroll is { } scroll &&
        scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 48;

    void OnLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Автоматический скролл ListBox реагирует даже на сдвиг SelectedIndex
        // при вставке страницы. Скроллим только при выборе ДРУГОГО сообщения,
        // сохраняя в том числе навигацию по списку с клавиатуры.
        if (!_restoringHistory && ReferenceEquals(e.Source, sender) &&
            sender is ListBox list && e.AddedItems.Count > 0 && e.AddedItems[0] is MessageItem item)
            list.ScrollIntoView(item);
    }

    void OnHistoryWheel(object? sender, PointerWheelEventArgs e)
    {
        // На самом верху Offset уже не меняется. Повторная прокрутка позволяет
        // повторить запрос после сетевой ошибки, без бесконечных автоповторов.
        if (e.Delta.Y > 0) QueueHistoryCheck();
    }

    void QueueHistoryCheck()
    {
        if (_historyCheckQueued) return;
        _historyCheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _historyCheckQueued = false;
            _ = LoadHistoryNearTop();
        }, DispatcherPriority.Background);
    }

    async Task LoadHistoryNearTop()
    {
        var vm = _vm;
        var scroll = _logScroll;
        if (_loadingOlder || _restoringHistory || _scrolling || _visiblePage != null || !IsEffectivelyVisible ||
            _log?.IsEffectivelyVisible != true || scroll == null || scroll.Viewport.Height <= 0 ||
            scroll.Offset.Y > HistoryThreshold || vm == null || vm.IsOpeningChat ||
            vm.IsLoadingHistory || !vm.HasOlderTimeline || vm.Timeline.Count == 0)
            return;

        var generation = _viewportGeneration;
        _loadingOlder = true;
        try
        {
            var added = await vm.LoadOlderAsync();
            if (added && generation == _viewportGeneration && ReferenceEquals(vm, _vm))
                QueueHistoryCheck(); // В том числе заполнить слишком высокий viewport.
        }
        finally
        {
            if (generation == _viewportGeneration) _loadingOlder = false;
        }
    }

    static Control MessageAnchor(Control container) =>
        container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("bubble"))
        ?? container.GetVisualDescendants().OfType<Border>().LastOrDefault(b => b.Classes.Contains("historyLabel"))
        ?? container;

    void CaptureHistoryAnchor()
    {
        _restoringHistory = true;
        _historyAnchor = null;
        if (_visiblePage != null || _log == null || _logScroll == null || _vm?.SelectedChat is not { } chat) return;

        // Привязываемся к пузырю сообщения: у первой строки после вставки может
        // исчезнуть разделитель даты или имя отправителя, изменив её высоту.
        foreach (var container in _log.GetRealizedContainers())
        {
            if (container is not ListBoxItem { Content: TimelineItem message }) continue;
            var anchor = MessageAnchor(container);
            var point = anchor.TranslatePoint(default, _logScroll);
            if (point == null || point.Value.Y + anchor.Bounds.Height <= 0 ||
                point.Value.Y >= _logScroll.Viewport.Height) continue;
            if (_historyAnchor == null || point.Value.Y < _historyAnchor.Y)
                _historyAnchor = new HistoryAnchor(message, point.Value.Y, chat.Id);
        }
    }

    void RestoreHistoryAnchor()
    {
        try
        {
            if (_historyAnchor is not { } anchor || _vm?.SelectedChat?.Id != anchor.ChatId ||
                _log == null || _logScroll == null) return;
            _log.UpdateLayout();
            var item = _log.Items.FirstOrDefault(candidate => Equals(ItemKey(candidate), ItemKey(anchor.Item)));
            if (item == null || _log.ContainerFromItem(item) is not { } container) return;
            var point = MessageAnchor(container).TranslatePoint(default, _logScroll);
            if (point == null) return;
            _logScroll.Offset = new Vector(_logScroll.Offset.X, _logScroll.Offset.Y + point.Value.Y - anchor.Y);
            _log.UpdateLayout();
            _followLatest = IsNearBottom();
        }
        finally
        {
            _historyAnchor = null;
            _restoringHistory = false;
        }
        SyncPinFromViewport();
    }
}
