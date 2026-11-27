using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    const int ActivityPageSize = 30;
    readonly Dictionary<long, GroupActivityLine> _activity = new();
    public ObservableCollection<TimelineItem> Timeline { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOlderTimeline))]
    bool _hasMoreActivity;
    public bool HasOlderTimeline => HasMore || HasMoreActivity;
    int _activityGeneration;
    bool _timelineUpdateQueued;

    void ClearGroupActivity()
    {
        ++_activityGeneration;
        _activity.Clear();
        HasMoreActivity = false;
        ScheduleTimelineUpdate();
    }

    async Task LoadGroupActivity(ChatItem? chat)
    {
        if (chat is not { IsGroup: true }) return;
        var generation = ++_activityGeneration;
        try
        {
            var items = await GetActivity(chat.Id);
            if (_closed || generation != _activityGeneration || SelectedChat?.Id != chat.Id) return;
            if (_activity.Count == 0) HasMoreActivity = items.Length == ActivityPageSize;
            AddActivity(items);
        }
        catch (ApiException) { }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) when (_closed) { }
    }

    async Task<GroupActivityItem[]> GetActivity(long chatId, long? beforeId = null)
    {
        var path = $"chats/{chatId}/activity";
        if (beforeId is { } id) path += $"?before_id={id}";
        return await _api.Send<GroupActivityItem[]>(HttpMethod.Get, path, null, default) ?? [];
    }

    void AddActivity(IEnumerable<GroupActivityItem> items)
    {
        foreach (var item in items)
        {
            if (_activity.TryGetValue(item.Id, out var current) && current.Event == item) continue;
            _activity[item.Id] = new GroupActivityLine(item);
        }
        ScheduleTimelineUpdate();
    }

    void ScheduleTimelineUpdate()
    {
        if (_timelineUpdateQueued) return;
        _timelineUpdateQueued = true;
        Dispatcher.UIThread.Post(UpdateTimeline);
    }

    void UpdateTimeline()
    {
        _timelineUpdateQueued = false;
        UpdateReplyPreviews();
        var sorted = MergeTimeline(Messages, _activity.Values);
        GroupTimeline(sorted);

        // Keep existing item containers and the scroll position across live inserts and older pages.
        var remaining = sorted.ToHashSet();
        for (var i = Timeline.Count - 1; i >= 0; i--)
            if (!remaining.Contains(Timeline[i])) Timeline.RemoveAt(i);
        for (var i = 0; i < sorted.Length; i++)
            if (i == Timeline.Count || !ReferenceEquals(sorted[i], Timeline[i]))
                Timeline.Insert(i, sorted[i]);
    }

    internal static TimelineItem[] MergeTimeline(IEnumerable<MessageItem> messages, IEnumerable<GroupActivityLine> activity)
    {
        var ordered = messages.OrderBy(m => m.Sequence).ToArray();
        var result = new List<TimelineItem>();
        var index = 0;
        foreach (var entry in activity.OrderBy(a => a.SentAt).ThenBy(a => a.Event.Id))
        {
            while (index < ordered.Length && ordered[index].SentAt < entry.SentAt)
                result.Add(ordered[index++]);
            result.Add(entry);
        }
        result.AddRange(ordered.Skip(index));
        return result.ToArray();
    }
}

public sealed class GroupActivityLine : TimelineItem
{
    public GroupActivityItem Event { get; }
    public string Time => SentAt?.LocalDateTime.ToString("HH:mm") ?? "";
    public string ActionLabel => Event.Kind switch
    {
        "joined" => "вступление в группу",
        "left" => "выход из группы",
        "removed" => "исключение из группы",
        _ => ""
    };
    public string Description => $"{Name} · {ActionLabel}";
    public string Name => string.IsNullOrWhiteSpace(Event.DisplayName) ? "Удалённый аккаунт" : Event.DisplayName;
    public bool HasProfile => Event.UserId is > 0;

    public GroupActivityLine(GroupActivityItem item)
    {
        Event = item;
        if (DateTimeOffset.TryParse(item.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sent))
            SentAt = sent;
    }
}

public sealed record GroupActivityItem(long Id, string Kind, string? DisplayName, string OccurredAt, long? UserId);
