using CommunityToolkit.Mvvm.Input;

namespace MessengerDesktop;

public partial class ShellViewModel
{
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
            if (gen == _openGen) Error = Friendly(ex);
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

    /// <summary>После reconnect / close 4008: догнать пропущенное через after_id (не before_id).</summary>
    async Task CatchUpAfterReconnect()
    {
        var chat = SelectedChat;
        if (chat == null || Messages.Count == 0) return;
        var gen = _openGen;
        var afterId = Messages[^1].Id;
        try
        {
            while (true)
            {
                var page = await _api.GetMessages(chat.Id, 50, afterId: afterId);
                if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
                if (page.Count == 0) break;
                await Ui(() => MergePage(chat, page));
                afterId = page[^1].Id;
                if (page.Count < 50) break;
            }
            if (Messages.Count > 0)
            {
                try { await _api.MarkRead(chat.Id, Messages[^1].Id); } catch { }
            }
            RequestScroll();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    void Fill(ChatItem chat, List<MemberDto> members, List<MessageDto> messages, bool replace)
    {
        _names[_me.Id] = _me.DisplayName;
        foreach (var m in members) _names[m.Id] = m.DisplayName;
        _myRole = members.FirstOrDefault(m => m.Id == _me.Id)?.Role ?? "";
        OnPropertyChanged(nameof(CanDeleteEveryone));
        if (!replace) return;
        var texts = messages.ToDictionary(m => m.Id, MessageMapper.Body);
        Messages.Clear();
        foreach (var m in messages) Messages.Add(MessageMapper.ToItem(m, _me.Id, NameOf, texts));
        HasMore = messages.Count >= 50;
        if (messages.Count == 0)
        {
            chat.Preview = "Нет сообщений";
            chat.Time = "";
        }
        else
        {
            chat.Preview = MessageMapper.Preview(messages[^1], _me.Id, NameOf);
            chat.Time = MessageMapper.FormatTime(messages[^1].SentAt);
        }
    }

    [RelayCommand]
    async Task LoadOlder()
    {
        var chat = SelectedChat;
        if (chat == null || Messages.Count == 0 || !HasMore || Busy) return;
        Busy = true;
        try
        {
            var page = await _api.GetMessages(chat.Id, 50, beforeId: Messages[0].Id);
            await Ui(() =>
            {
                var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
                foreach (var m in page) texts[m.Id] = MessageMapper.Body(m);
                for (var i = 0; i < page.Count; i++)
                    Messages.Insert(i, MessageMapper.ToItem(page[i], _me.Id, NameOf, texts));
                HasMore = page.Count >= 50;
            });
        }
        catch (Exception ex) { Error = Friendly(ex); }
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
                Draft = "";
                await ReloadMessages();
            }
            else
            {
                // client_ref: повтор после сетевого сбоя не создаст дубль
                var clientRef = Guid.NewGuid().ToString("N");
                var id = await _api.SendMessage(chat.Id, text, ReplyTo?.Id, clientRef);
                ReplyTo = null;
                Draft = "";
                // Без полного Reload (Clear мигает «фантомами»): догоняем after_id, WS дедупится по id.
                await MergeAfter(chat, afterId: id - 1);
            }
        }
        catch (Exception ex) { Error = Friendly(ex); }
        finally { Busy = false; }
    }

