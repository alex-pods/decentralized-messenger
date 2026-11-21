using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    partial void OnSelectedChatChanged(ChatItem? value)
    {
        if (_suppress) return;
        SelectedMessage = null;
        ReplyTo = null;
        Editing = null;
        RestoreDraft(value);
        if (Overlay == "pins") Overlay = "";
        ClearPins();
        if (value == null)
        {
            Messages.Clear();
            HasMore = false;
            return;
        }
        value.Unread = false;
        _ = OpenChat(value);
    }

    [RelayCommand]
    async Task ReloadChats()
    {
        var selected = SelectedChat?.Id;
        var unread = Chats.Where(c => c.Unread).Select(c => c.Id).ToHashSet();
        List<ChatDto> list;
        try
        {
            list = await LoadChatList();
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
            return;
        }

        try
        {
            await Ui(() =>
            {
                _suppress = true;
                DisposeItems(Chats);
                foreach (var c in list)
                {
                    if (c.OtherUser != null) _names[c.OtherUser.Id] = c.OtherUser.DisplayName;
                    var item = MessageMapper.MapChat(c, _me.Id, NameOf);
                    item.Unread = unread.Contains(item.Id) && item.Id != selected;
                    Chats.Add(item);
                    if (item.AvatarUserId is { } avatarUserId)
                        _ = LoadAvatar(avatarUserId, image =>
                        {
                            if (Chats.Contains(item)) item.Avatar = CreateAvatar(image);
                        });
                    _live.Follow(item.Id); // member_added/left приходят только по follow
                }
                _suppress = false;
                SelectedChat = selected is { } sid ? Chats.FirstOrDefault(c => c.Id == sid) : null;
            });
            if (Chats.Count > 0) Error = null;
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    async Task<List<ChatDto>> LoadChatList()
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return await _api.GetChats(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
                await Task.Delay(400 * (attempt + 1));
            }
        }
        throw last!;
    }

    [RelayCommand] void ShowDirect() { ResetSearch(); Overlay = "direct"; }
    [RelayCommand]
    void ShowGroup()
    {
        ResetSearch();
        DisposeItems(GroupDraft, SearchResults);
        GroupTitle = "";
        Overlay = "group";
    }

    [RelayCommand]
    async Task Search()
    {
        var q = SearchQuery.Trim().TrimStart('@');
        if (q.Length == 0) { SearchHint = "Введите полный тег пользователя"; return; }
        var generation = ++_searchGeneration;
        var overlay = Overlay;
        Searching = true;
        SearchHint = "Ищем…";
        Error = null;
        try
        {
            var users = await _api.Search(q);
            if (generation != _searchGeneration || overlay != Overlay) return;
            DisposeItems(SearchResults, GroupDraft);
            foreach (var u in users)
            {
                SearchResults.Add(u);
                _ = LoadAvatar(u.Id, image =>
                {
                    if (SearchResults.Contains(u) || GroupDraft.Contains(u)) u.Avatar = CreateAvatar(image);
                });
            }
            SearchHint = users.Count == 0 ? "Никого не нашли. Проверьте полный тег." : "Выберите человека в списке";
        }
        catch (Exception ex) { if (generation == _searchGeneration && overlay == Overlay) { Error = Friendly(ex); SearchHint = ""; } }
        finally { if (generation == _searchGeneration) Searching = false; }
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
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand]
    void RemoveDraft(UserCardDto user) => DisposeRemoved(GroupDraft, user, SearchResults);

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
        catch (Exception ex) { Error = Friendly(ex); }
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
            await Ui(() =>
            {
                _suppress = true;
                DisposeRemoved(Chats, chat);
                SelectedChat = null;
                _suppress = false;
                Messages.Clear();
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
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
            await Ui(() =>
            {
                _suppress = true;
                DisposeRemoved(Chats, chat);
                SelectedChat = null;
                _suppress = false;
                Messages.Clear();
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    void DropChat(long id)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == id);
        if (chat == null) return;
        _suppress = true;
        DisposeRemoved(Chats, chat);
        if (SelectedChat?.Id == id)
        {
            SelectedChat = null;
            Messages.Clear();
            Overlay = "";
        }
        _suppress = false;
    }
}
