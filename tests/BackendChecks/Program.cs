using System.Net.WebSockets;
using Messenger.Api.Realtime;

var timeout = TimeSpan.FromSeconds(2);
using var socket = new RecordingSocket();
using var connection = new WsConnection(1, socket, "test");
var writer = connection.RunWriterAsync(CancellationToken.None);
if (!connection.TryEnqueue([1])) throw new Exception("First frame was rejected");
await socket.FirstStarted.Task.WaitAsync(timeout);
if (!connection.TryEnqueue([2])) throw new Exception("Frame was dropped while writer was busy");
if (socket.Sent != 1) throw new Exception("Second frame was sent while first was in flight");
socket.ReleaseFirst.SetResult();
await socket.SecondStarted.Task.WaitAsync(timeout);
Console.WriteLine("PASS busy WebSocket writer preserves a queued frame");

connection.RequestClose(4001, "logout");
if (socket.Closes != 0) throw new Exception("Close raced with an active send");
if (connection.TryEnqueue([3])) throw new Exception("Writer accepted work after close was requested");
socket.ReleaseSecond.SetResult();
await writer.WaitAsync(timeout);
if (socket.Closes != 1 || socket.ConcurrentClose || socket.CloseStatus != (WebSocketCloseStatus)4001)
    throw new Exception("Requested close was not serialized after the active send");
Console.WriteLine("PASS WebSocket close waits for the active writer and preserves the close code");

sealed class RecordingSocket : WebSocket
{
    public int Sent;
    public int Closes;
    public bool ConcurrentClose;
    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private WebSocketCloseStatus? _closeStatus;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
    {
        ConcurrentClose |= Volatile.Read(ref _active) != 0;
        _closeStatus = status;
        Interlocked.Increment(ref Closes);
        return Task.CompletedTask;
    }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
    {
        Interlocked.Increment(ref _active);
        var first = Interlocked.Increment(ref Sent) == 1;
        (first ? FirstStarted : SecondStarted).SetResult();
        try { await (first ? ReleaseFirst : ReleaseSecond).Task.WaitAsync(token); }
        finally { Interlocked.Decrement(ref _active); }
    }
}
