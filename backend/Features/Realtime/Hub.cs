using Dapper;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace MessengerBackend.Hubs;

/// <summary>
/// follow(Chat)/pub/sub. Каждое соединение привязано к сессии (token_hash) и имеет
/// ОГРАНИЧЕННУЮ очередь доставки (256 событий): медленный клиент, не читающий сокет,
/// отключается (close 4008) и восстанавливается повторным подключением + догрузкой истории,
/// вместо расходования памяти сервера. Close codes: 1001 shutdown, 4001 logout,
/// 4002 сессия истекла, 4008 медленный клиент.
/// Один процесс: несколько экземпляров сервера не поддерживается (события не реплицируются).
/// </summary>
public sealed class Hub
{
    public const int CloseShutdown = 1001;
    public const int CloseSessionRevoked = 4001;
    public const int CloseSessionExpired = 4002;
    public const int CloseSlowConsumer = 4008;
    private const int QueueCapacity = 256;

    public sealed class Conn
    {
        public required WebSocket Ws;
        public required string TokenHash;
        public required long UserId;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly CancellationTokenSource Lifetime = new();
        public readonly Channel<byte[]> Queue = Channel.CreateBounded<byte[]>(QueueCapacity);
        public readonly ConcurrentDictionary<long, byte> Followed = new();
    }

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, Conn>> _byUser = new();

    public sealed class Registration : IAsyncDisposable
    {
        private readonly Hub _hub;
        private readonly ConcurrentDictionary<Guid, Conn> _set;
        private readonly Conn _conn;
        private int _disposed;
        public Conn Connection => _conn;
        public Registration(Hub hub, ConcurrentDictionary<Guid, Conn> set, Conn conn)
        { _hub = hub; _set = set; _conn = conn; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _conn.Lifetime.Cancel();
            _conn.Queue.Writer.TryComplete();
            lock (_hub._byUser)
            {
                foreach (var (id, c) in _set)
                    if (ReferenceEquals(c, _conn)) _set.TryRemove(id, out _);
                if (_set.IsEmpty) _hub._byUser.TryRemove(_conn.UserId, out _);
            }
            try
            {
                if (_conn.Ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _conn.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
                }
            }
            catch { /* уже закрыт */ }
        }
    }

    public Registration Register(long userId, string tokenHash, WebSocket ws)
    {
        var conn = new Conn { Ws = ws, TokenHash = tokenHash, UserId = userId };
        lock (_byUser)
        {
            var set = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Conn>());
            set[Guid.NewGuid()] = conn;
            return new Registration(this, set, conn);
        }
    }

    /// <summary>Фоновый писатель очереди соединения. Возвращаетfalse, если соединение закрыто.</summary>
    public static async Task WriteLoopAsync(Conn conn, CancellationToken ct)
    {
        await foreach (var payload in conn.Queue.Reader.ReadAllAsync(ct))
        {
            await SendLocked(conn, payload, ct);
            if (conn.Ws.State != WebSocketState.Open) break;
        }
    }

    /// <summary>Событие чата: подписанным (follow) сокетам участников; AlwaysDeliver — без подписки.
    /// Возвращает число сокетов ДРУГИХ участников, которым событие попало в очередь (для delivered_at).</summary>
    public Task<int> PublishToChat(long chatId, IEnumerable<long> userIds, long excludeUserId, WsEvent evt) =>
        Send(chatId, userIds, excludeUserId, evt, requireFollow: !evt.AlwaysDeliver);

    /// <summary>Личные события без подписки (chat_created, синхронизация «у себя», owner_changed).</summary>
    public Task PublishToUsers(IEnumerable<long> userIds, WsEvent evt) =>
        Send(null, userIds, long.MinValue, evt, requireFollow: !evt.AlwaysDeliver);

    private async Task<int> Send(long? chatId, IEnumerable<long> userIds, long excludeUserId,
        WsEvent evt, bool requireFollow)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(evt);
        var delivered = 0;
        foreach (var uid in userIds.Distinct())
        {
            if (!_byUser.TryGetValue(uid, out var set)) continue;
            foreach (var conn in set.Values)
            {
                var allowed = Db.Read(c => c.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM user_sessions WHERE token_hash=@h AND expires_at>@now",
                    new { h = conn.TokenHash, now = Util.NowIso() }) > 0);
                if (!allowed) continue;
                if (requireFollow && (chatId is null || !conn.Followed.ContainsKey(chatId.Value))) continue;
                if (conn.Queue.Writer.TryWrite(payload))
                { if (uid != excludeUserId) delivered++; }
                else
                    _ = KillAsync(conn, CloseSlowConsumer, "slow consumer: delivery queue overflow"); // не ждём
            }
        }
        return delivered;
    }

    /// <summary>Контрольное сообщение (pong/followed/error) — напрямую, в обход очереди.</summary>
    public static async Task SendControlAsync(Conn conn, object reply, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(reply);
        await SendLocked(conn, payload, ct);
    }

    static async Task SendLocked(Conn conn, byte[] payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, conn.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var entered = false;
        try
        {
            await conn.SendLock.WaitAsync(timeout.Token);
            entered = true;
            if (conn.Ws.State == WebSocketState.Open)
                await conn.Ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
        }
        catch { conn.Ws.Abort(); }
        finally { if (entered) conn.SendLock.Release(); }
    }

    public Task CloseSession(string tokenHash, int closeCode, string reason) =>
        CloseWhere(c => c.TokenHash == tokenHash, closeCode, reason);

    public Task CloseAll(int closeCode, string reason) =>
        CloseWhere(_ => true, closeCode, reason);

    public Task CloseWhere(Func<Conn, bool> predicate, int closeCode, string reason)
    {
        var tasks = new List<Task>();
        foreach (var set in _byUser.Values)
            foreach (var conn in set.Values)
                if (predicate(conn))
                    tasks.Add(KillAsync(conn, closeCode, reason));
        return Task.WhenAll(tasks);
    }

    public static async Task KillAsync(Conn conn, int closeCode, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var entered = false;
        try
        {
            await conn.SendLock.WaitAsync(timeout.Token);
            entered = true;
            if (conn.Ws.State == WebSocketState.Open)
                await conn.Ws.CloseOutputAsync((WebSocketCloseStatus)closeCode, reason, timeout.Token);
        }
        catch { try { conn.Ws.Abort(); } catch { /* уже мёртв */ } }
        finally
        {
            if (entered) conn.SendLock.Release();
            conn.Lifetime.Cancel();
        }
    }
}

// ---------- События, уходящие в WebSocket (общие модели с HTTP-ответами) ----------

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
