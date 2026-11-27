using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;

namespace MessengerDesktop;

public partial class ChatItem : ObservableObject, IDisposable
{
    public long Id { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Tag { get; init; }
    public string SearchSubtitle => Tag is null ? Kind : "@" + Tag;
    public string Kind { get; init; } = "";
    public bool IsGroup => Type == "group";
    public long? AvatarUserId { get; init; }
    public long AvatarColorKey => AvatarUserId ?? Id;
    public long LastMessageId { get; set; }
    private Bitmap? _avatar;
    public Bitmap? Avatar
    {
        get => _avatar;
        set
        {
            var previous = _avatar;
            if (!SetProperty(ref _avatar, value)) return;
            previous?.Dispose();
            OnPropertyChanged(nameof(HasAvatar));
            OnPropertyChanged(nameof(ShowInitial));
        }
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewLabel))] private string _preview = "Нет сообщений";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreviewAuthor)), NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private string _previewAuthor = "";
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private bool _unread;
    public bool HasAvatar => Avatar is not null;
    public bool ShowInitial => !HasAvatar;
    public bool HasPreviewAuthor => PreviewAuthor.Length > 0;
    public string PreviewLabel => HasPreviewAuthor ? $"{PreviewAuthor}: {Preview}" : Preview;

    internal void SetPreview(string? author, string? text)
    {
        PreviewAuthor = text != null && (IsGroup || author == "Вы") ? MessageMapper.Trim(author ?? "", 32) : "";
        Preview = text == null ? "Нет сообщений" : MessageMapper.Trim(text, 120);
    }

    public void Dispose() => Avatar = null;
}

public abstract partial class TimelineItem : ObservableObject
{
    public DateTimeOffset? SentAt { get; init; }
    [ObservableProperty] private bool _startsDay;
    public string DayLabel => SentAt?.LocalDateTime.Date switch
    {
        null => "",
        var date when date == DateTime.Today => "Сегодня",
        var date when date == DateTime.Today.AddDays(-1) => "Вчера",
        var date => date.Value.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
    };
}

public partial class MessageItem : TimelineItem
{
    [ObservableProperty] private bool _startsRun = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibleMeta))] private bool _endsMinute = true;
    public long Id { get; init; }
    public long SenderId { get; init; }
    public string SenderName { get; init; } = "";
    public bool Mine { get; init; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibleMeta))] private string _meta = "";
    public long? ReplyToId { get; init; }
    [ObservableProperty] private string _replyAuthor = "";
    [ObservableProperty] private string _reply = "";
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isNotStored;
    [ObservableProperty] private string _forwarded = "";
    [ObservableProperty] private string _forwardedTip = "";
    public bool HasReply => ReplyToId != null;
    public string VisibleMeta
    {
        get
        {
            if (EndsMinute) return Meta;
            var separator = Meta.IndexOf(" · ", StringComparison.Ordinal);
            return separator >= 0 ? Meta[(separator + 3)..] : "";
        }
    }
    public bool IsForwarded => Forwarded.Length > 0;
    public bool ShowSender => !Mine;

    partial void OnForwardedChanged(string value) => OnPropertyChanged(nameof(IsForwarded));

    public long MyId { get; init; }
    public Dictionary<string, HashSet<long>> ReactionUsers { get; private set; } = new();
    public HashSet<string> MyReactions { get; private set; } = new();
    [ObservableProperty] private IReadOnlyList<ReactionChip> _chips = Array.Empty<ReactionChip>();
    public bool HasReactions => Chips.Count > 0;
    partial void OnChipsChanged(IReadOnlyList<ReactionChip> value) => OnPropertyChanged(nameof(HasReactions));

    public void SetReactions(Dictionary<string, HashSet<long>> map)
    {
        ReactionUsers = map;
        RefreshReactions();
    }

    public void ApplyReaction(string emoji, long userId, bool add)
    {
        if (!ReactionUsers.TryGetValue(emoji, out var users))
        {
            if (!add) return;
            ReactionUsers[emoji] = users = new HashSet<long>();
        }
        if (add) users.Add(userId); else users.Remove(userId);
        if (users.Count == 0) ReactionUsers.Remove(emoji);
        RefreshReactions();
    }

    void RefreshReactions()
    {
        MyReactions = ReactionUsers.Where(kv => kv.Value.Contains(MyId)).Select(kv => kv.Key).ToHashSet();
        Chips = ReactionUsers
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ReactionChip
            {
                Emoji = kv.Key,
                Count = kv.Value.Count,
                Mine = kv.Value.Contains(MyId),
                Message = this
            })
            .ToList();
    }
}

public partial class MemberRow : ObservableObject, IDisposable
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string TagLabel { get; init; } = "";
    public string Detail => RoleLabel.Length == 0 ? TagLabel : $"{TagLabel} · {RoleLabel}";
    private Bitmap? _avatar;
    public Bitmap? Avatar
    {
        get => _avatar;
        set
        {
            var previous = _avatar;
            if (!SetProperty(ref _avatar, value)) return;
            previous?.Dispose();
            OnPropertyChanged(nameof(HasAvatar));
        }
    }
    public string Role { get; init; } = "";
    public string RoleLabel { get; init; } = "";
    public string PromoteLabel { get; init; } = "";
    public bool CanKick { get; init; }
    public bool CanPromote { get; init; }
    public bool CanTransfer { get; init; }
    public bool HasAvatar => Avatar is not null;

    public void Dispose() => Avatar = null;
}

public sealed class PinItem
{
    public long MessageId { get; init; }
    public long? SenderId { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string Text { get; set; } = "";
}

public sealed class ReactionChip
{
    public string Emoji { get; init; } = "";
    public int Count { get; init; }
    public bool Mine { get; init; }
    public MessageItem Message { get; init; } = null!;
}
