using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

/// <summary>
/// Оболочка чата. Логика разнесена по partial-файлам:
/// Chats / Messages / Members / Profile — чтобы можно было править домены параллельно.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly Navigator _nav;
    private readonly MessengerClient _api;
    private readonly LiveConnection _live;
    private readonly Dictionary<long, string> _names = new();
    private readonly ConcurrentDictionary<long, Task<Bitmap?>> _avatarLoads = new();
    private readonly ConcurrentDictionary<long, int> _avatarVersions = new();
    private ProfileDto _me;
    private string _myRole = "";
    private int _openGen;
    private bool _suppress;
    private bool _closed;

    public event Action? ScrollToEnd;

    public ObservableCollection<ChatItem> Chats { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();
    public ObservableCollection<UserCardDto> SearchResults { get; } = new();
    public ObservableCollection<UserCardDto> GroupDraft { get; } = new();
    public ObservableCollection<MemberRow> Members { get; } = new();

    [ObservableProperty] private string _header;
    [ObservableProperty] private string _status = "подключение…";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _groupTitle = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _bio = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatar;
    [ObservableProperty] private bool _hasMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroup))]
    [NotifyPropertyChangedFor(nameof(HasChat))]
    [NotifyPropertyChangedFor(nameof(ChatTitle))]
    private ChatItem? _selectedChat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanDeleteEveryone))]
    [NotifyPropertyChangedFor(nameof(HasMessageActions))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    private MessageItem? _selectedMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeHint))]
    [NotifyPropertyChangedFor(nameof(HasComposeHint))]
    private MessageItem? _replyTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeHint))]
    [NotifyPropertyChangedFor(nameof(HasComposeHint))]
    private MessageItem? _editing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayTitle))]
    [NotifyPropertyChangedFor(nameof(OverlayOpen))]
    [NotifyPropertyChangedFor(nameof(ShowPeopleSearch))]
    private string _overlay = "";

    public bool IsGroup => SelectedChat?.Type == "group";
    public bool HasChat => SelectedChat != null;
    public bool HasAvatar => Avatar is not null;
    public string ChatTitle => SelectedChat?.Title ?? "Выберите чат";
    public bool OverlayOpen => Overlay.Length > 0;
    public bool ShowPeopleSearch => Overlay is "direct" or "group" or "members";
    public string OverlayTitle => Overlay switch
    {
        "direct" => "Новый личный чат",
        "group" => "Новая группа",
        "members" => "Участники",
        "profile" => "Профиль",
        _ => ""
    };
    public bool HasMessageActions => SelectedMessage != null;
    public bool CanEdit => SelectedMessage is { Mine: true, IsDeleted: false, IsNotStored: false };
    public bool CanDeleteEveryone =>
        SelectedMessage is { IsDeleted: false } && (SelectedMessage.Mine || _myRole is "owner" or "admin");
    public string SelectionHint => SelectedMessage is null ? "" : "Выбрано сообщение";
    public bool HasComposeHint => ComposeHint != null;
    public string? ComposeHint => Editing != null
        ? "Редактирование сообщения"
        : ReplyTo != null ? "Ответ: " + MessageMapper.Trim(ReplyTo.Text, 60) : null;

    public ShellViewModel(Navigator nav, MessengerClient api, ProfileDto me)
    {
        _nav = nav;
        _api = api;
        _me = me;
        _names[me.Id] = me.DisplayName;
        Header = $"{me.DisplayName}  @{me.Tag}";
        DisplayName = me.DisplayName;
        Bio = me.Bio ?? "";
        _ = LoadAvatar(me.Id, image => Avatar = image);
        _live = new LiveConnection(api);
        _live.Event += e => Dispatcher.UIThread.Post(() => Apply(e));
        _live.State += s => Dispatcher.UIThread.Post(() => Status = s);
        _live.SessionRevoked += () => Dispatcher.UIThread.Post(() => ForceLogout("Сессия завершена. Войдите снова."));
        _live.Reconnected += () => Dispatcher.UIThread.Post(() => _ = CatchUpAfterReconnect());
        _ = _live.RunAsync();
        _ = ReloadChats();
    }

    [RelayCommand] void CloseOverlay() => Overlay = "";
    [RelayCommand] void DismissError() => Error = null;

    [RelayCommand]
    async Task Logout()
    {
        try { await _api.Logout(); } catch { /* локальную сессию забываем в любом случае */ }
        ForceLogout(null);
    }

    void ForceLogout(string? error)
    {
        if (_closed) return;
        _closed = true;
        _live.Dispose();
        _api.Dispose();
        SessionStore.Clear();
        _nav.Go(new LoginViewModel(_nav, _api.ServerBase, error));
    }

    void ResetSearch()
    {
        SearchQuery = "";
        SearchResults.Clear();
    }

    string NameOf(long id) => id == _me.Id ? "Вы" : _names.TryGetValue(id, out var n) ? n : "#" + id;

    void RequestScroll() => Dispatcher.UIThread.Post(() => ScrollToEnd?.Invoke());

    static Task Ui(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    async Task LoadAvatar(long userId, Action<Bitmap?> apply)
    {
        var version = _avatarVersions.GetOrAdd(userId, 0);
        try
        {
            var image = await _avatarLoads.GetOrAdd(userId, FetchAvatar);
            await Ui(() =>
            {
                if (_avatarVersions.GetOrAdd(userId, 0) == version) apply(image);
            });
        }
        catch (Exception ex)
        {
            await Ui(() => Error = Friendly(ex));
        }
    }

    async Task<Bitmap?> FetchAvatar(long userId)
    {
        var bytes = await _api.GetAvatar(userId);
        if (bytes is null) return null;
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    void RefreshAvatar(long userId, Bitmap? image)
    {
        foreach (var chat in Chats.Where(c => c.AvatarUserId == userId)) chat.Avatar = image;
        foreach (var user in SearchResults.Concat(GroupDraft).Where(u => u.Id == userId)) user.Avatar = image;
        foreach (var member in Members.Where(m => m.Id == userId)) member.Avatar = image;
        if (userId == _me.Id) Avatar = image;
    }

    void InvalidateAvatar(long userId)
    {
        _avatarVersions.AddOrUpdate(userId, 1, (_, version) => version + 1);
        _avatarLoads.TryRemove(userId, out _);
    }

    string Friendly(Exception ex) => LoginViewModel.Friendly(ex);
}
