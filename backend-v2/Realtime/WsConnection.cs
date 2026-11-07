using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Messenger.Api.Realtime;

public sealed class WsConnection(
    long userId,
    WebSocket socket,
    string tokenHash) : IDisposable
{
    private readonly ConcurrentDictionary<long, byte> followedChats = new();

    private readonly Channel<byte[]> outgoing =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly CancellationTokenSource closed = new();
    private CloseRequest? closeRequest;

    public Guid Id { get; } = Guid.NewGuid();
    public long UserId { get; } = userId;
    public WebSocket Socket { get; } = socket;
    public string TokenHash { get; } = tokenHash;
    public CancellationToken Closed => closed.Token;

    public void Follow(long chatId) =>
        followedChats.TryAdd(chatId, 0);

    public bool IsFollowing(long chatId) =>
        followedChats.ContainsKey(chatId);

    public bool TryEnqueue(byte[] payload)
    {
        if (Volatile.Read(ref closeRequest) is not null)
            return false;

        if (outgoing.Writer.TryWrite(payload))
            return true;

        RequestClose(4008, "slow consumer: delivery queue overflow");
        return false;
    }

    public void RequestClose(int code, string reason)
    {
        Interlocked.CompareExchange(
            ref closeRequest,
            new CloseRequest(code, reason),
            null);

        outgoing.Writer.TryComplete();
    }

    public async Task RunWriterAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in outgoing.Reader.ReadAllAsync(ct))
            {
                if (Volatile.Read(ref closeRequest) is not null ||
                    Socket.State != WebSocketState.Open)
                    break;

                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);

                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                await Socket.SendAsync(
                    payload.AsMemory(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: timeout.Token);
            }
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or WebSocketException)
        {
            Socket.Abort();
        }
        finally
        {
            RequestClose(1000, "bye");
            var request = Volatile.Read(ref closeRequest)!;

            try
            {
                if (Socket.State is WebSocketState.Open
                    or WebSocketState.CloseReceived)
                {
                    using var timeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(2));

                    await Socket.CloseOutputAsync(
                        (WebSocketCloseStatus)request.Code,
                        request.Reason,
                        timeout.Token);
                }
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or WebSocketException)
            {
                Socket.Abort();
            }
            finally
            {
                closed.Cancel();
            }
        }
    }

    public void Dispose() => closed.Dispose();

    private sealed record CloseRequest(int Code, string Reason);
}