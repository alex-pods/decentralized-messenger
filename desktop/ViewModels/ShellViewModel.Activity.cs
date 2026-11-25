using CommunityToolkit.Mvvm.ComponentModel;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    [ObservableProperty] private IReadOnlyList<GroupActivityItem> _groupActivity = [];
    [ObservableProperty] private string? _latestGroupActivity;
    [ObservableProperty] private bool _hasGroupActivity;
    int _activityGeneration;

    void ClearGroupActivity()
    {
        ++_activityGeneration;
        GroupActivity = [];
        LatestGroupActivity = null;
        HasGroupActivity = false;
    }

    async Task LoadGroupActivity(ChatItem? chat)
    {
        if (chat is not { IsGroup: true }) return;
        var generation = ++_activityGeneration;
        try
        {
            var items = await _api.Send<GroupActivityItem[]>(HttpMethod.Get, $"chats/{chat.Id}/activity", null, default) ?? [];
            if (_closed || generation != _activityGeneration || SelectedChat?.Id != chat.Id) return;
            GroupActivity = items;
            LatestGroupActivity = items.FirstOrDefault()?.Description;
            HasGroupActivity = items.Length > 0;
        }
        catch (ApiException) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) when (_closed) { }
    }
}

public sealed record GroupActivityItem(long Id, string Kind, string? DisplayName, string OccurredAt)
{
    public string Description => Kind switch
    {
        "joined" => $"Добавлен участник: {Name}",
        "left" => $"Выход из группы: {Name}",
        "removed" => $"Участник исключён: {Name}",
        _ => ""
    };

    public string DateLabel => MessageMapper.FormatDateTime(OccurredAt);
    string Name => string.IsNullOrWhiteSpace(DisplayName) ? "Удалённый аккаунт" : DisplayName;
}