    async Task MergeAfter(ChatItem chat, long afterId)
    {
        var gen = _openGen;
        try
        {
            var page = await _api.GetMessages(chat.Id, 50, afterId: afterId);
            if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
            await Ui(() => MergePage(chat, page));
            if (Messages.Count > 0)
            {
                try { await _api.MarkRead(chat.Id, Messages[^1].Id); } catch { }
            }
            RequestScroll();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    void MergePage(ChatItem chat, List<MessageDto> page)
    {
        var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
        foreach (var m in page) texts[m.Id] = MessageMapper.Body(m);
        foreach (var m in page)
        {
            var existing = Messages.FirstOrDefault(x => x.Id == m.Id);
            if (existing is null)
            {
                var idx = 0;
                while (idx < Messages.Count && Messages[idx].Id < m.Id) idx++;
                Messages.Insert(idx, MessageMapper.ToItem(m, _me.Id, NameOf, texts));
            }
            else
            {
                var fresh = MessageMapper.ToItem(m, _me.Id, NameOf, texts);
                existing.Text = fresh.Text;
                existing.Meta = fresh.Meta;
                existing.Reply = fresh.Reply;
                existing.IsDeleted = fresh.IsDeleted;
                existing.IsNotStored = fresh.IsNotStored;
            }
        }
        if (page.Count > 0)
        {
            chat.Preview = MessageMapper.Preview(page[^1], _me.Id, NameOf);
            chat.Time = MessageMapper.FormatTime(page[^1].SentAt);
        }
    }

    [RelayCommand] void Reply() { if (SelectedMessage != null) { Editing = null; ReplyTo = SelectedMessage; } }
    [RelayCommand] void BeginEdit() { if (CanEdit && SelectedMessage != null) { ReplyTo = null; Editing = SelectedMessage; Draft = SelectedMessage.Text; } }
    [RelayCommand] void CancelCompose() { ReplyTo = null; Editing = null; }
    [RelayCommand] void ToggleMoreReactions() => ShowMoreReactions = !ShowMoreReactions;

    partial void OnSelectedMessageChanged(MessageItem? value) => ShowMoreReactions = false;

    [RelayCommand]
    async Task DeleteForMe()
    {
        if (SelectedMessage == null) return;
        await Delete(SelectedMessage.Id, false);
    }

    private bool _reactionBusy;

    [RelayCommand]
    async Task ToggleReaction(string? emoji)
    {
        await ToggleReactionFor(SelectedMessage, emoji);
        ShowMoreReactions = false;
    }

    [RelayCommand]
    Task ToggleChip(ReactionChip? chip) => ToggleReactionFor(chip?.Message, chip?.Emoji);

    async Task ToggleReactionFor(MessageItem? msg, string? emoji)
    {
        if (msg == null || msg.IsDeleted || string.IsNullOrEmpty(emoji) || _reactionBusy) return;
        _reactionBusy = true;
        try
        {
            Error = null;
            var had = msg.MyReactions.Contains(emoji);
            if (had) await _api.RemoveReaction(msg.Id, emoji);
            else await _api.AddReaction(msg.Id, emoji);
            msg.ApplyReaction(emoji, _me.Id, add: !had);
        }
        catch (Exception ex) { Error = Friendly(ex); }
        finally { _reactionBusy = false; }
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
        catch (Exception ex) { Error = Friendly(ex); }
        finally { Busy = false; }
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
            case "avatar_changed" when e.UserId is { } userId:
                InvalidateAvatar(userId);
                _ = LoadAvatar(userId, image => RefreshAvatar(userId, image));
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
            case "reaction_added":
            case "reaction_removed":
                if (e.ChatId == SelectedChat?.Id && e.MessageId is { } mid && e.UserId is { } uid
                    && !string.IsNullOrEmpty(e.Emoji))
                    Messages.FirstOrDefault(m => m.Id == mid)?.ApplyReaction(e.Emoji, uid, add: e.Type == "reaction_added");
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
        chat.Preview = MessageMapper.Preview(message, _me.Id, NameOf);
        chat.Time = MessageMapper.FormatTime(message.SentAt);
        if (unread && SelectedChat?.Id != chat.Id) chat.Unread = true;
        if (SelectedChat?.Id != chat.Id) return;
        var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
        texts[message.Id] = MessageMapper.Body(message);
        var existing = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (existing == null)
        {
            var item = MessageMapper.ToItem(message, _me.Id, NameOf, texts);
            var idx = 0;
            while (idx < Messages.Count && Messages[idx].Id < item.Id) idx++;
            Messages.Insert(idx, item);
            if (idx == Messages.Count - 1) RequestScroll();
            if (unread) _ = _api.MarkRead(chat.Id, message.Id);
        }
        else
        {
            var fresh = MessageMapper.ToItem(message, _me.Id, NameOf, texts);
            existing.Text = fresh.Text;
            existing.Meta = fresh.Meta;
            existing.Reply = fresh.Reply;
            existing.IsDeleted = fresh.IsDeleted;
            existing.IsNotStored = fresh.IsNotStored;
            existing.SetReactions(fresh.ReactionUsers);
        }
    }

    void TouchPreview()
    {
        if (SelectedChat == null) return;
        var last = Messages.LastOrDefault();
        if (last == null)
        {
            SelectedChat.Preview = "Нет сообщений";
            SelectedChat.Time = "";
            return;
        }
        var who = last.Mine ? "Вы" : last.SenderName;
        SelectedChat.Preview = MessageMapper.Trim($"{who}: {last.Text}", 120);
        SelectedChat.Time = last.Meta.Split(" · ", 2, StringSplitOptions.None)[0];
    }
}
