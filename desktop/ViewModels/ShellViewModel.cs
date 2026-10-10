using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Messenger.Core;
using Messenger.Core.Models;

namespace MessengerDesktop;

public partial class ShellViewModel : ObservableObject
{
    private readonly Navigator _nav;
    private readonly MessengerClient _api;
    private readonly LiveConnection _live;
    private readonly MessengerSession _core;
    private readonly Dictionary<long, string> _names = new();
    private ProfileDto _me;
    private string _myRole = "";
    private int _openGen;
    private bool _suppress;
    private bool _closed;
    private readonly CancellationTokenSource _stop = new();
    private bool _refreshing;
    private bool _refreshAgain;
    private int _messageLimit = 50;

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
    [ObservableProperty] private bool _saveHistory = true;
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
    public bool HasPending => Messages.Any(m => m.Id < 0);
    public bool HasChat => SelectedChat != null;
    public string ChatTitle => SelectedChat?.Title ?? "Выберите чат";
    public bool OverlayOpen => Overlay.Length > 0;
    public bool ShowPeopleSearch => Overlay is "direct" or "group" or "members";
    public string OverlayTitle => Overlay switch
    {
        "direct" => "Новый личный чат",
        "group" => "Новая группа",
        "members" => "Участники",
        "settings" => "Настройки",
        _ => ""
    };
    public bool HasMessageActions => SelectedMessage is { Id: > 0 };
    public bool CanEdit => SelectedMessage is { Id: > 0, Mine: true, IsDeleted: false, IsNotStored: false };
    public bool CanDeleteEveryone =>
        SelectedMessage is { Id: > 0, IsDeleted: false } && (SelectedMessage.Mine || _myRole is "owner" or "admin");
    public string SelectionHint => SelectedMessage is null ? "" : "Выбрано сообщение";
    public bool HasComposeHint => ComposeHint != null;
    public string? ComposeHint => Editing != null
        ? "Редактирование сообщения"
        : ReplyTo != null ? "Ответ: " + Trim(ReplyTo.Text, 60) : null;

    public ShellViewModel(
    Navigator nav,
    MessengerClient api,
    ProfileDto me,
    MessengerSession core)
    {
        _core = core;
        _nav = nav;
        _api = api;
        _me = me;
        _names[me.Id] = me.DisplayName;
        Header = $"{me.DisplayName}  @{me.Tag}";
        DisplayName = me.DisplayName;
        Bio = me.Bio ?? "";
        _live = new LiveConnection(api);
        _live.Event += e => _ = ApplyLive(e);
        _live.State += state => Dispatcher.UIThread.Post(() => { if (!_closed) Status = state; });
        _live.SessionRevoked += Revoke;
        _core.SessionRevoked += Revoke;
        _core.Status += state => Dispatcher.UIThread.Post(() => { if (!_closed) Status = state; });
        _core.Changed += () => Dispatcher.UIThread.Post(() => { if (!_closed) _ = RefreshLocal(); });
        _nav.Window.Closed += WindowClosed;
        _ = Task.Run(() => _live.RunAsync());
        _ = RefreshLocal();
        _ = Task.Run(() => _core.RunAsync(_stop.Token));
    }

    partial void OnSelectedChatChanged(ChatItem? value)
    {
        if (_suppress) return;
        SelectedMessage = null;
        ReplyTo = null;
        Editing = null;
        if (value == null)
        {
            ++_openGen;
            Messages.Clear();
            HasMore = false;
            return;
        }
        value.Unread = false;
        _ = OpenChat(value);
    }

