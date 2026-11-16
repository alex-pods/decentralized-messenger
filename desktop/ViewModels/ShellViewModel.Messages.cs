using Avalonia.Threading;
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
            await LoadPins(chat, gen);
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
        await LoadPins(chat, gen);
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
        OnPropertyChanged(nameof(CanManagePins));
        OnPropertyChanged(nameof(CanPin));
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
        var gen = _openGen;
        var fetch = ++_historyFetch;
        var beforeId = Messages[0].Id;
        Busy = true;
        try
        {
            var page = await _api.GetMessages(chat.Id, 50, beforeId: beforeId);
            if (fetch != _historyFetch || !SameChat(gen, chat.Id)) return;
            await Ui(() =>
            {
                if (fetch != _historyFetch || !SameChat(gen, chat.Id)) return;
                InsertOlder(page);
                HasMore = page.Count >= 50;
            });
        }
        catch (Exception ex)
        {
            if (gen == _openGen) Error = Friendly(ex);
        }
        finally { if (fetch == _historyFetch) Busy = false; }
    }

    void InsertOlder(List<MessageDto> page)
    {
        if (page.Count == 0) return;
        var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
        foreach (var m in page) texts[m.Id] = MessageMapper.Body(m);
        for (var i = 0; i < page.Count; i++)
            Messages.Insert(i, MessageMapper.ToItem(page[i], _me.Id, NameOf, texts));
    }

    bool SameChat(int gen, long chatId) =>
        gen == _openGen && SelectedChat?.Id == chatId;

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

    async Task LoadPins(ChatItem chat, int gen)
    {
        try
        {
            var pins = await _api.GetPins(chat.Id);
            if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
            await Ui(() => ReplacePins(pins));
        }
        catch (Exception ex)
        {
            if (gen == _openGen) Error = Friendly(ex);
        }
    }

    void ReplacePins(List<PinDto> pins)
    {
        _pins.Clear();
        foreach (var pin in pins.OrderByDescending(p => p.MessageId))
        {
            if (pin.Message is null) continue;
            _pins.Add(new PinItem
            {
                MessageId = pin.MessageId,
                Text = MessageMapper.Trim(MessageMapper.Body(pin.Message), 90)
            });
        }
        _pinIndex = 0;
        NotifyPins();
    }

    void ClearPins()
    {
        _pins.Clear();
        _pinIndex = 0;
        NotifyPins();
    }

    void NotifyPins()
    {
        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(PinCounter));
        OnPropertyChanged(nameof(SelectedIsPinned));
        OnPropertyChanged(nameof(PinActionLabel));
    }

    void UpsertPin(MessageDto message)
    {
        _pins.RemoveAll(p => p.MessageId == message.Id);
        var item = new PinItem
        {
            MessageId = message.Id,
            Text = MessageMapper.Trim(MessageMapper.Body(message), 90)
        };
        var at = _pins.FindIndex(p => p.MessageId < message.Id);
        if (at < 0) _pins.Add(item);
        else _pins.Insert(at, item);
        _pinIndex = 0;
        NotifyPins();
    }

    void RemovePin(long messageId)
    {
        if (_pins.RemoveAll(p => p.MessageId == messageId) == 0) return;
        if (_pinIndex >= _pins.Count) _pinIndex = Math.Max(0, _pins.Count - 1);
        NotifyPins();
    }

    /// <summary>
    /// Как в Telegram: в полоске — самый нижний ещё не пролистанный закреп.
    /// После прохода через закреп вверх показывается следующий выше по чату.
    /// </summary>
    public void SyncPinToVisible(MessageItem lastVisible)
    {
        if (_pins.Count == 0) return;
        if (Environment.TickCount64 < _pinSyncQuietUntil) return;

        var lastIdx = Messages.IndexOf(lastVisible);
        if (lastIdx < 0) return;

        var pinMsgIndices = new int[_pins.Count];
        for (var i = 0; i < _pins.Count; i++)
        {
            pinMsgIndices[i] = -1;
            for (var m = 0; m < Messages.Count; m++)
            {
                if (Messages[m].Id != _pins[i].MessageId) continue;
                pinMsgIndices[i] = m;
                break;
            }
        }

        var next = PinStrip.IndexForScroll(lastIdx, pinMsgIndices);
        if (next == _pinIndex) return;
        _pinIndex = next;
        NotifyPins();
    }

    /// <summary>
    /// Переход к закрепу. Если его нет в загруженном окне, история догружается
    /// страницами по 50 вверх и вставляется в список одним обновлением на UI-потоке.
    /// Смена чата или новый клик отменяют переход и не двигают ленту закрепов.
    /// </summary>
    [RelayCommand]
    async Task ShowPinned()
    {
        var chat = SelectedChat;
        if (chat == null || _pins.Count == 0) return;
        var gen = _openGen;
        var jump = ++_pinJump;
        var index = Math.Clamp(_pinIndex, 0, _pins.Count - 1);
        var messageId = _pins[index].MessageId;
        Error = null;

        if (Messages.Any(m => m.Id == messageId))
        {
            RevealPinned(messageId);
            return;
        }

        var fetch = ++_historyFetch;
        Busy = true;
        var older = new List<MessageDto>();
        var hasMore = HasMore;
        var beforeId = Messages.Count == 0 ? (long?)null : Messages[0].Id;
        try
        {
            while (older.All(m => m.Id != messageId))
            {
                if (!SameJump(gen, jump, chat.Id)) return;
                if (beforeId is null || !hasMore || beforeId <= messageId)
                    break;
                var page = await _api.GetMessages(chat.Id, 50, beforeId: beforeId.Value);
                if (fetch != _historyFetch || !SameJump(gen, jump, chat.Id)) return;
                if (page.Count == 0 || page[0].Id >= beforeId)
                {
                    hasMore = false;
                    break;
                }
                older.InsertRange(0, page);
                beforeId = page[0].Id;
                hasMore = page.Count >= 50;
            }

            await Ui(() =>
            {
                if (fetch != _historyFetch || !SameJump(gen, jump, chat.Id)) return;
                if (older.Count > 0)
                    PrependMessages(older);
                HasMore = hasMore;
                RevealPinned(messageId);
            });
        }
        catch (Exception ex)
        {
            if (SameJump(gen, jump, chat.Id))
                await Ui(() => { if (SameJump(gen, jump, chat.Id)) Error = Friendly(ex); });
        }
        finally
        {
            if (fetch == _historyFetch)
                await Ui(() => { if (fetch == _historyFetch) Busy = false; });
        }
    }

    void PrependMessages(List<MessageDto> older)
    {
        // Вставляем в начало, без Clear() всей ленты — меньше шансов сломать раскладку списка.
        var texts = Messages.ToDictionary(m => m.Id, m => m.Text);
        foreach (var message in older)
            texts[message.Id] = MessageMapper.Body(message);
        var prefix = new MessageItem[older.Count];
        for (var i = 0; i < older.Count; i++)
            prefix[i] = MessageMapper.ToItem(older[i], _me.Id, NameOf, texts);
        for (var i = prefix.Length - 1; i >= 0; i--)
            Messages.Insert(0, prefix[i]);
    }

    void RevealPinned(long messageId)
    {
        var item = Messages.FirstOrDefault(m => m.Id == messageId);
        if (item == null)
        {
            Error = "Закреплённое сообщение не найдено в истории";
            return;
        }

        SelectedMessage = item;
        var target = item;
        // ScrollIntoView даёт пачку ScrollChanged — не даём им откатить смену полоски после клика.
        _pinSyncQuietUntil = Environment.TickCount64 + 450;
        Dispatcher.UIThread.Post(() =>
        {
            try { ScrollToMessage?.Invoke(target); }
            catch { /* список мог уже смениться */ }
        }, DispatcherPriority.Background);
        var at = _pins.FindIndex(p => p.MessageId == messageId);
        if (at >= 0 && _pins.Count > 1)
            _pinIndex = (at + 1) % _pins.Count;
        NotifyPins();
    }

    bool SameJump(int gen, int jump, long chatId) =>
        jump == _pinJump && SameChat(gen, chatId);

    [RelayCommand]
    async Task UnpinShown()
    {
        var chat = SelectedChat;
        if (chat == null || !CanManagePins || _pins.Count == 0) return;
        var pin = _pins[Math.Clamp(_pinIndex, 0, _pins.Count - 1)];
        try
        {
            Error = null;
            await _api.UnpinMessage(chat.Id, pin.MessageId);
            RemovePin(pin.MessageId);
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand]
    async Task TogglePin()
    {
        var chat = SelectedChat;
        var message = SelectedMessage;
        if (chat == null || message == null || !CanPin) return;
        try
        {
            Error = null;
            if (SelectedIsPinned)
            {
                await _api.UnpinMessage(chat.Id, message.Id);
                RemovePin(message.Id);
            }
            else
            {
                await _api.PinMessage(chat.Id, message.Id);
                UpsertPin(new MessageDto
                {
                    Id = message.Id,
                    Text = message.Text,
                    ContentState = message.IsDeleted ? "deleted" : "stored"
                });
            }
        }
        catch (Exception ex) { Error = Friendly(ex); }
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
                if (e.ChatId == SelectedChat?.Id)
                {
                    var pinned = _pins.FirstOrDefault(p => p.MessageId == e.Message.Id);
                    if (pinned != null)
                    {
                        pinned.Text = MessageMapper.Trim(MessageMapper.Body(e.Message), 90);
                        NotifyPins();
                    }
                }
                break;
            case "message_pinned" when e.ChatId == SelectedChat?.Id && e.Message != null:
                UpsertPin(e.Message);
                break;
            case "message_unpinned" when e.ChatId == SelectedChat?.Id && e.MessageId is { } unpinned:
                RemovePin(unpinned);
                break;
            case "message_deleted":
                if (e.ForEveryone != false && e.MessageId is { } dropped)
                    RemovePin(dropped);
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
