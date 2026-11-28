using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using MessengerDesktop;
using Microsoft.Data.Sqlite;

internal static class RelayChecks
{
    internal sealed record Fixture(string Chat, string Id, string Text, RelayReceipt Receipt);

    internal static async Task<Fixture> Run(MessengerClient sender, string senderId, string database)
    {
        using var recipient = await Account(sender.ServerBase, "relaybob");
        using var outsider = await Account(sender.ServerBase, "relayoutsider");
        await sender.DiscoverAntispam();
        Expect(sender.RelaySupported, "Server did not advertise relay");
        var chat = await sender.CreateDirect((await recipient.GetMe()).Id);
        var id = NewMessage(chat);
        var missingChat = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        const string marker = "relay-content-91ae690e029b45778a13";
        await Error(() => outsider.RelayMessage(chat, id, marker), 404, "not_found");
        await Error(() => outsider.RelayMessage(missingChat, NewMessage(missingChat), marker), 404, "not_found");
        await Error(() => sender.RelayMessage(chat, "invalid", marker), 400, "validation_error");
        await Error(() => sender.RelayMessage(chat, id, " "), 400, "validation_error");
        await Error(() => sender.RelayMessage(chat, id, new string('a', 4097)), 400, "validation_error");

        using var http = new HttpClient();
        var endpoint = sender.ServerBase + "/chats/" + chat + "/messages/relay";
        using (var unauthorized = await http.PostAsJsonAsync(endpoint, new { message_id = id, text = marker }))
            Expect(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Relay endpoint accepted an unauthenticated request");
        http.DefaultRequestHeaders.Authorization = new("Bearer", sender.Token);
        using (var oversized = await http.PostAsJsonAsync(endpoint, new { message_id = id, text = new string('a', 33_000) }))
            Expect(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Relay payload limit was not enforced");
        using (var wrongShape = await http.PostAsJsonAsync(endpoint, new { message_id = id, text = marker, reply_to_id = id }))
            Expect(wrongShape.StatusCode == HttpStatusCode.BadRequest, "Relay accepted a stored-message-only field");

        using var oldSocket = await Connect(recipient, relay: false);
        await Follow(oldSocket, chat, "followed");
        using var outsideSocket = await Connect(outsider, relay: true);
        await Follow(outsideSocket, chat, "error");
        await Error(() => sender.RelayMessage(chat, id, marker), 409, "relay_no_recipient");

        using var openButNotFollowing = await Connect(recipient, relay: true);
        await Error(() => sender.RelayMessage(chat, id, marker), 409, "relay_no_recipient");

        var previous = await sender.SendMessage(chat, "Stored before relay");
        var before = (await sender.GetMessages(chat)).Single(m => m.Id == previous);
        using var socket = await Connect(recipient, relay: true);
        await Follow(socket, chat, "followed");

        using var published = await http.PostAsJsonAsync(endpoint, new { message_id = id, text = marker });
        Expect(published.StatusCode == HttpStatusCode.Accepted && published.Headers.CacheControl?.NoStore == true,
            "Relay response or cache policy was unexpected");
        var receipt = await published.Content.ReadFromJsonAsync<RelayReceipt>(MessengerClient.Json)
            ?? throw new Exception("Relay receipt is missing");
        var evt = await Receive(socket);
        var message = evt.Message;
        Expect(evt.Type == "message_relayed" && evt.ChatId == chat && message?.ContentState == "relayed" &&
            message.Id == id && message.ChatId == chat && message.SenderId == senderId && message.Text == marker &&
            message.Sequence == receipt.Sequence && receipt.Sequence > before.Sequence && receipt.QueuedConnections == 1,
            "Unexpected relay payload, order or number of recipients");

        var duplicates = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => sender.RelayMessage(chat, id, marker)));
        Expect(duplicates.All(duplicate => duplicate == receipt), "Retries did not return the first receipt");
        await Ping(socket);
        // Other connections may have the earlier stored event, but never the relay frame.
        await Ping(oldSocket, "message");
        await Ping(openButNotFollowing);
        await Ping(outsideSocket);

        await Error(() => recipient.RelayMessage(chat, id, "Different sender"), 409, "message_id_conflict");
        await Error(() => sender.RelayMessage(chat, previous, "Reusing stored ID"), 409, "message_id_conflict");
        await Error(() => sender.Send(HttpMethod.Post, $"chats/{chat}/messages", new { message_id = id, text = "Reusing relay ID" }, default),
            409, "message_id_conflict");
        await Error(() => sender.Send(HttpMethod.Post, $"messages/{previous}/forward", new { chat_id = chat, message_id = id }, default),
            409, "message_id_conflict");
        await Error(() => sender.EditMessage(id, "Not a stored message"), 404, "not_found");

        var next = await sender.SendMessage(chat, "Stored after relay");
        var history = await recipient.GetMessages(chat);
        Expect(history.Select(item => item.Id).SequenceEqual([previous, next]) && history[^1].Sequence > receipt.Sequence &&
            (await recipient.GetMessages(chat, afterSequence: receipt.Sequence)).Single().Id == next,
            "Relay appeared in history or corrupted the sequence cursor");

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM messages WHERE id=$id),
                   (SELECT COUNT(*) FROM relay_receipts WHERE id=$id AND sequence=$sequence AND queued_connections=1)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sequence", receipt.Sequence);
        using var rows = command.ExecuteReader();
        Expect(rows.Read() && rows.GetInt64(0) == 0 && rows.GetInt64(1) == 1, "Relay persisted a normal message or lost its receipt");

