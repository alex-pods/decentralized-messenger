using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;

namespace MessengerDesktop;

public partial class ChatItem : ObservableObject, IDisposable
{
    public long Id { get; init; }
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool IsGroup => Type == "group";
    public long? AvatarUserId { get; init; }
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
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private bool _unread;
    public bool HasAvatar => Avatar is not null;
    public bool ShowInitial => !IsGroup && !HasAvatar;

    public void Dispose() => Avatar = null;
}

public partial class MessageItem : ObservableObject
{
    public long Id { get; init; }
    public long SenderId { get; init; }
    public string SenderName { get; init; } = "";
    public bool Mine { get; init; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _reply = "";
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isNotStored;
    public bool HasReply => Reply.Length > 0;
    public bool ShowSender => !Mine;

    partial void OnReplyChanged(string value) => OnPropertyChanged(nameof(HasReply));

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

public sealed class ReactionChip
{
    public string Emoji { get; init; } = "";
    public int Count { get; init; }
    public bool Mine { get; init; }
    public MessageItem Message { get; init; } = null!;
}
