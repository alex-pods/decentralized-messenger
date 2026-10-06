using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessengerBackend;

/// <summary>
/// follow(Chat)/pub/sub. Каждое соединение привязано к конкретной сессии (token_hash):
/// logout/истечение сессии закрывает её соединения (close codes: 4001 revoked, 4002 expired).
/// События чата доставляются только подписанным (follow); AlwaysDeliver — без подписки.
/// </summary>
public sealed class Hub
{
    public const int CloseSessionRevoked = 4001;
    public const int CloseSessionExpired = 4002;

    public sealed class Conn
    {
        public required WebSocket Ws;
        public required string TokenHash;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly ConcurrentDictionary<long, byte> Followed = new();
    }

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, Conn>> _byUser = new();

    public sealed class Registration : IDisposable
    {
        private readonly Hub _hub;
        private readonly long _userId;
        private readonly ConcurrentDictionary<Guid, Conn> _set;
        private readonly Conn _conn;
        public Conn Connection => _conn;
        public Registration(Hub hub, long userId, ConcurrentDictionary<Guid, Conn> set, Conn conn)
        { _hub = hub; _userId = userId; _set = set; _conn = conn; }
        public void Dispose()
        {
            foreach (var (id, c) in _set)
                if (ReferenceEquals(c, _conn)) _set.TryRemove(id, out _);
            if (_set.IsEmpty) _hub._byUser.TryRemove(_userId, out _);
        }
    }

    public Registration Register(long userId, string tokenHash, WebSocket ws)
    {
        var conn = new Conn { Ws = ws, TokenHash = tokenHash };
        var set = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Conn>());
        set[Guid.NewGuid()] = conn;
        return new Registration(this, userId, set, conn);
    }

    /// <summary>Закрыть все соединения данной сессии (logout). Код 4001.</summary>
    public Task CloseSession(string tokenHash, int closeCode, string reason) =>
        CloseWhere(c => c.TokenHash == tokenHash, closeCode, reason);

    /// <summary>Закрыть соединения с истёкшими/отозванными сессиями. Код 4002.</summary>
    public Task CloseWhere(Func<Conn, bool> predicate, int closeCode, string reason)
    {
        var tasks = new List<Task>();
        foreach (var set in _byUser.Values)
            foreach (var conn in set.Values)
                if (predicate(conn))
                    tasks.Add(CloseConn(conn, closeCode, reason));
        return Task.WhenAll(tasks);
    }

    private static async Task CloseConn(Conn conn, int closeCode, string reason)
    {
        try
        {
            if (conn.Ws.State == WebSocketState.Open)
            {
                // не ждём полного рукопожатия: клиент может не ответить на close-фрейм
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await conn.Ws.CloseAsync((WebSocketCloseStatus)closeCode, reason, cts.Token);
            }
        }
        catch
        {
            try { conn.Ws.Abort(); } catch { /* уже мёртв */ }
        }
    }

    /// <summary>Событие чата: доставляется подписанным (follow) сокетам участников,
    /// кроме событий с AlwaysDeliver. Возвращает число сокетов ДРУГИХ участников,
    /// получивших событие (для delivered_at).</summary>
    public Task<int> PublishToChat(long chatId, IEnumerable<long> userIds, long excludeUserId, WsEvent evt) =>
        Send(chatId, userIds, excludeUserId, evt, requireFollow: !evt.AlwaysDeliver);

    /// <summary>Личные события без подписки (chat_created, синхронизация «у себя», owner_changed).</summary>
    public Task PublishToUsers(IEnumerable<long> userIds, WsEvent evt) =>
        Send(null, userIds, long.MinValue, evt, requireFollow: !evt.AlwaysDeliver);

    private async Task<int> Send(long? chatId, IEnumerable<long> userIds,
        long excludeUserId, WsEvent evt, bool requireFollow)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var tasks = new List<Task<bool>>();
        foreach (var uid in userIds.Distinct())
        {
            if (!_byUser.TryGetValue(uid, out var set)) continue;
            foreach (var conn in set.Values)
            {
                if (requireFollow && (chatId is null || !conn.Followed.ContainsKey(chatId.Value))) continue;
                tasks.Add(SendSafe(conn, payload, count: uid != excludeUserId));
            }
        }
        var results = await Task.WhenAll(tasks);
        return results.Count(r => r);
    }

    private static async Task<bool> SendSafe(Conn conn, byte[] payload, bool count)
    {
        if (!conn.SendLock.Wait(0))
            return false; // прошлая отправка ещё идёт — пропускаем событие, клиент догонит через историю
        try
        {
            if (conn.Ws.State == WebSocketState.Open)
            {
                await conn.Ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
                return count;
            }
        }
        catch { /* сокет умер — уберётся при выходе из receive-цикла */ }
        finally { conn.SendLock.Release(); }
        return false;
    }
}

// ---------- События, уходящие в WebSocket ----------

public record WsEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("chat_id")] long? ChatId = null)
{
    /// <summary>Доставлять даже без follow (chat_created, личные синхронизации, owner_changed).</summary>
    [JsonIgnore] public bool AlwaysDeliver { get; init; }
    [JsonPropertyName("message")] public MessageOut? Message { get; init; }
    [JsonPropertyName("message_id")] public long? MessageId { get; init; }
    [JsonPropertyName("for_everyone")] public bool? ForEveryone { get; init; }
    [JsonPropertyName("chat")] public object? Chat { get; init; }
    [JsonPropertyName("user")] public UserCard? User { get; init; }
    [JsonPropertyName("user_id")] public long? UserId { get; init; }
    [JsonPropertyName("emoji")] public string? Emoji { get; init; }
    [JsonPropertyName("attachment")] public AttachmentOut? Attachment { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("last_read_message_id")] public long? LastReadMessageId { get; init; }
}
