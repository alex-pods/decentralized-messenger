using System.Globalization;
using System.Text.Json;
using Messenger.Core.Interfaces;
using Messenger.Core.Models;
using Messenger.Core.Remote;

namespace Messenger.Core;

// One account, one SQLite store. UI reads the store; HTTP/WS update the same rows.
public sealed class MessengerSession
{
    private readonly IMessageStore _store;
    private readonly IMessengerRemote _remote;
    private readonly SemaphoreSlim _sync = new(1, 1);
    public AccountIdentity Account { get; }
    public event Action? Changed;
    public event Action<string>? Status;
    public event Action? SessionRevoked;

    public MessengerSession(AccountIdentity account, IMessageStore store, IMessengerRemote remote)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(remote);
        Account = account;
        _store = store;
        _remote = remote;
    }

    public Task UpdateProfileAsync(User profile, CancellationToken ct = default)
    {
        if (profile.Id != Account.UserId) throw new InvalidOperationException("Профиль не принадлежит текущему аккаунту.");
        return _store.UpsertUserAsync(profile, ct);
    }

    public static User MapProfile(ProfileDto p) => new()
    {
        Id = p.Id, Tag = p.Tag, DisplayName = p.DisplayName, Bio = p.Bio,
        AvatarRemotePath = p.AvatarRemotePath, JoinDate = Date(p.JoinDate), LastSeenAt = OptionalDate(p.LastSeenAt)
    };

    public async Task<List<ChatDto>> GetChatsAsync(CancellationToken ct = default)
    {
        var result = new List<ChatDto>();
        foreach (var c in await _store.GetChatListAsync(ct: ct))
        {
            var last = (await _store.GetMessagesPageAsync(c.Id, limit: 1, ct: ct)).FirstOrDefault();
            result.Add(new ChatDto
            {
                Id = c.Id, Type = c.Type, Title = c.Title, CreatedAt = c.CreatedAt.ToString("O"), Unread = c.UnreadCount > 0,
                OtherUser = c.Peer is { } p ? new UserCardDto { Id = p.Id, Tag = p.Tag, DisplayName = p.DisplayName } : null,
                LastMessage = last is null ? null : MapMessage(last)
            });
        }
        return result;
    }

    public async Task<List<MemberDto>> GetMembersAsync(long chatId, CancellationToken ct = default) =>
        (await _store.GetMembersAsync(chatId, ct)).Select(m => new MemberDto
        { Id = m.UserId, Tag = m.User.Tag, DisplayName = m.User.DisplayName, Role = m.Role, JoinedAt = m.JoinedAt.ToString("O") }).ToList();

    public async Task<List<MessageDto>> GetMessagesAsync(long chatId, int limit = 50, CancellationToken ct = default) =>
        (await _store.GetMessagesPageAsync(chatId, limit: limit, ct: ct)).Select(MapMessage).Reverse().ToList();

    public async Task QueueMessageAsync(long chatId, string text, long? replyLocalId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new ArgumentException("Сообщение: 1–4096 символов.");
        await _store.EnqueueOutgoingAsync(chatId, Account.UserId, text, replyLocalId, ct: ct);
        Changed?.Invoke();
    }

    public async Task MarkReadAsync(long chatId, CancellationToken ct = default)
    {
        if (await _store.MarkChatReadAsync(chatId, ct) > 0) Changed?.Invoke();
    }

    public async Task RetryAsync(long chatId, CancellationToken ct = default)
    {
        await _store.RetryFailedAsync(chatId, ct);
        Changed?.Invoke();
    }

    public async Task HideChatAsync(long chatId, CancellationToken ct = default)
    {
        await _store.SetChatVisibleAsync(chatId, false, ct);
        Changed?.Invoke();
    }

    public async Task SyncAsync(long? chatId = null, CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (chatId is { } id) await SyncChat(await _remote.GetChat(id, ct), ct);
            else
            {
                var chats = await _remote.GetChats(ct);
                var visible = new List<long>();
                foreach (var chat in chats)
                {
                    try { await SyncChat(chat, ct); visible.Add(chat.Id); }
                    catch (ApiException ex) when (ex.Status is 403 or 404)
                    { await _store.SetChatVisibleAsync(chat.Id, false, ct); }
                }
                // Never replace the chat list after a partial/network failure.
                await _store.SetVisibleChatsAsync(visible, ct);
            }
            Changed?.Invoke();
        }
        finally { _sync.Release(); }
    }

    private async Task CacheChat(ChatDto chat, CancellationToken ct)
    {
        if (chat.OtherUser is { } peer) await CacheUser(peer.Id, peer.DisplayName, peer.Tag, ct);
        await _store.UpsertChatAsync(new ChatInfo(chat.Id, chat.Type, chat.Title, chat.OtherUser?.Id, Date(chat.CreatedAt)), ct);
    }

    private async Task SyncChat(ChatDto chat, CancellationToken ct)
    {
        await CacheChat(chat, ct);
        var members = await _remote.GetMembers(chat.Id, ct);
        foreach (var member in members) await CacheUser(member.Id, member.DisplayName, member.Tag, ct);
        await _store.ReplaceMembersAsync(chat.Id, members.Select(m => new ChatMemberInfo(m.Id, m.Role, Date(m.JoinedAt))).ToArray(), ct);
        var myRead = members.FirstOrDefault(m => m.Id == Account.UserId)?.LastReadMessageId ?? 0;
        var ids = new List<long>();
        long? before = null;
        long upper = long.MaxValue;
        do
        {
            var page = await _remote.GetMessages(chat.Id, 100, before, ct);
            if (before is null && page.Count > 0) upper = page.Max(m => m.Id);
            foreach (var m in page)
            {
                await SaveMessage(m, m.Id > myRead, ct);
                ids.Add(m.Id);
            }
            if (page.Count < 100) break;
            var next = page.Min(m => m.Id);
            if (before is { } previous && next >= previous) throw new InvalidOperationException("Некорректная страница истории.");
            before = next;
        } while (true);
        await _store.ReconcileHistoryAsync(chat.Id, ids, upper, ct);
        await _store.SetChatVisibleAsync(chat.Id, true, ct);
    }

    private async Task CacheUser(long id, string? name, string? tag, CancellationToken ct)
    {
        var user = await _store.GetUserAsync(id, ct);
        if (user is null) user = MapProfile(await _remote.GetUser(id, ct));
        if (name is not null) user.DisplayName = name;
        if (tag is not null) user.Tag = tag;
        await _store.UpsertUserAsync(user, ct);
    }

    private async Task SaveMessage(MessageDto m, bool countUnread, CancellationToken ct)
    {
        if (await _store.GetUserAsync(m.SenderId, ct) is null) await CacheUser(m.SenderId, null, null, ct);
        var outgoing = m.SenderId == Account.UserId;
        await _store.SaveIncomingAsync(new IncomingMessage(m.Id, m.ChatId, m.SenderId, m.ReplyToId,
            m.Text, Date(m.SentAt), OptionalDate(m.EditedAt), ClientId: m.ClientId, IsOutgoing: outgoing,
            ContentState: m.ContentState, DeliveredAt: OptionalDate(m.DeliveredAt),
            ReadAt: outgoing ? OptionalDate(m.ReadAt) : (!countUnread ? DateTime.UtcNow : null),
            DeletedAt: OptionalDate(m.DeletedAt), CountUnread: countUnread), ct);
    }

    public async Task ApplyAsync(WsEventDto e, CancellationToken ct = default)
    {
        if (e.Type is not ("message" or "message_edited" or "message_deleted" or "read" or
            "chat_created" or "chat_hidden" or "chat_deleted" or "member_added" or "member_removed" or
            "member_left" or "member_role_changed" or "owner_changed")) return;
        // Membership/list events require a fresh authorized snapshot.
        if (e.Type is "chat_created" or "chat_hidden" or "chat_deleted" or "member_added" or "member_removed" or "member_left" or "member_role_changed" or "owner_changed")
        {
            if (e.ChatId is { } gone && (e.Type is "chat_hidden" or "chat_deleted" || e.Type is "member_removed" or "member_left" && e.UserId == Account.UserId))
                await HideChatAsync(gone, ct);
            await SyncAsync(ct: ct);
            return;
        }
        await _sync.WaitAsync(ct);
        try
        {
            if (e.Type == "message_deleted" && e.ForEveryone == false && e.MessageId is { } hidden)
                await _store.HideServerMessageAsync(hidden, ct);
            else if (e.Message is { } m)
            {
                if (!(await _store.GetChatListAsync(ct: ct)).Any(c => c.Id == m.ChatId))
                    await CacheChat(await _remote.GetChat(m.ChatId, ct), ct);
                await SaveMessage(m, true, ct);
                await _store.SetChatVisibleAsync(m.ChatId, true, ct);
            }
            else if (e.Type == "read" && e.ChatId is { } chat && e.LastReadMessageId is { } upto && e.UserId != Account.UserId)
            {
                // Direct receipts can be applied immediately. Group aggregation comes from HTTP.
                if ((await _store.GetChatListAsync(ct: ct)).Any(c => c.Id == chat && c.Type == "direct"))
                    await _store.ApplyReadAsync(chat, upto, DateTime.UtcNow, ct);
            }
            Changed?.Invoke();
        }
        finally { _sync.Release(); }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _sync.WaitAsync(ct);
        try
        {
            foreach (var op in await _store.GetDueOutboxAsync(ct: ct))
            {
                try
                {
                    if (op.Type == OutboxOperationType.SendMessage && op.Message is { } m)
                    {
                        var sent = await _remote.SendQueuedMessage(m.ChatId, m.Text, m.ClientId, m.ReplyToServerId, ct);
                        await _store.MarkSentAsync(m.ClientId, sent.MessageId, OptionalDate(sent.SentAt) ?? m.SentAt, ct);
                    }
                    else if (op.Type == OutboxOperationType.MarkRead && op.ChatId is { } chat)
                    {
                        using var payload = JsonDocument.Parse(op.PayloadJson!);
                        await _remote.MarkRead(chat, payload.RootElement.GetProperty("upToServerId").GetInt64(), ct);
                        await _store.CompleteOutboxAsync(op.Id, ct);
                    }
                    else throw new InvalidOperationException("Эта операция очереди ещё не поддерживается в MVP.");
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (ApiException ex) when (ex.Status == 401) { throw; }
                catch (ApiException ex) when (ex.Status is >= 400 and < 500 && ex.Status is not (408 or 429))
                {
                    if (op.MessageId is { } mid) await _store.MarkSendFailedAsync(mid, ct);
                    else await _store.CompleteOutboxAsync(op.Id, ct); // obsolete read after leaving/hiding
                    Status?.Invoke("Ошибка отправки: " + ex.Message);
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    await _store.RecordOutboxFailureAsync(op.Id, ex.Message, ct);
                    throw;
                }
            }
        }
        finally { _sync.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var nextSync = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(ct);
                if (DateTime.UtcNow >= nextSync)
                {
                    await SyncAsync(ct: ct);
                    nextSync = DateTime.UtcNow.AddSeconds(30);
                    Status?.Invoke("онлайн");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiException ex) when (ex.Status == 401) { SessionRevoked?.Invoke(); return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ApiException)
            { Status?.Invoke("нет связи · сообщения в очереди"); }
            catch (Exception ex) { Status?.Invoke("Ошибка синхронизации: " + ex.Message); }
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static MessageDto MapMessage(Message m) => new()
    {
        Id = m.ServerId ?? -m.Id, LocalId = m.Id, ClientId = m.ClientId, ChatId = m.ChatId,
        SenderId = m.SenderId, LocalSenderName = m.Sender?.DisplayName, ReplyToId = m.ReplyToServerId,
        Text = m.Text, ContentState = m.ContentState, SyncState = m.SyncState,
        SentAt = m.SentAt.ToString("O"), EditedAt = m.EditedAt?.ToString("O"),
        DeliveredAt = m.DeliveredAt?.ToString("O"), ReadAt = m.ReadAt?.ToString("O"), DeletedAt = m.DeletedAt?.ToString("O")
    };
    private static DateTime Date(string? value) => DateTimeOffset.Parse(value ?? throw new InvalidOperationException("Нет даты сервера."), CultureInfo.InvariantCulture).UtcDateTime;
    private static DateTime? OptionalDate(string? value) => value is null ? null : Date(value);
}
