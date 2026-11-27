using CommunityToolkit.Mvvm.ComponentModel;

namespace MessengerDesktop;

public enum WorkspacePage { Account, Members, Contact, Settings, Appearance }

public sealed class WorkspaceRoute(WorkspacePage page, long? userId = null, string? section = null)
{
    public WorkspacePage Page { get; } = page;
    public long? UserId { get; } = userId;
    public string? Section { get; } = section;
}

public partial class ShellViewModel
{
    readonly Stack<WorkspaceRoute> _workspaceHistory = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WorkspaceTitle))]
    WorkspaceRoute? _workspace;

    public string WorkspaceTitle => ShowingPinnedMessages ? "Закреплённые сообщения" : Workspace?.Page switch
    {
        WorkspacePage.Account => ProfileEditing ? "Редактирование профиля" : "Мой аккаунт",
        WorkspacePage.Members => ChatTitle,
        WorkspacePage.Contact => "Профиль",
        WorkspacePage.Settings => "Настройки",
        WorkspacePage.Appearance => "Оформление",
        _ => ""
    };

    internal string? KnownContactName(long userId) => _names.GetValueOrDefault(userId);

    internal bool ContainsWorkspace(WorkspaceRoute route) => Workspace == route || _workspaceHistory.Contains(route);
    internal bool HasWorkspacePage(WorkspacePage page) =>
        Workspace?.Page == page || _workspaceHistory.Any(route => route.Page == page);

    internal void NavigateWorkspace(WorkspacePage page, long? userId = null, string? section = null)
    {
        if (Workspace is { } current)
        {
            if (current.Page == page && current.UserId == userId && current.Section == section) return;
            _workspaceHistory.Push(current);
        }
        Workspace = new(page, userId, section);
    }

    internal void BackWorkspace()
    {
        if (Workspace?.Page == WorkspacePage.Account && ProfileEditing)
        {
            if (!SaveProfileCommand.IsRunning) ResetProfileDraft();
            return;
        }
        Error = null;
        Workspace = _workspaceHistory.TryPop(out var previous) ? previous : null;
    }

    internal void ResetWorkspace()
    {
        if (HasWorkspacePage(WorkspacePage.Account) && ProfileEditing) ResetProfileDraft();
        _workspaceHistory.Clear();
        Workspace = null;
    }
}
