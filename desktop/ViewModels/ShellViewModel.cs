using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel : ObservableObject
{
    private readonly Navigator _nav;
    private readonly MessengerClient _api;
    private readonly LiveConnection _live;
    private readonly Dictionary<long, string> _names = new();
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
    public bool HasMessageActions => SelectedMessage != null;
    public bool CanEdit => SelectedMessage is { Mine: true, IsDeleted: false, IsNotStored: false };
    public bool CanDeleteEveryone =>
        SelectedMessage is { IsDeleted: false } && (SelectedMessage.Mine || _myRole is "owner" or "admin");
    public string SelectionHint => SelectedMessage is null ? "" : "Выбрано сообщение";
    public bool HasComposeHint => ComposeHint != null;
    public string? ComposeHint => Editing != null
        ? "Редактирование сообщения"
        : ReplyTo != null ? "Ответ: " + Trim(ReplyTo.Text, 60) : null;

    public ShellViewModel(Navigator nav, MessengerClient api, ProfileDto me)
    {
        _nav = nav;
        _api = api;
        _me = me;
        _names[me.Id] = me.DisplayName;
        Header = $"{me.DisplayName}  @{me.Tag}";
        DisplayName = me.DisplayName;
        Bio = me.Bio ?? "";
        _live = new LiveConnection(api);
        _live.Event += e => Dispatcher.UIThread.Post(() => Apply(e));
        _live.State += s => Dispatcher.UIThread.Post(() => Status = s);
        _live.SessionRevoked += () => Dispatcher.UIThread.Post(() => ForceLogout("Сессия завершена. Войдите снова."));
        _ = _live.RunAsync();
        _ = ReloadChats();
    }

    partial void OnSelectedChatChanged(ChatItem? value)
    {
        if (_suppress) return;
        SelectedMessage = null;
        ReplyTo = null;
        Editing = null;
        if (value == null)
        {
            Messages.Clear();
            HasMore = false;
            return;
        }
        value.Unread = false;
        _ = OpenChat(value);
    }

    async Task OpenChat(ChatItem chat)
    {
        var gen = ++_openGen;
        try
        {
            Error = null;
            var members = await _api.GetMembers(chat.Id);
            var messages = await _api.GetMessages(chat.Id, 50);
            if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
            await Ui(() => Fill(chat, members, messages, replace: true));
            _live.Follow(chat.Id);
            if (messages.Count > 0)
            {
                try { await _api.MarkRead(chat.Id, messages[^1].Id); }
                catch { /* прочтение не блокирует чат */ }
            }
            RequestScroll();
        }
        catch (Exception ex)
        {
            if (gen == _openGen) Error = LoginViewModel.Friendly(ex);
        }
    }

    async Task ReloadMessages()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        var gen = _openGen;
        var members = await _api.GetMembers(chat.Id);
        var messages = await _api.GetMessages(chat.Id, 50);
        if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
        await Ui(() => Fill(chat, members, messages, replace: true));
        if (messages.Count > 0)
        {
            try { await _api.MarkRead(chat.Id, messages[^1].Id); } catch { }
        }
        RequestScroll();
    }

    void Fill(ChatItem chat, List<MemberDto> members, List<MessageDto> messages, bool replace)
    {
        _names[_me.Id] = _me.DisplayName;
        foreach (var m in members) _names[m.Id] = m.DisplayName;
        _myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        OnPropertyChanged(nameof(CanDeleteEveryone));
        if (!replace) return;
        var texts = messages.ToDictionary(m => m.Id, Body);
        Messages.Clear();
        foreach (var m in messages) Messages.Add(ToItem(m, texts));
        HasMore = messages.Count >= 50;
        chat.Preview = messages.Count == 0 ? "Нет сообщений" : Preview(messages[^1]);
    }

    [RelayCommand]
    async Task ReloadChats()
    {
        try
        {
            var selected = SelectedChat?.Id;
            var unread = Chats.Where(c => c.Unread).Select(c => c.Id).ToHashSet();
            var list = await _api.GetChats();
            await Ui(() =>
            {
                _suppress = true;
                Chats.Clear();
                foreach (var c in list)
                {
                    if (c.OtherUser != null) _names[c.OtherUser.Id] = c.OtherUser.DisplayName;
                    var item = MapChat(c);
                    item.Unread = unread.Contains(item.Id) && item.Id != selected;
                    Chats.Add(item);
                    _live.Follow(item.Id);
                }
                _suppress = false;
                SelectedChat = selected is { } sid ? Chats.FirstOrDefault(c => c.Id == sid) : null;
            });
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
    }

    [RelayCommand]
    async Task LoadOlder()
    {
        var chat = SelectedChat;
        if (chat == null || Messages.Count == 0 || !HasMore || Busy) return;
        Busy = true;
        try
        {
            var page = await _api.GetMessages(chat.Id, 50, Messages[0].Id);
            await Ui(() =>
            {
                var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
                foreach (var m in page) texts[m.Id] = Body(m);
                for (var i = 0; i < page.Count; i++)
                    Messages.Insert(i, ToItem(page[i], texts));
                HasMore = page.Count >= 50;
            });
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
        finally { Busy = false; }
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
            }
            else
            {
                await _api.SendMessage(chat.Id, text, ReplyTo?.Id);
                ReplyTo = null;
            }
            Draft = "";
            await ReloadMessages();
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
        finally { Busy = false; }
    }

    [RelayCommand] void Reply() { if (SelectedMessage != null) { Editing = null; ReplyTo = SelectedMessage; } }
    [RelayCommand] void BeginEdit() { if (CanEdit && SelectedMessage != null) { ReplyTo = null; Editing = SelectedMessage; Draft = SelectedMessage.Text; } }
    [RelayCommand] void CancelCompose() { ReplyTo = null; Editing = null; }

    [RelayCommand]
    async Task DeleteForMe()
    {
        if (SelectedMessage == null) return;
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        await LoadMembersPanel();
    }

    [RelayCommand]
    async Task ShowSettings()
    {
        Overlay = "settings";
        try
        {
            var settings = await _api.GetSettings();
            var me = await _api.GetMe();
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
    }

    async Task LoadMembersPanel()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        var members = await _api.GetMembers(chat.Id);
        var myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        _myRole = myRole;
        var owner = myRole == "owner";
        var staff = myRole is "owner" or "admin";
        await Ui(() =>
        {
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
        });
    }

    [RelayCommand]
    async Task Kick(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.RemoveMember(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
    }

    [RelayCommand]
    async Task Transfer(MemberRow row)
    {
        if (SelectedChat == null) return;
        try { await _api.TransferOwner(SelectedChat.Id, row.Id); await LoadMembersPanel(); }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
    }

    [RelayCommand]
    async Task LeaveChat()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        try
        {
            await _api.Leave(chat.Id);
            Overlay = "";
            await Ui(() => { _suppress = true; Chats.Remove(chat); SelectedChat = null; _suppress = false; Messages.Clear(); });
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
    }

    [RelayCommand]
    async Task HideChat()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        try
        {
            await _api.HideChat(chat.Id);
            Overlay = "";
            await Ui(() => { _suppress = true; Chats.Remove(chat); SelectedChat = null; _suppress = false; Messages.Clear(); });
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
            await Ui(() =>
            {
                _me = me;
                _names[me.Id] = me.DisplayName;
                Header = $"{me.DisplayName}  @{me.Tag}";
                Overlay = "";
            });
        }
        catch (Exception ex) { Error = LoginViewModel.Friendly(ex); }
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
        _closed = true;
        _live.Dispose();
        _api.Dispose();
        SessionStore.Clear();
        _nav.Go(new LoginViewModel(_nav, _api.ServerBase, error));
    }

    void Apply(WsEventDto e)
    {
        switch (e.Type)
        {
            case "message" when e.Message != null:
                Incoming(e.ChatId, e.Message, unread: e.Message.SenderId != _me.Id);
                break;
            case "message_edited" when e.Message != null:
                Incoming(e.ChatId, e.Message, unread: false);
                break;
            case "message_deleted":
                if (e.ForEveryone == false && e.ChatId == SelectedChat?.Id && e.MessageId is { } hid)
                {
                    var gone = Messages.FirstOrDefault(m => m.Id == hid);
                    if (gone != null) Messages.Remove(gone);
                    TouchPreview();
                }
                else if (e.Message != null)
                    Incoming(e.ChatId, e.Message, unread: false);
                else
                    _ = ReloadChats();
                break;
            case "chat_created":
            case "chat_hidden":
            case "chat_deleted":
                _ = ReloadChats();
                break;
            case "member_removed":
            case "member_left":
                if (e.UserId == _me.Id && e.ChatId is { } left)
                    DropChat(left);
                else if (e.ChatId == SelectedChat?.Id && Overlay == "members")
                    _ = LoadMembersPanel();
                break;
            case "member_added":
            case "member_role_changed":
            case "owner_changed":
                if (e.ChatId == SelectedChat?.Id && Overlay == "members")
                    _ = LoadMembersPanel();
                break;
            case "read":
                if (SelectedChat is { } open && e.ChatId == open.Id && open.Type != "group"
                    && e.UserId != _me.Id && e.LastReadMessageId is { } upto)
                {
                    foreach (var m in Messages)
                    {
                        if (!m.Mine || m.Id > upto || m.Meta.Contains("прочитано")) continue;
                        m.Meta = m.Meta.Replace(" · доставлено", "") + " · прочитано";
                    }
                }
                break;
        }
    }

    void Incoming(long? chatId, MessageDto message, bool unread)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null)
        {
            _ = ReloadChats();
            return;
        }
        chat.Preview = Preview(message);
        if (unread && SelectedChat?.Id != chat.Id) chat.Unread = true;
        if (SelectedChat?.Id != chat.Id) return;
        var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
        texts[message.Id] = Body(message);
        var existing = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (existing == null)
        {
            var item = ToItem(message, texts);
            var idx = 0;
            while (idx < Messages.Count && Messages[idx].Id < item.Id) idx++;
            Messages.Insert(idx, item);
            if (idx == Messages.Count - 1) RequestScroll();
            if (unread) _ = _api.MarkRead(chat.Id, message.Id);
        }
        else
        {
            var fresh = ToItem(message, texts);
            existing.Text = fresh.Text;
            existing.Meta = fresh.Meta;
            existing.Reply = fresh.Reply;
            existing.IsDeleted = fresh.IsDeleted;
            existing.IsNotStored = fresh.IsNotStored;
        }
    }

    void DropChat(long id)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == id);
        if (chat == null) return;
        _suppress = true;
        Chats.Remove(chat);
        if (SelectedChat?.Id == id)
        {
            SelectedChat = null;
            Messages.Clear();
            Overlay = "";
        }
        _suppress = false;
    }

    void TouchPreview()
    {
        if (SelectedChat == null) return;
        var last = Messages.LastOrDefault();
        SelectedChat.Preview = last == null ? "Нет сообщений" : $"{last.SenderName}: {last.Text}";
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
        if (m.EditedAt != null) meta += " · изменено";
        if (m.SenderId == _me.Id && m.ReadAt != null) meta += " · прочитано";
        else if (m.SenderId == _me.Id && m.DeliveredAt != null) meta += " · доставлено";
        return new MessageItem
        {
            Id = m.Id,
            SenderId = m.SenderId,
            SenderName = NameOf(m.SenderId),
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
        var who = m.SenderId == _me.Id ? "Вы" : NameOf(m.SenderId);
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