        Console.WriteLine("PASS relay API/WS: authorization, validation, live delivery, legacy and unfollowed sockets, retries, ID collisions and history exclusion");
        return new Fixture(chat, id, marker, receipt);
    }

    internal static async Task CheckRestart(MessengerClient client, Fixture fixture, string database)
    {
        var receipt = await client.RelayMessage(fixture.Chat, fixture.Id, fixture.Text);
        Expect(receipt == fixture.Receipt && (await client.GetMessages(fixture.Chat)).All(m => m.Id != fixture.Id),
            "Restart lost the relay receipt or replayed the message");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('relay_receipts') ORDER BY cid";
        using var cols = command.ExecuteReader();
        var fields = new List<string>();
        while (cols.Read()) fields.Add(cols.GetString(0));
        Expect(fields.SequenceEqual(["id", "chat_id", "sender_id", "sequence", "sent_at", "queued_connections"]),
            "Unexpected relay persistence fields");
        Console.WriteLine("PASS relay restart: stable metadata-only receipt and no message in history");
    }

    private static string NewMessage(string chat) => chat + "." + Guid.NewGuid().ToString("N");

    private static async Task<MessengerClient> Account(string address, string name)
    {
        var client = new MessengerClient(address);
        await client.Register(name, "fixture-password");
        await client.Login(name, "fixture-password");
        return client;
    }

    private static async Task<ClientWebSocket> Connect(MessengerClient client, bool relay)
    {
        var socket = new ClientWebSocket();
        if (relay) socket.Options.SetRequestHeader("Messenger-Message-Relay", "1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAuthenticatedSocket(socket, client.WebSocketUri(), timeout.Token);
        Expect((await Receive(socket)).Type == "connected", "WebSocket handshake event missing");
        return socket;
    }

    private static async Task Follow(ClientWebSocket socket, string chat, string expected)
    {
        await Send(socket, new { action = "follow", chat_id = chat });
        Expect((await Receive(socket)).Type == expected, "Unexpected follow result");
    }

    private static async Task Ping(ClientWebSocket socket, string? priorEvent = null)
    {
        await Send(socket, new { action = "ping" });
        var next = await Receive(socket);
        if (priorEvent != null && next.Type == priorEvent) next = await Receive(socket);
        Expect(next.Type == "pong", "Unexpected or duplicate relay frame");
    }

    private static async Task Send(ClientWebSocket socket, object frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
    }

    private static async Task<WsEventDto> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Expect(result.MessageType == WebSocketMessageType.Text, "WebSocket closed unexpectedly");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<WsEventDto>(data.ToArray(), MessengerClient.Json)
            ?? throw new Exception("Empty WebSocket event");
    }

    private static async Task Error(Func<Task> action, int status, string code)
    {
        try { await action(); }
        catch (ApiException error) when (error.Status == status && error.Code == code) { return; }
        throw new Exception("Expected API error " + code);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
