using System.Net.WebSockets;
using MessengerBackend.Hubs;

var conn = new Hub.Conn { Ws = new RecordingSocket(), TokenHash = "test", UserId = 1 };
await conn.SendLock.WaitAsync();
Task send = Hub.SendControlAsync(conn, new { type = "pong" }, CancellationToken.None);
await Task.Delay(30);
if (send.IsCompleted)
    throw new Exception("A busy WebSocket writer must await the send lock, not requeue/drop the payload");
conn.SendLock.Release();
await send.WaitAsync(TimeSpan.FromSeconds(2));
if (((RecordingSocket)conn.Ws).Sent != 1) throw new Exception("Control frame lost");
Console.WriteLine("PASS busy WebSocket sender waits without requeue/spin/drop");

var socket = new RecordingSocket { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
var race = new Hub.Conn { Ws = socket, TokenHash = "test", UserId = 1 };
var writing = Hub.SendControlAsync(race, new { type = "pong" }, CancellationToken.None);
var closing = Hub.KillAsync(race, 4001, "logout");
await Task.Delay(30);
if (socket.Closes != 0) throw new Exception("Close raced with an active send");
socket.Gate.SetResult();
await Task.WhenAll(writing, closing).WaitAsync(TimeSpan.FromSeconds(2));
if (socket.Closes != 1 || socket.ConcurrentClose) throw new Exception("Close was not serialized");
Console.WriteLine("PASS WebSocket close uses the same send lock");

sealed class RecordingSocket : WebSocket
{
    public int Sent;
    public int Closes;
    public bool ConcurrentClose;
    public TaskCompletionSource? Gate;
    private int _active;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
    { ConcurrentClose |= Volatile.Read(ref _active) != 0; Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref Sent);
        try { if (Gate is not null) await Gate.Task.WaitAsync(token); }
        finally { Interlocked.Decrement(ref _active); }
    }
}
