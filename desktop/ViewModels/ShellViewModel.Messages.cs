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
            var membersTask = FetchHistoryMembers(chat.Id);
            var messagesTask = FetchHistory(chat.Id, 50);
            await Task.WhenAll(membersTask, messagesTask);
            var members = await membersTask;
            var messages = await messagesTask;
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
        var members = await FetchHistoryMembers(chat.Id);
        var messages = await FetchHistory(chat.Id, 50, preferCacheOnError: false);
        if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
        await Ui(() => Fill(chat, members, messages, replace: true));
        await LoadPins(chat, gen);
        if (messages.Count > 0)
        {
            try { await _api.MarkRead(chat.Id, messages[^1].Id); } catch { }
        }
        RequestScroll();
    }

    /// <summary>После reconnect / close 4008: догнать пропущенное по sequence.</summary>
    async Task CatchUpAfterReconnect()
    {
        var chat = SelectedChat;
        if (chat == null) return;
        var gen = _openGen;
        var afterSequence = Math.Max(0, (FirstStoredMessage()?.Sequence ?? 1) - 1);
        try
        {
            while (true)
            {
                var version = HistoryVersion(chat.Id);
                var page = await FetchHistory(chat.Id, 50, afterSequence: afterSequence);
                if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
                await Ui(() =>
                {
                    if (!SameChat(gen, chat.Id)) return;
                    if (version == HistoryVersion(chat.Id))
                    {
                        var through = page.Count >= 50 ? page[^1].Sequence : long.MaxValue;
                        var returned = page.Select(m => m.Id).ToHashSet();
                        foreach (var removed in Messages.Where(m => !m.IsRelayed && m.Sequence > afterSequence &&
                                     m.Sequence <= through && !returned.Contains(m.Id)).ToArray())
                        {
                            Messages.Remove(removed);
                            UpdateLocalPin(chat.Id, removed.Id, null);
                        }
                    }
                    MergePage(SelectedChat!, page);
                });
                if (page.Count < 50) break;
                afterSequence = page[^1].Sequence;
            }
            if (LastStoredMessage() is { } last)
            {
                try { await _api.MarkRead(chat.Id, last.Id); } catch { }
            }
            RequestScroll();
            _ = LoadPins(chat, gen);
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
        OnPropertyChanged(nameof(CanInviteMembers));
        OnPropertyChanged(nameof(CanPin));
        if (!replace) return;
        Messages.Clear();
        foreach (var m in messages) Messages.Add(MessageMapper.ToItem(m, _me.Id, NameOf));
        RestoreRelays(chat.Id);
        HasMore = messages.Count >= 50;
        if (messages.Count == 0)
        {
            chat.SetPreview(null, null);
            chat.Time = "";
        }
        else
        {
            UpdateChatPreview(chat, messages[^1]);
        }
    }

    [RelayCommand]
    async Task LoadOlder()
    {
        var chat = SelectedChat;
        if (chat == null || !HasOlderTimeline || Busy) return;
        var gen = _openGen;
        var fetch = ++_historyFetch;
        var beforeSequence = HasMore ? FirstStoredMessage()?.Sequence : null;
        var beforeActivity = HasMoreActivity ? _activity.Keys.Min() : (long?)null;
        Busy = true;
        try
        {
            var page = beforeSequence is { } sequence ? await FetchHistory(chat.Id, 50, beforeSequence: sequence) : null;
            if (fetch != _historyFetch || !SameChat(gen, chat.Id)) return;
            await Ui(() =>
            {
                if (fetch != _historyFetch || !SameChat(gen, chat.Id)) return;
                if (page != null)
                {
                    InsertOlder(page);
                    HasMore = page.Count >= 50;
                }
            });
            if (beforeActivity is { } activityId)
            {
                var olderEvents = (await GetActivity(chat.Id, activityId)).Where(item => item.Id < activityId).ToArray();
                if (fetch != _historyFetch || !SameChat(gen, chat.Id)) return;
                HasMoreActivity = olderEvents.Length == ActivityPageSize;
                AddActivity(olderEvents);
            }
        }
        catch (Exception ex)
        {
            if (gen == _openGen) Error = Friendly(ex);
        }
        finally { if (fetch == _historyFetch) Busy = false; }
    }

    void InsertOlder(List<MessageDto> page)
    {
        foreach (var message in page)
            InsertInOrder(MessageMapper.ToItem(message, _me.Id, NameOf));
    }

    bool SameChat(int gen, string chatId) =>
        gen == _openGen && SelectedChat?.Id == chatId;

    [RelayCommand]
    async Task Send()
    {
        var chat = SelectedChat;
        var submitted = Draft;
        var text = submitted.Trim();
        if (chat == null || text.Length == 0 || Busy || _api.AntispamBlocked) return;
        if (text.Length > 4096) { Error = "Сообщение длиннее 4096 символов"; return; }
        Busy = true;
        try
        {
            Error = null;
            if (RelayArmed)
                await SendRelay(chat, text, submitted);
            else if (Editing is { } ed)
            {
                await _api.EditMessage(ed.Id, text);
                CompleteCompose(chat.Id, submitted, editing: true);
                if (SelectedChat?.Id == chat.Id) await ReloadMessages();
            }
            else
            {
                // client_ref: повтор после сетевого сбоя не создаст дубль
                var clientRef = Guid.NewGuid().ToString("N");
                var afterSequence = LastStoredMessage()?.Sequence ?? 0;
                await _api.SendMessage(chat.Id, text, ReplyTo?.Id, clientRef);
                CompleteCompose(chat.Id, submitted, editing: false);
                // Capture order before sending; the WS response may already be in the list.
                await MergeAfter(chat, afterSequence);
            }
        }
        catch (Exception ex) { Error = Friendly(ex); }
        finally { Busy = false; }
    }

    [RelayCommand]
    async Task ForwardTo(ChatItem? target)
    {
        var source = ForwardSource;
        if (Overlay != "forward" || target == null || source is not { IsDeleted: false, IsNotStored: false } ||
            Busy || _api.AntispamBlocked || !Chats.Any(chat => chat.Id == target.Id)) return;
        Busy = true;
        try
        {
            Error = null;
            var afterSequence = SelectedChat?.Id == target.Id ? LastStoredMessage()?.Sequence ?? 0 : 0;
            await _api.ForwardMessage(source.Id, target.Id);
            if (ReferenceEquals(ForwardSource, source)) Overlay = "";
            if (SelectedChat?.Id == target.Id) await MergeAfter(target, afterSequence);
            else await ReloadChats();
        }
        catch (Exception ex) { Error = Friendly(ex); }
        finally { Busy = false; }
    }

    async Task MergeAfter(ChatItem chat, long afterSequence)
    {
        var gen = _openGen;
        try
        {
            while (true)
            {
                var page = await FetchHistory(chat.Id, 50, afterSequence: afterSequence);
                if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
                await Ui(() => { if (SameChat(gen, chat.Id)) MergePage(chat, page); });
                if (page.Count < 50) break;
                afterSequence = page[^1].Sequence;
            }
            if (LastStoredMessage() is { } last)
            {
                try { await _api.MarkRead(chat.Id, last.Id); } catch { }
            }
            RequestScroll();
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    void MergePage(ChatItem chat, List<MessageDto> page)
    {
        foreach (var m in page)
        {
            var existing = Messages.FirstOrDefault(x => x.Id == m.Id);
            if (existing is null)
            {
                InsertInOrder(MessageMapper.ToItem(m, _me.Id, NameOf));
            }
            else
            {
                var fresh = MessageMapper.ToItem(m, _me.Id, NameOf);
                existing.Text = fresh.Text;
                existing.Meta = fresh.Meta;
                existing.Forwarded = fresh.Forwarded;
                existing.ForwardedTip = fresh.ForwardedTip;
                existing.IsDeleted = fresh.IsDeleted;
                existing.IsNotStored = fresh.IsNotStored;
                existing.Images = fresh.Images;
                existing.SetReactions(fresh.ReactionUsers);
            }
        }
        UpdateReplyPreviews();
        if (page.Count > 0) UpdateChatPreview(chat, page[^1]);
    }

    async Task LoadPins(ChatItem chat, int gen)
    {
        try
        {
            var version = HistoryVersion(chat.Id);
            List<PinDto> pins;
            var offline = false;
            try
            {
                pins = await _api.GetPins(chat.Id);
                if (version == HistoryVersion(chat.Id))
                    CacheHistory(store => store.SavePins(chat.Id, pins));
            }
            catch (Exception ex) when (CanUseLocalHistory(ex))
            {
                pins = await ReadLocalHistory(store => store.ReadPins(chat.Id)) ?? [];
                offline = true;
            }
            if (gen != _openGen || SelectedChat?.Id != chat.Id) return;
            await Ui(() => ReplacePins(pins));
            if (!offline) await UpgradeLocalPinSequences(chat, gen);
        }
        catch (Exception ex)
        {
            if (gen == _openGen) Error = Friendly(ex);
        }
    }

    void ReplacePins(List<PinDto> pins)
    {
        _pins.Clear();
        foreach (var pin in pins.OrderByDescending(p => p.Message?.Sequence))
        {
            if (pin.Message is null) continue;
            _pins.Add(new PinItem
            {
                MessageId = pin.MessageId,
                Sequence = pin.Message.Sequence,
                SenderId = pin.Message.SenderId,
                SentAt = DateTimeOffset.TryParse(pin.Message.SentAt, out var sentAt) ? sentAt : null,
                Text = MessageMapper.Body(pin.Message)
            });
        }
        _pinIndex = 0;
        NotifyPins();
    }

    void ClearPins()
    {
        _pins.Clear();
        _stripPins.Clear();
        _pinIndex = 0;
        NotifyPins();
    }

    void NotifyPins()
    {

        OnPropertyChanged(nameof(SelectedIsPinned));
        OnPropertyChanged(nameof(PinActionLabel));
        RefreshPinList();
    }

    void NotifyPinStrip()
    {
        OnPropertyChanged(nameof(HasPins));
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(PinScope));
    }

    void UpsertPin(MessageDto message)
    {
        _pins.RemoveAll(p => p.MessageId == message.Id);
        var item = new PinItem
        {
            MessageId = message.Id,
            Sequence = message.Sequence,
            SenderId = message.SenderId,
            SentAt = DateTimeOffset.TryParse(message.SentAt, out var sentAt) ? sentAt : null,
            Text = MessageMapper.Body(message)
        };
        var at = _pins.FindIndex(p => p.Sequence < message.Sequence);
        if (at < 0) _pins.Add(item);
        else _pins.Insert(at, item);
        _pinIndex = 0;
        NotifyPins();
    }

    void RemovePin(string messageId)
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
        if (_stripPins.Count == 0 || Environment.TickCount64 < _pinSyncQuietUntil) return;
        var next = _stripPins.FindIndex(pin => pin.Sequence is > 0 && pin.Sequence <= lastVisible.Sequence);
        if (next < 0) next = _stripPins.Count - 1;
        if (next == _pinIndex) return;
        _pinIndex = next;
        NotifyPinStrip();
    }

    /// <summary>
    /// Переход к закрепу. Если его нет в загруженном окне, история догружается
    /// страницами по 50 вверх и вставляется в список одним обновлением на UI-потоке.
    /// Смена чата или новый клик отменяют переход и не двигают ленту закрепов.
    /// </summary>
    [RelayCommand]
    async Task ShowPinned()
    {
        if (SelectedChat is { } chat && ShownPin is { } pin)
            await JumpToMessage(chat, pin.MessageId, cyclePin: true);
    }

    internal bool IsRevealingMessage => Environment.TickCount64 < _pinSyncQuietUntil;

    internal Task JumpToReply(string messageId) =>
        SelectedChat is { } chat ? JumpToMessage(chat, messageId, cyclePin: false) : Task.CompletedTask;

    async Task JumpToMessage(ChatItem chat, string messageId, bool cyclePin)
    {
        var gen = _openGen;
        var jump = ++_pinJump;
        Error = null;

        if (Messages.Any(m => m.Id == messageId))
        {
            RevealMessage(messageId, cyclePin);
            return;
        }

        var fetch = ++_historyFetch;
        Busy = true;
        var older = new List<MessageDto>();
        var hasMore = HasMore;
        var beforeSequence = FirstStoredMessage()?.Sequence;
        try
        {
            while (older.All(m => m.Id != messageId))
            {
                if (!SameJump(gen, jump, chat.Id)) return;
                if (beforeSequence is null || !hasMore)
                    break;
                var page = await FetchHistory(chat.Id, 50, beforeSequence: beforeSequence.Value);
                if (fetch != _historyFetch || !SameJump(gen, jump, chat.Id)) return;
                if (page.Count == 0 || page[0].Sequence >= beforeSequence)
                {
                    hasMore = false;
                    break;
                }
                older.InsertRange(0, page);
                beforeSequence = page[0].Sequence;
                hasMore = page.Count >= 50;
            }

            await Ui(() =>
            {
                if (fetch != _historyFetch || !SameJump(gen, jump, chat.Id)) return;
                if (older.Count > 0)
                {
                    InsertOlder(older);
                    UpdateTimeline();
                }
                HasMore = hasMore;
                RevealMessage(messageId, cyclePin);
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

    void RevealMessage(string messageId, bool cyclePin)
    {
        var item = Messages.FirstOrDefault(m => m.Id == messageId);
        if (item == null)
        {
            Error = "Сообщение не найдено в истории";
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
        if (!cyclePin) return;
        var at = _stripPins.FindIndex(p => p.MessageId == messageId);
        if (at >= 0 && _stripPins.Count > 1)
            _pinIndex = (at + 1) % _stripPins.Count;
        NotifyPinStrip();
    }

    bool SameJump(int gen, int jump, string chatId) =>
        jump == _pinJump && SameChat(gen, chatId);

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
                CacheSharedPin(chat.Id, message.Id, null);
                if (SelectedChat?.Id == chat.Id) RemovePin(message.Id);
            }
            else
            {
                await _api.PinMessage(chat.Id, message.Id);
                var pinned = new MessageDto
                {
                    Id = message.Id,
                    ChatId = chat.Id,
                    Sequence = message.Sequence,
                    SenderId = message.SenderId,
                    SentAt = message.SentAt?.ToString("O"),
                    Text = message.Text,
                    ContentState = message.IsDeleted ? "deleted" : "stored"
                };
                CacheSharedPin(chat.Id, message.Id, pinned);
                if (SelectedChat?.Id == chat.Id) UpsertPin(pinned);
            }
        }
        catch (Exception ex) { Error = Friendly(ex); }
    }

    [RelayCommand] void Reply() { if (SelectedMessage is { IsRelayed: false }) { Editing = null; ReplyTo = SelectedMessage; } }
    [RelayCommand] void BeginEdit() { if (CanEdit && SelectedMessage != null) { if (Editing == null) _beforeEdit = Draft; ReplyTo = null; Editing = SelectedMessage; Draft = SelectedMessage.Text; } }
    [RelayCommand] void ShowForward() { if (CanForward) Overlay = "forward"; }
    [RelayCommand] void CancelCompose() { if (Editing != null) Draft = _beforeEdit; ReplyTo = null; Editing = null; }
    [RelayCommand] void ToggleMoreReactions() => ShowMoreReactions = !ShowMoreReactions;

    partial void OnSelectedMessageChanged(MessageItem? value) => ShowMoreReactions = false;

    [RelayCommand]
    async Task DeleteForMe()
    {
        if (SelectedMessage == null) return;
        if (SelectedMessage.IsRelayed)
        {
            DismissRelay(SelectedMessage.Id);
            return;
        }
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
        if (msg == null || msg.IsDeleted || msg.IsRelayed || string.IsNullOrEmpty(emoji) || _reactionBusy) return;
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

    async Task Delete(string id, bool everyone)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Error = null;
            var deletingChat = SelectedChat;
            await _api.DeleteMessage(id, everyone);
            if (deletingChat != null)
            {
                CacheMessageRemoval(deletingChat.Id, id, everyone);
                UpdateLocalPin(deletingChat.Id, id, null);
            }
            SelectedMessage = null;
            await ReloadMessages();
        }
        catch (Exception ex) { Error = Friendly(ex); }
        finally { Busy = false; }
    }

    void Apply(WsEventDto e)
    {
        if (_closed) return;
        CacheEvent(e);
        switch (e.Type)
        {
            case "followed" when e.ChatId == SelectedChat?.Id:
                // Catch changes committed between the initial history read and the server subscription.
                _ = LoadGroupActivity(SelectedChat);
                break;
            case "message" when e.Message != null:
                Incoming(e.ChatId, e.Message, unread: e.Message.SenderId != _me.Id);
                break;
            case "message_relayed" when e.ChatId is { } relayChat && e.Message is { } relay:
                ReceiveRelay(relayChat, relay);
                break;
            case "attachment_added":
                AttachmentAdded(e);
                break;
            case "message_edited" when e.Message != null:
                UpdateLocalPin(e.Message.ChatId, e.Message.Id, MessageMapper.Body(e.Message));
                Incoming(e.ChatId, e.Message, unread: false);
                if (e.ChatId == SelectedChat?.Id)
                {
                    var pinned = _pins.FirstOrDefault(p => p.MessageId == e.Message.Id);
                    if (pinned != null)
                    {
                        pinned.Text = MessageMapper.Body(e.Message);
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
                if (e.ChatId == SelectedChat?.Id && e.MessageId == _imageMessageId && Overlay == "images")
                    CloseOverlay();
                if (e.ChatId is { } deletedChat && e.MessageId is { } deletedId)
                    UpdateLocalPin(deletedChat, deletedId, null);
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
                _ = ReloadChats();
                break;
            case "chat_hidden":
            case "chat_deleted":
                if (e.ChatId is { } removedChat) DropChat(removedChat, purgePins: e.Type == "chat_deleted");
                _ = ReloadChats();
                break;
            case "avatar_changed" when e.UserId is { } userId:
                InvalidateAvatar(userId);
                _ = LoadAvatar(userId, image => RefreshAvatar(userId, image));
                break;
            case "member_removed":
            case "member_left":
                if (e.UserId == _me.Id && e.ChatId is { } left)
                    DropChat(left, purgePins: true);
                else if (e.ChatId == SelectedChat?.Id)
                {
                    _ = LoadGroupActivity(SelectedChat);
                    if (HasWorkspacePage(WorkspacePage.Members)) _ = LoadMembersPanel();
                }
                break;
            case "member_added":
                if (e.ChatId == SelectedChat?.Id) _ = LoadGroupActivity(SelectedChat);
                goto case "member_role_changed";
            case "member_role_changed":
            case "owner_changed":
                if (e.ChatId == SelectedChat?.Id && HasWorkspacePage(WorkspacePage.Members))
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
                    && e.UserId != _me.Id && e.LastReadSequence is { } upto)
                {
                    foreach (var m in Messages)
                    {
                        if (!m.Mine || m.IsRelayed || m.Sequence > upto || m.Meta.Contains("прочитано")) continue;
                        m.Meta = m.Meta.Replace(" · доставлено", "") + " · прочитано";
                    }
                }
                break;
        }
    }

    void Incoming(string? chatId, MessageDto message, bool unread)
    {
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null)
        {
            _ = ReloadChats();
            return;
        }
        UpdateChatPreview(chat, message);
        if (unread && SelectedChat?.Id != chat.Id) chat.Unread = true;
        if (SelectedChat?.Id != chat.Id) return;
        var existing = Messages.FirstOrDefault(m => m.Id == message.Id);
        if (existing == null)
        {
            var item = MessageMapper.ToItem(message, _me.Id, NameOf);
            InsertInOrder(item);
            if (Messages.LastOrDefault() == item) RequestScroll();
            if (unread) _ = _api.MarkRead(chat.Id, message.Id);
        }
        else
        {
            var fresh = MessageMapper.ToItem(message, _me.Id, NameOf);
            existing.Text = fresh.Text;
            if (message.Attachments != null || fresh.IsDeleted || fresh.IsNotStored)
                existing.Images = fresh.Images;
            existing.Meta = fresh.Meta;
            existing.Forwarded = fresh.Forwarded;
            existing.ForwardedTip = fresh.ForwardedTip;
            existing.IsDeleted = fresh.IsDeleted;
            existing.IsNotStored = fresh.IsNotStored;
            existing.SetReactions(fresh.ReactionUsers);
            UpdateReplyPreviews();
        }
    }

    void TouchPreview()
    {
        if (SelectedChat == null) return;
        var last = LastStoredMessage();
        if (last == null)
        {
            SelectedChat.SetPreview(null, null);
            SelectedChat.Time = "";
            UpdateChatOrder(SelectedChat, null, 0, null);
            return;
        }
        SelectedChat.SetPreview(MessageMapper.PreviewAuthor(last.SenderId, _me.Id, NameOf), last.Text);
        SelectedChat.Time = last.Meta.Split(" · ", 2, StringSplitOptions.None)[0];
        UpdateChatOrder(SelectedChat, last.Id, last.Sequence, last.SentAt);
    }

    void UpdateChatPreview(ChatItem chat, MessageDto message)
    {
        if (message.Sequence < chat.LastMessageSequence) return;
        chat.SetPreview(MessageMapper.PreviewAuthor(message.SenderId, _me.Id, NameOf), MessageMapper.Body(message));
        chat.Time = MessageMapper.FormatTime(message.SentAt);
        UpdateChatOrder(chat, message.Id, message.Sequence,
            DateTimeOffset.TryParse(message.SentAt, out var sentAt) ? sentAt : null);
    }

    void UpdateChatOrder(ChatItem chat, string? lastMessageId, long sequence, DateTimeOffset? sentAt)
    {
        chat.LastMessageId = lastMessageId;
        chat.LastMessageSequence = sequence;
        chat.LastMessageSentAt = sentAt;
        var index = Chats.IndexOf(chat);
        if (index < 0) return;
        // Per-chat sequences cannot be compared between different chats.
        var ordered = Chats.OrderByDescending(c => c.LastMessageSentAt)
            .ThenByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList();
        var target = ordered.IndexOf(chat);
        if (target != index) Chats.Move(index, target);
    }
}