    async Task OpenChat(ChatItem chat)
    {
        ++_openGen;
        _messageLimit = 50;
        try
        {
            await RefreshLocal();
            _live.Follow(chat.Id);
            await _core.MarkReadAsync(chat.Id, _stop.Token);
            RequestScroll();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    async Task ReloadMessages()
    {
        if (SelectedChat is not { } chat) return;
        await _core.SyncAsync(chat.Id, _stop.Token);
        await RefreshLocal();
    }

    // Database changes are coalesced on the UI thread. A late read cannot replace a different open chat.
    async Task RefreshLocal()
    {
        if (_closed) return;
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var gen = _openGen;
                var selected = SelectedChat?.Id;
                var list = await _core.GetChatsAsync(_stop.Token);
                var members = selected is { } id ? await _core.GetMembersAsync(id, _stop.Token) : new List<MemberDto>();
                var messages = selected is { } mid ? await _core.GetMessagesAsync(mid, _messageLimit, _stop.Token) : new List<MessageDto>();
                if (_closed) return;
                if (gen != _openGen) { _refreshAgain = true; continue; }
                _suppress = true;
                try
                {
                    Chats.Clear();
                    foreach (var c in list)
                    {
                        if (c.OtherUser != null) _names[c.OtherUser.Id] = c.OtherUser.DisplayName;
                        var item = MapChat(c);
                        item.Unread = c.Unread && item.Id != selected;
                        Chats.Add(item);
                        _live.Follow(item.Id);
                    }
                    SelectedChat = selected is { } sid ? Chats.FirstOrDefault(c => c.Id == sid) : null;
                }
                finally { _suppress = false; }
                if (SelectedChat is { } open)
                {
                    Fill(open, members, messages, replace: true);
                    if (Overlay == "members") FillMembersPanel(members);
                    await _core.MarkReadAsync(open.Id, _stop.Token);
                }
                else
                {
                    Messages.Clear(); HasMore = false; SelectedMessage = null;
                    if (Overlay == "members") { Overlay = ""; Members.Clear(); }
                }
            } while (_refreshAgain && !_closed);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { _refreshing = false; }
    }

    void Fill(ChatItem chat, List<MemberDto> members, List<MessageDto> messages, bool replace)
    {
        _names[_me.Id] = _me.DisplayName;
        foreach (var m in members) _names[m.Id] = m.DisplayName;
        _myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        OnPropertyChanged(nameof(CanDeleteEveryone));
        if (!replace) return;
        var texts = messages.ToDictionary(m => m.Id, Body);
        var lastId = Messages.LastOrDefault()?.LocalId;
        var selectedId = SelectedMessage?.LocalId;
        Messages.Clear();
        foreach (var m in messages) Messages.Add(ToItem(m, texts));
        SelectedMessage = Messages.FirstOrDefault(m => m.LocalId == selectedId);
        HasMore = messages.Count >= _messageLimit;
        OnPropertyChanged(nameof(HasPending));
        if (lastId != Messages.LastOrDefault()?.LocalId) RequestScroll();
        chat.Preview = messages.Count == 0 ? "Нет сообщений" : Preview(messages[^1]);
    }

    [RelayCommand]
    async Task ReloadChats()
    {
        try { await _core.SyncAsync(ct: _stop.Token); await RefreshLocal(); }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task LoadOlder()
    {
        if (SelectedChat == null || !HasMore || Busy) return;
        _messageLimit += 50;
        await RefreshLocal();
    }

    [RelayCommand]
    async Task RetryPending()
    {
        if (SelectedChat is not { } chat) return;
        try { Error = null; await _core.RetryAsync(chat.Id, _stop.Token); }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task Send()
    {
        var chat = SelectedChat;
        var text = Draft.Trim();
        if (chat == null || text.Length == 0 || Busy) return;
        if (text.Length > 4096) { Error = "Сообщение длиннее 4096 символов"; return; }
        Busy = true;
        try
        {
            Error = null;
            if (Editing is { } ed)
            {
                await _api.EditMessage(ed.Id, text);
                Editing = null;
                await _core.SyncAsync(chat.Id, _stop.Token);
            }
            else
            {
                await _core.QueueMessageAsync(chat.Id, text, ReplyTo?.LocalId, _stop.Token);
                ReplyTo = null;
            }
            if (SelectedChat?.Id == chat.Id && Draft.Trim() == text) Draft = "";
            await RefreshLocal();
            RequestScroll();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Busy = false; }
    }

    [RelayCommand] void Reply() { if (SelectedMessage is { Id: > 0, IsDeleted: false }) { Editing = null; ReplyTo = SelectedMessage; } }
    [RelayCommand] void BeginEdit() { if (CanEdit && SelectedMessage != null) { ReplyTo = null; Editing = SelectedMessage; Draft = SelectedMessage.Text; } }
    [RelayCommand] void CancelCompose() { ReplyTo = null; Editing = null; }

    [RelayCommand]
    async Task DeleteForMe()
    {
        if (SelectedMessage is not { Id: > 0 }) return;
        await Delete(SelectedMessage.Id, false);
    }

    [RelayCommand]
    async Task DeleteForEveryone()
    {
        if (SelectedMessage == null || !CanDeleteEveryone) return;
        await Delete(SelectedMessage.Id, true);
    }

    async Task Delete(long id, bool everyone)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Error = null;
            await _api.DeleteMessage(id, everyone);
            SelectedMessage = null;
            await ReloadMessages();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Busy = false; }
    }

    [RelayCommand] void ShowDirect() { ResetSearch(); Overlay = "direct"; }
    [RelayCommand] void ShowGroup() { ResetSearch(); GroupDraft.Clear(); GroupTitle = ""; Overlay = "group"; }
    [RelayCommand] void CloseOverlay() => Overlay = "";

    [RelayCommand]
    async Task ShowMembers()
    {
        if (SelectedChat == null) return;
        ResetSearch();
        Overlay = "members";
        try { await LoadMembersPanel(); }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task ShowSettings()
    {
        Overlay = "settings";
        try
        {
            var settings = await _api.GetSettings();
            var me = await _api.GetMe();
            await _core.UpdateProfileAsync(DesktopBootstrap.MapProfile(me));
            SessionStore.Save(_api.ServerBase, _api.Token!, me);
            await Ui(() =>
            {
                _me = me;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                SaveHistory = settings.SaveHistory;
                DisplayName = me.DisplayName;
                Bio = me.Bio ?? "";
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task Search()
    {
        var q = SearchQuery.Trim();
        if (q.Length == 0) { Error = "Введите тег или имя"; return; }
        try
        {
            Error = null;
            var users = await _api.Search(q);
            await Ui(() =>
            {
                SearchResults.Clear();
                foreach (var u in users) SearchResults.Add(u);
                if (users.Count == 0) Error = "Никого не нашли";
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task PickUser(UserCardDto user)
    {
        if (user.Id == _me.Id) { Error = "Это вы"; return; }
        try
        {
            Error = null;
            if (Overlay == "direct")
            {
                var id = await _api.CreateDirect(user.Id);
                Overlay = "";
                await ReloadChats();
                await Ui(() => SelectedChat = Chats.FirstOrDefault(c => c.Id == id));
            }
            else if (Overlay == "group")
            {
                if (GroupDraft.All(u => u.Id != user.Id))
                    GroupDraft.Add(user);
            }
            else if (Overlay == "members" && SelectedChat != null)
            {
                await _api.AddMember(SelectedChat.Id, user.Id);
                await LoadMembersPanel();
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    void RemoveDraft(UserCardDto user) => GroupDraft.Remove(user);

    [RelayCommand]
    async Task CreateGroup()
    {
        var title = GroupTitle.Trim();
        if (title.Length is < 1 or > 64) { Error = "Название группы: 1–64 символа"; return; }
        try
        {
            Error = null;
            var id = await _api.CreateGroup(title, GroupDraft.Select(u => u.Id));
            Overlay = "";
            await ReloadChats();
            await Ui(() => SelectedChat = Chats.FirstOrDefault(c => c.Id == id));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    async Task LoadMembersPanel()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        try { await _core.SyncAsync(chat.Id, _stop.Token); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { Error = "Нет связи. Показан сохранённый состав группы."; }
        var members = await _core.GetMembersAsync(chat.Id, _stop.Token);
        await Ui(() => FillMembersPanel(members));
    }

    void FillMembersPanel(List<MemberDto> members)
    {
        var myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        _myRole = myRole;
        var owner = myRole == "owner";
        var staff = myRole is "owner" or "admin";
        Members.Clear();
        foreach (var m in members)
        {
            _names[m.Id] = m.DisplayName;
            Members.Add(new MemberRow
            {
                Id = m.Id,
                Title = $"{m.DisplayName}  @{m.Tag}",
                Role = m.Role,
                RoleLabel = m.Role switch { "owner" => "владелец", "admin" => "админ", _ => "участник" },
                PromoteLabel = m.Role == "admin" ? "Снять админа" : "Сделать админом",
                CanKick = staff && m.Id != _me.Id && m.Role != "owner" && (m.Role != "admin" || owner),
                CanPromote = owner && m.Id != _me.Id && m.Role != "owner",
                CanTransfer = owner && m.Id != _me.Id
            });
        }
        OnPropertyChanged(nameof(CanDeleteEveryone));
    }

    [RelayCommand]
    async Task Kick(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.RemoveMember(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task ToggleAdmin(MemberRow row)
    {
        if (SelectedChat == null) return;
        try
        {
            await _api.SetRole(SelectedChat.Id, row.Id, row.Role == "admin" ? "member" : "admin");
            await LoadMembersPanel();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task Transfer(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.TransferOwner(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task LeaveChat()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        try
        {
            await _api.Leave(chat.Id);
            await _core.HideChatAsync(chat.Id, _stop.Token);
            Overlay = "";
            await Ui(() => { _suppress = true; Chats.Remove(chat); SelectedChat = null; _suppress = false; Messages.Clear(); });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task HideChat()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        try
        {
            await _api.HideChat(chat.Id);
            await _core.HideChatAsync(chat.Id, _stop.Token);
            Overlay = "";
            await Ui(() => { _suppress = true; Chats.Remove(chat); SelectedChat = null; _suppress = false; Messages.Clear(); });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task SaveSettings()
    {
        var name = DisplayName.Trim();
        if (name.Length is < 1 or > 32) { Error = "Имя: 1–32 символа"; return; }
        if (Bio.Length > 500) { Error = "О себе длиннее 500 символов"; return; }
        try
        {
            Error = null;
            await _api.UpdateSettings(SaveHistory);
            await _api.UpdateProfile(name, Bio);
            var me = await _api.GetMe();
            await _core.UpdateProfileAsync(DesktopBootstrap.MapProfile(me));
            SessionStore.Save(_api.ServerBase, _api.Token!, me);
            await Ui(() =>
            {
                _me = me;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                Overlay = "";
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    [RelayCommand]
    async Task Logout()
    {
        try { await _api.Logout(); } catch { /* локальную сессию забываем в любом случае */ }
        ForceLogout(null);
    }

    [RelayCommand] void DismissError() => Error = null;

    void ForceLogout(string? error)
    {
        if (_closed) return;
        Close();
        SessionStore.Clear();
        _nav.Go(new LoginViewModel(_nav, _api.ServerBase, error));
    }

    void Revoke() => Dispatcher.UIThread.Post(() => ForceLogout("Сессия завершена. Войдите снова."));

    void WindowClosed(object? sender, EventArgs e) => Close();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _nav.Window.Closed -= WindowClosed;
        _stop.Cancel();
        _live.Dispose();
        _api.Dispose();
    }

    void ShowError(Exception ex)
    {
        if (_closed) return;
        if (ex is ApiException { Status: 401 }) ForceLogout("Сессия завершена. Войдите снова.");
        else Error = LoginViewModel.Friendly(ex);
    }

    async Task ApplyLive(WsEventDto e)
    {
        try { await _core.ApplyAsync(e, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { await Ui(() => ShowError(ex)); }
    }

    ChatItem MapChat(ChatDto c)
    {
        var title = c.Type == "group"
            ? (string.IsNullOrWhiteSpace(c.Title) ? "Группа" : c.Title)
            : (c.OtherUser?.DisplayName ?? c.OtherUser?.Tag ?? "Личный чат");
        return new ChatItem
        {
            Id = c.Id,
            Type = c.Type,
            Title = title,
            Kind = c.Type == "group" ? "Группа" : "Личный чат",
            Preview = c.LastMessage is null ? "Нет сообщений" : Preview(c.LastMessage)
        };
    }

    MessageItem ToItem(MessageDto m, IReadOnlyDictionary<long, string> texts)
    {
        var reply = "";
        if (m.ReplyToId is { } rid)
            reply = texts.TryGetValue(rid, out var t) ? "↩ " + Trim(t, 80) : "↩ ответ";
        var meta = FormatTime(m.SentAt);
        if (m.SyncState == MessageSyncState.Pending) meta += " · в очереди";
        if (m.SyncState == MessageSyncState.Failed) meta += " · не отправлено";
        if (m.EditedAt != null) meta += " · изменено";
        if (m.SenderId == _me.Id && m.ReadAt != null) meta += " · прочитано";
        else if (m.SenderId == _me.Id && m.DeliveredAt != null) meta += " · доставлено";
        return new MessageItem
        {
            Id = m.Id,
            LocalId = m.LocalId,
            SenderId = m.SenderId,
            SenderName = m.SenderId == _me.Id ? "Вы" : m.LocalSenderName ?? NameOf(m.SenderId),
            Mine = m.SenderId == _me.Id,
            Text = Body(m),
            Meta = meta,
            Reply = reply,
            IsDeleted = m.ContentState == "deleted",
            IsNotStored = m.ContentState == "not_stored"
        };
    }

    string Preview(MessageDto m)
    {
        var who = m.SenderId == _me.Id ? "Вы" : m.LocalSenderName ?? NameOf(m.SenderId);
        return $"{who}: {Body(m)}";
    }

    string NameOf(long id) => id == _me.Id ? "Вы" : _names.TryGetValue(id, out var n) ? n : "#" + id;

    static string Body(MessageDto m) => m.ContentState switch
    {
        "deleted" => "Сообщение удалено",
        "not_stored" when string.IsNullOrEmpty(m.Text) => "Текст не сохранён на сервере",
        _ => m.Text ?? ""
    };

    static string FormatTime(string? iso)
    {
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t))
            return t.ToLocalTime().ToString("HH:mm");
        return "";
    }

    static string Trim(string text, int n) => text.Length <= n ? text : text[..n] + "…";

    void ResetSearch()
    {
        SearchQuery = "";
        SearchResults.Clear();
    }

    void RequestScroll() => Dispatcher.UIThread.Post(() => ScrollToEnd?.Invoke());

    static Task Ui(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
