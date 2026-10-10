using Messenger.Core.Remote;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LocalBackend.Remote;

public sealed class SessionRevokedException : Exception;

/// <summary>WebSocket /ws: ping и follow(chat_id), события сервера.</summary>
public sealed class LiveConnection : IDisposable
{
    private readonly MessengerClient _api;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly HashSet<long> _followed = new();
    private ClientWebSocket? _ws;

    public event Action<WsEventDto>? Event;
    public event Action<string>? State;
    public event Action? SessionRevoked;

    public LiveConnection(MessengerClient api) => _api = api;

    public void Follow(long chatId)
    {
        lock (_followed) { if (!_followed.Add(chatId)) return; }
        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
            _ = FollowNow(ws, chatId);
    }

    async Task FollowNow(ClientWebSocket ws, long chatId)
    {
        try { await SendRaw(ws, new { action = "follow", chat_id = chatId }, _cts.Token); }
        catch { /* reconnect resubscribes to all followed chats */ }
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Session(ct);
            }
            catch (SessionRevokedException)
            {
                SessionRevoked?.Invoke();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                /* обрыв — подождём и поднимем сокет снова */
            }
            if (ct.IsCancellationRequested) return;
            State?.Invoke("переподключение…");
            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task Session(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(_api.WebSocketUri(), ct);
        _ws = ws;
        State?.Invoke("онлайн");
        try
        {
            long[] ids;
            lock (_followed) ids = _followed.ToArray();
            foreach (var id in ids)
                await SendRaw(ws, new { action = "follow", chat_id = id }, ct);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var recv = ReceiveLoop(ws, stop.Token);
            var ping = PingLoop(ws, stop.Token);
            var finished = await Task.WhenAny(recv, ping);
            stop.Cancel();
            try { await Task.WhenAll(recv, ping); }
            catch (OperationCanceledException) { }
            if (finished.IsFaulted)
            {
                var ex = finished.Exception!.GetBaseException();
                if (ex is SessionRevokedException) throw ex;
                if (ex is OperationCanceledException) return;
                throw ex;
            }
        }
        finally
        {
            if (ReferenceEquals(_ws, ws)) _ws = null;
            State?.Invoke("нет соединения");
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* уже закрыт */ }
        }
    }

    async Task PingLoop(ClientWebSocket ws, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
        while (await timer.WaitForNextTickAsync(ct))
            await SendRaw(ws, new { action = "ping" }, ct);
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    var code = (int)(ws.CloseStatus ?? 0);
                    if (code is 4001 or 4002) throw new SessionRevokedException();
                    return;
                }
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            if (res.MessageType != WebSocketMessageType.Text || ms.Length == 0) continue;
            WsEventDto? evt;
            try
            {
                evt = JsonSerializer.Deserialize<WsEventDto>(Encoding.UTF8.GetString(ms.ToArray()), MessengerClient.Json);
            }
            catch { continue; }
            if (!string.IsNullOrEmpty(evt?.Type))
                Event?.Invoke(evt);
        }
    }

    async Task SendRaw(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _send.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _send.Release(); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* уже отменён */ }
        try { _ws?.Abort(); } catch { /* закрываем */ }
    }
}
