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
