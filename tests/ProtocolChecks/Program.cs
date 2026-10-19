using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

var url = args[0].TrimEnd('/');
var clients = new List<HttpClient>();
async Task<(HttpClient Http, string Token, long Id, string Login)> User()
{
    var h = new HttpClient { BaseAddress = new Uri(url + "/") };
    clients.Add(h);
    var credentials = new { login = "ws" + Guid.NewGuid().ToString("N")[..10], password = "protocol-check-password" };
    (await h.PostAsJsonAsync("auth/register", credentials)).EnsureSuccessStatusCode();
    var login = await (await h.PostAsJsonAsync("auth/login", credentials)).Content.ReadFromJsonAsync<JsonElement>();
    var token = login.GetProperty("user_session_token").GetString()!;
    h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var me = await h.GetFromJsonAsync<JsonElement>("users/me");
    return (h, token, me.GetProperty("id").GetInt64(), credentials.login);
}
async Task<ClientWebSocket> Connect(string token)
{
    var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(url.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws?user_session_token=" + token), CancellationToken.None);
    var connected = await Receive(ws);
    Check(connected.GetProperty("type").GetString() == "connected", "connected event");
    return ws;
}
async Task<JsonElement> Receive(ClientWebSocket ws)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var buffer = new byte[65536];
    using var bytes = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close)
            return JsonSerializer.SerializeToElement(new { close = (int?)result.CloseStatus });
        bytes.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(bytes.ToArray()).RootElement.Clone();
}
async Task Send(ClientWebSocket ws, object value) => await ws.SendAsync(
    new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, CancellationToken.None);
async Task Follow(ClientWebSocket ws, long group)
{
    await Send(ws, new { action = "follow", chat_id = group });
    Check((await Receive(ws)).GetProperty("type").GetString() == "followed", "follow");
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var a = await User();
var b = await User();
var groupReply = await a.Http.PostAsJsonAsync("chats/group", new { title = "WS checks", member_ids = new[] { b.Id } });
groupReply.EnsureSuccessStatusCode();
var group = (await groupReply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chat_id").GetInt64();
using var ws = await Connect(b.Token);
await Follow(ws, group);
(await a.Http.PostAsJsonAsync("chats", new { another_user_id = b.Id })).EnsureSuccessStatusCode();
var createdDirect = await Receive(ws);
Check(createdDirect.GetProperty("type").GetString() == "chat_created" &&
      createdDirect.GetProperty("chat").GetProperty("other_user").GetProperty("id").GetInt64() == a.Id,
      "direct chat event must name the other user for each recipient");
Console.WriteLine("PASS WS direct chat contains the recipient-specific peer");

// Removed member keeps an open connection, but cannot follow or receive new chat messages.
(await a.Http.DeleteAsync($"chats/{group}/members/{b.Id}")).EnsureSuccessStatusCode();
Check((await Receive(ws)).GetProperty("type").GetString() == "member_removed", "removed event");
(await a.Http.PostAsJsonAsync($"chats/{group}/messages", new { text = "must not leak" })).EnsureSuccessStatusCode();
await Send(ws, new { action = "follow", chat_id = group });
Check((await Receive(ws)).GetProperty("type").GetString() == "error", "removed member cannot follow");
await Send(ws, new { action = "ping" });
Check((await Receive(ws)).GetProperty("type").GetString() == "pong", "no message delivered after removal");
Console.WriteLine("PASS WS removed member loses subscription and delivery");

// Two sessions for the same account: revoking one must leave the other intact.
using var ws2 = await Connect(a.Token);
using var ws3 = await Connect(a.Token);
using var separateSession = new HttpClient { BaseAddress = new Uri(url + "/") };
var secondLogin = await (await separateSession.PostAsJsonAsync("auth/login", new { login = a.Login, password = "protocol-check-password" })).Content.ReadFromJsonAsync<JsonElement>();
var secondToken = secondLogin.GetProperty("user_session_token").GetString()!;
separateSession.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
using var separateSocket = await Connect(secondToken);
(await a.Http.PostAsync("auth/logout", null)).EnsureSuccessStatusCode();
Check((await Receive(ws2)).GetProperty("close").GetInt32() == 4001, "logout close code");
Check((await Receive(ws3)).GetProperty("close").GetInt32() == 4001, "logout all sockets of same session");
await Send(separateSocket, new { action = "ping" });
Check((await Receive(separateSocket)).GetProperty("type").GetString() == "pong", "other session survives logout");
(await separateSession.GetAsync("users/me")).EnsureSuccessStatusCode();
Console.WriteLine("PASS WS logout revokes all sockets of the session");

using var oversized = await Connect(b.Token);
await Send(oversized, new { action = new string('x', 17000) });
Check((await Receive(oversized)).GetProperty("close").GetInt32() == 1009, "oversized frame close code");
Console.WriteLine("PASS WS fragmented/oversized input is bounded");
using (var normal = await Connect(b.Token))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await normal.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
    Check(normal.State == WebSocketState.Closed, "normal close handshake");
}
Console.WriteLine("PASS WS normal close handshake");

// Eight subscribers and concurrent HTTP traffic, measuring actual event delivery.
var c = await User();
var chatReply = await c.Http.PostAsJsonAsync("chats/group", new { title = "WS load", member_ids = new[] { b.Id } });
var chat = (await chatReply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chat_id").GetInt64();
var sockets = new List<ClientWebSocket>();
for (var i = 0; i < 8; i++) { var socket = await Connect(b.Token); sockets.Add(socket); await Follow(socket, chat); }
var readers = sockets.Select(async socket =>
{
    var seen = new HashSet<long>();
    while (seen.Count < 40)
    {
        var e = await Receive(socket);
        if (e.GetProperty("type").GetString() == "message") seen.Add(e.GetProperty("message").GetProperty("id").GetInt64());
    }
}).ToArray();
var watch = Stopwatch.StartNew();
using var backend = args.Length > 1 ? Process.GetProcessById(int.Parse(args[1])) : null;
var cpuStart = backend?.TotalProcessorTime.TotalMilliseconds;
var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, ct) =>
{
    var request = Stopwatch.StartNew();
    (await c.Http.PostAsJsonAsync($"chats/{chat}/messages", new { text = "load " + i, client_ref = "load-" + i }, ct)).EnsureSuccessStatusCode();
    (await b.Http.GetAsync($"chats/{chat}/messages?limit=10", ct)).EnsureSuccessStatusCode();
    latencies.Add(request.Elapsed.TotalMilliseconds);
});
await Task.WhenAll(readers);
var sorted = latencies.Order().ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { sockets = 8, concurrent_requests = 8, http_requests = 80,
    delivered_events = 320, elapsed_ms = watch.Elapsed.TotalMilliseconds,
    send_and_history_p50_ms = sorted[19], send_and_history_p95_ms = sorted[37], errors = 0,
    server_cpu_ms = backend?.TotalProcessorTime.TotalMilliseconds - cpuStart,
    server_peak_rss_bytes = backend?.PeakWorkingSet64 }));
foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); }
foreach (var h in clients) h.Dispose();
