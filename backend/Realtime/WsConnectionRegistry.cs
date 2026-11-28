using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Messenger.Api.Realtime;

public sealed class WsConnectionRegistry
{

    private int shuttingDown;
    private readonly ConcurrentDictionary<Guid, WsConnection> connections = new();

    public WsConnection Register(string userId, WebSocket socket, string tokenHash, bool supportsRelay = false)
    {
        var connection = new WsConnection(userId, socket, tokenHash, supportsRelay);

        if (!connections.TryAdd(connection.Id, connection))
            throw new InvalidOperationException("Duplicate connection ID.");

        if (Volatile.Read(ref shuttingDown) != 0)
        {
            connection.RequestClose(
                1001,
                "server shutting down");
        }
        return connection;
    }

    public void Remove(Guid connectionId) =>
        connections.TryRemove(connectionId, out _);

    public WsConnection[] GetByUser(string userId) =>
        connections.Values
            .Where(connection => connection.UserId == userId)
            .ToArray();

    public WsConnection[] GetAll() =>
        connections.Values.ToArray();

    public void CloseSession(string tokenHash, int code, string reason)
    {
        foreach (var connection in GetAll())
        {
            if (connection.TokenHash == tokenHash)
                connection.RequestClose(code, reason);
        }
    }

    public void BeginShutdown()
    {
        Interlocked.Exchange(ref shuttingDown, 1);

        foreach (var connection in GetAll())
        {
            connection.RequestClose(
                1001,
                "server shutting down");
        }
    }
}
