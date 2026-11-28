using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop.Appearance;
using MessengerDesktop.Settings;
using MessengerDesktop.Views.Dialogs;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    static readonly object PinsPage = new();
    readonly Dictionary<WorkspaceRoute, WorkspaceView> _workspaceViews = new();
    object? _visiblePage;
    ChatPosition? _chatPosition;

    sealed class WorkspaceView(Control content)
    {
        public Control Content { get; } = content;
        public Control? Focus { get; set; }
        public ViewportPosition? Position { get; set; }
    }

    sealed record ChatPosition(string Id, string Draft, int Caret, int Start, int End,
        ViewportPosition? Position, Control? Focus);

    sealed record ViewportPosition(Vector Offset, object? ItemKey, double ItemTop);

    void OnWorkspaceBackClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ShowingPinnedMessages == true) _vm.ClosePinListCommand.Execute(null);
        else _vm?.BackWorkspace();
    }

    void UpdateWorkspaceFrame(bool narrow, double sidebarWidth)
    {
        var margin = default(Thickness);
        if (!narrow)
        {
            const double inset = 18;
            var paneWidth = Bounds.Width - sidebarWidth;
            var available = paneWidth - 2 * inset;
            var center = Bounds.Width / 2 - sidebarWidth;
            // Shift the panel only when centering would make it too narrow.
            var width = Math.Clamp(2 * (center - inset), Math.Min(540, available), Math.Min(656, available));
            var left = Math.Clamp(center - width / 2, inset, paneWidth - width - inset);
            margin = new Thickness(left, inset, paneWidth - left - width, inset);
        }
        WorkspaceFrame.Margin = margin;
        WorkspaceFrame.CornerRadius = new CornerRadius(narrow ? 0 : 10);
        WorkspaceFrame.BorderThickness = new Thickness(narrow ? 0 : 1);
        ConversationWallpaper.IsVisible = !narrow || _visiblePage == null;
    }

    void PresentWorkspace()
    {
        if (_vm is not { } vm) return;
        object? next = vm.ShowingPinnedMessages ? PinsPage : vm.Workspace;
        if (ReferenceEquals(_visiblePage, next)) return;

        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        if (_visiblePage == null && vm.SelectedChat is { } chat)
            _chatPosition = new(chat.Id, vm.Draft, ComposerInput.CaretIndex, ComposerInput.SelectionStart,
                ComposerInput.SelectionEnd, CaptureViewport(_log, _logScroll), focus);
        else if (_visiblePage is WorkspaceRoute previous && _workspaceViews.TryGetValue(previous, out var old))
        {
            var list = old.Content.FindDescendantOfType<MemberList>()?.List;
            var scroll = old.Content as ScrollViewer ?? list?.FindDescendantOfType<ScrollViewer>() ??
                old.Content.FindDescendantOfType<ScrollViewer>();
            old.Position = CaptureViewport(list, scroll);
            old.Focus = focus;
        }

        _visiblePage = next;
        if (next is WorkspaceRoute route)
        {
            if (!_workspaceViews.TryGetValue(route, out var view))
                _workspaceViews[route] = view = new(CreateWorkspace(vm, route));
            WorkspaceContent.Content = view.Content;
        }
        else if (vm.Workspace == null) WorkspaceContent.Content = null;

        foreach (var stale in _workspaceViews.Keys.Where(route => !vm.ContainsWorkspace(route)).ToArray())
            _workspaceViews.Remove(stale);
        WorkspaceFrame.IsVisible = next != null;
        WorkspaceSurface.IsVisible = next is WorkspaceRoute;
        ConversationSurface.IsVisible = next == null;
        UpdateNavigation();
        Dispatcher.UIThread.Post(() => RestorePage(next), DispatcherPriority.Loaded);
    }

    Control CreateWorkspace(ShellViewModel vm, WorkspaceRoute route) => route.Page switch
    {
        WorkspacePage.Account => new ScrollViewer
        {
            Classes = { "dialogScroll" },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new ProfileEditor()
        },
        WorkspacePage.Members => new ConversationDetails(),
        WorkspacePage.Contact => new UserProfilePage(vm, route.UserId!),
        WorkspacePage.Settings => new SettingsPage(vm, route.Section),
        WorkspacePage.Appearance => CreateAppearance(vm, route),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    static AppearanceEditor CreateAppearance(ShellViewModel vm, WorkspaceRoute route)
    {
        var editor = new AppearanceEditor(vm.ThemeApi, vm.CurrentUserId);
        editor.CloseRequested += () => { if (vm.Workspace == route) vm.BackWorkspace(); };
        return editor;
    }

    void RestorePage(object? page)
    {
        if (!ReferenceEquals(_visiblePage, page) || _vm is not { } vm) return;
        if (page is WorkspaceRoute route && _workspaceViews.TryGetValue(route, out var view))
        {
            var list = view.Content.FindDescendantOfType<MemberList>()?.List;
            var scroll = view.Content as ScrollViewer ?? list?.FindDescendantOfType<ScrollViewer>() ??
                view.Content.FindDescendantOfType<ScrollViewer>();
            if (view.Focus is not { IsEffectivelyVisible: true } focus || !focus.Focus()) WorkspaceBack.Focus();
            RestoreViewport(list, scroll, view.Position, page);
        }
        else if (ReferenceEquals(page, PinsPage)) WorkspaceBack.Focus();
        else if (page == null)
        {
            var previous = _chatPosition;
            _chatPosition = null;
            if (previous != null && previous.Id == vm.SelectedChat?.Id)
            {
                if (!vm.IsRevealingMessage) RestoreViewport(_log, _logScroll, previous.Position, page);
                if (vm.Draft == previous.Draft)
                {
                    ComposerInput.CaretIndex = previous.Caret;
                    ComposerInput.SelectionStart = previous.Start;
                    ComposerInput.SelectionEnd = previous.End;
                }
            }
            if (previous?.Focus is { IsEffectivelyVisible: true } focus && focus.Focus()) return;
            if (ComposerInput.IsEffectivelyVisible) ComposerInput.Focus();
            else if (ChatSearch.IsEffectivelyVisible) ChatSearch.Focus();
        }
    }

    static object? ItemKey(object? item) => item switch
    {
        MessageItem message => ("message", message.Id),
        GroupActivityLine activity => ("activity", activity.Event.Id),
        MemberRow member => ("member", member.Id),
        _ => null
    };

    static ViewportPosition? CaptureViewport(ListBox? list, ScrollViewer? scroll)
    {
        if (scroll == null) return null;
        if (list == null || scroll.FindDescendantOfType<ScrollContentPresenter>() is not { } viewport)
            return new(scroll.Offset, null, 0);
        foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (ItemKey(row.Content) is { } key && row.TranslatePoint(default, viewport) is { } point &&
                point.Y < viewport.Bounds.Height && point.Y + row.Bounds.Height > 0)
                return new(scroll.Offset, key, point.Y);
        }
        return new(scroll.Offset, null, 0);
    }

    void RestoreViewport(ListBox? list, ScrollViewer? scroll, ViewportPosition? saved, object? page)
    {
        if (scroll == null || saved == null) return;
        scroll.Offset = saved.Offset;
        if (list == null || saved.ItemKey == null) return;
        var item = list.Items.FirstOrDefault(candidate => Equals(ItemKey(candidate), saved.ItemKey));
        if (item == null) return;
        void Align()
        {
            if (!ReferenceEquals(_visiblePage, page) || _vm?.IsRevealingMessage == true ||
                scroll.FindDescendantOfType<ScrollContentPresenter>() is not { } viewport) return;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(control => ReferenceEquals(control.Content, item));
            if (row?.TranslatePoint(default, viewport) is { } position)
                scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + position.Y - saved.ItemTop);
        }
        if (!list.GetVisualDescendants().OfType<ListBoxItem>().Any(row => ReferenceEquals(row.Content, item)))
            list.ScrollIntoView(item);
        Dispatcher.UIThread.Post(Align, DispatcherPriority.Loaded);
    }
}
