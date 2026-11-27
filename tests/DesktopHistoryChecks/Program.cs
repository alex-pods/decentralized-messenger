using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MessengerDesktop;
using MessengerDesktop.Views;

// Actual ShellView + HTTP/WebSocket fixture; no running backend, accounts or database needed.
AppBuilder.Configure<App>().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont().SetupWithoutStarting();
using var server = new HistoryServer();
using var api = new MessengerClient(server.Url, "test");
var window = new Window { Width = 1100, Height = 760 };
var vm = new ShellViewModel(new Navigator(window), api, new ProfileDto { Id = 1, Tag = "me", DisplayName = "Me" });
var view = new ShellView { DataContext = vm };
window.Content = view;
window.Show();
var log = view.FindControl<ListBox>("Log")!;
ScrollViewer Scroll() => log.GetVisualDescendants().OfType<ScrollViewer>().First();

void Pump()
{
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
}

void Until(Func<bool> condition, string label)
{
    var timer = Stopwatch.StartNew();
    while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10))
    {
        Pump();
        Thread.Sleep(5);
    }
    Pump();
    if (!condition()) throw new Exception($"Timeout: {label}; UI error: {vm.Error}");
}

void Settle()
{
    for (var i = 0; i < 30; i++) { Pump(); Thread.Sleep(5); }
}

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("OK " + label);
}

void Open(long id, int count)
{
    vm.SelectedChat = vm.Chats.Single(c => c.Id == id);
    Until(() => !vm.IsOpeningChat && vm.Messages.Count == count, "open chat " + id);
    Settle();
}

double BubbleY(MessageItem item)
{
    var container = log.ContainerFromItem(item)!;
    var bubble = container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("bubble"));
    return bubble.TranslatePoint(default, Scroll())!.Value.Y;
}

(MessageItem Item, double Y) Anchor()
{
    var first = vm.Messages.Select(m => (Item: m, Y: BubbleY(m)))
        .First(m => m.Y >= 0 && m.Y < Scroll().Viewport.Height);
    return first;
}

void At(double y)
{
    Scroll().Offset = new Vector(0, y);
    Pump();
}

try
{
    Until(() => vm.Chats.Count == 4 && server.Socket != null, "chat list and socket");
    Open(1, 50);
    Check(vm.Messages[0].Id == 201 && server.OlderRequests == 0, "opening loads only the latest 50 messages");
    Check(Scroll().Extent.Height - Scroll().Viewport.Height - Scroll().Offset.Y < 2, "opening starts at the bottom");
    Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Показать раньше"), "manual history button removed");

    server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var selected = vm.Messages[^1];
    vm.SelectedMessage = selected;
    Settle();
    At(80);
    Until(() => server.OlderRequests == 1 && vm.IsLoadingHistory, "automatic history request");
    At(40);
    At(20);
    vm.Draft = "I can still type and send";
    var duplicate = vm.LoadOlderAsync();
    Check(duplicate.IsCompletedSuccessfully && !duplicate.Result && vm.CanSendDraft, "one request at a time, composer remains usable");
    var anchor = Anchor();
    server.Gate.SetResult();
    Until(() => vm.Messages.Count == 100 && !vm.IsLoadingHistory, "first older page");
    Settle();
    Check(Math.Abs(BubbleY(anchor.Item) - anchor.Y) < 2, "prepend preserves the visible bubble position");
    Check(ReferenceEquals(vm.SelectedMessage, selected), "paging keeps the selected message");
    Check(server.OlderRequests == 1, "layout changes do not download all history");

    server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    At(0);
    Until(() => server.OlderRequests == 2, "second page request");
    At(1600); // The user continues scrolling while the HTTP response is delayed.
    anchor = Anchor();
    server.Gate.SetResult();
    Until(() => vm.Messages.Count == 150 && !vm.IsLoadingHistory, "second older page");
    Check(Math.Abs(BubbleY(anchor.Item) - anchor.Y) < 2, "scrolling during a request is preserved");

    anchor = Anchor();
    server.Push(251).GetAwaiter().GetResult();
    Until(() => vm.Messages.Any(m => m.Id == 251), "incoming while reading");
    Settle();
    Check(Math.Abs(BubbleY(anchor.Item) - anchor.Y) < 2, "incoming messages do not pull the reader down");
    Scroll().ScrollToEnd();
    Settle();
    server.Push(252).GetAwaiter().GetResult();
    Until(() => vm.Messages.Any(m => m.Id == 252), "incoming at the bottom");
    Settle();
    Check(Scroll().Extent.Height - Scroll().Viewport.Height - Scroll().Offset.Y < 2, "incoming messages follow at the bottom");

    server.Gate = null;
    server.FailNext = true;
    var requests = server.OlderRequests;
    At(0);
    Until(() => vm.Error != null && !vm.IsLoadingHistory, "network error");
    Settle();
    Check(server.OlderRequests == requests + 1 && vm.Messages.Count == 152, "error retains history without an automatic retry loop");
    At(300);
    At(0);
    Until(() => vm.Messages.Count == 202, "retry after another scroll");
    Check(vm.Error == null, "scrolling retries a failed page");

    while (vm.HasMore)
    {
        requests = server.OlderRequests;
        At(0);
        Until(() => server.OlderRequests > requests && !vm.IsLoadingHistory, "reach history beginning");
    }
    Check(vm.Messages.Count == 252 && vm.Messages.Select(m => m.Id).SequenceEqual(Enumerable.Range(1, 252).Select(i => (long)i)),
        "all pages are ordered, complete and unique");
    requests = server.OlderRequests;
    At(0);
    Settle();
    Check(server.OlderRequests == requests, "no more requests after the beginning");

    Open(2, 5);
    Check(!vm.HasMore && server.OlderRequests == requests, "short chat does not request older history");
    Open(3, 0);
    Check(!vm.HasMore && server.OlderRequests == requests, "empty chat does not request history");
    Open(1, 50);
    server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    At(0);
    Until(() => vm.IsLoadingHistory && server.OlderRequests == requests + 1, "delayed request before chat switch");
    Open(2, 5);
    server.Gate.SetResult();
    Settle();
    Check(!vm.IsLoadingHistory && vm.Messages.All(m => m.Id >= 1001), "switching chats ignores the old response and releases the loader");

    server.Gate = null;
    Open(1, 50);
    Until(() => vm.HasPins, "pins");
    server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    requests = server.OlderRequests;
    At(0);
    Until(() => server.OlderRequests > requests, "history request before pin jump");
    var cancelledHistory = server.Gate;
    server.Gate = null;
    var jump = vm.ShowPinnedCommand.ExecuteAsync(null);
    Until(() => jump.IsCompleted && vm.SelectedMessage?.Id == 10, "jump to an old pin");
    cancelledHistory.SetResult();
    Settle();
    Check(BubbleY(vm.SelectedMessage!) >= 0 && BubbleY(vm.SelectedMessage!) < Scroll().Viewport.Height,
        "old pin is loaded and scrolled into view");
    Check(vm.Messages.Select(m => m.Id).Distinct().Count() == vm.Messages.Count, "pin navigation and paging do not duplicate messages");

    Open(4, 50);
    At(0);
    Until(() => vm.Messages.Count == 100 && !vm.IsLoadingHistory, "full final page");
    At(0);
    Until(() => !vm.HasMore && !vm.IsLoadingHistory, "empty page after exactly 100 messages");
    Check(vm.Messages.Count == 100, "an exact multiple of the page size terminates on an empty page");
    Console.WriteLine("PASS desktop history checks");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    vm.SelectedChat = null;
    view.DataContext = null;
    window.Close();
}

sealed class HistoryServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<long, MessageDto> _messages = new();
    int _olderRequests;
    public string Url { get; }
    public WebSocket? Socket { get; private set; }
    public TaskCompletionSource? Gate { get; set; }
    public bool FailNext { get; set; }
    public int OlderRequests => Volatile.Read(ref _olderRequests);

    public HistoryServer()
    {
        var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        for (var i = 1; i <= 250; i++) _messages[i] = Message(i, 1);
        for (var i = 1001; i <= 1005; i++) _messages[i] = Message(i, 2);
        for (var i = 2001; i <= 2100; i++) _messages[i] = Message(i, 4);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { _ = Handle(await _listener.GetContextAsync()); }
                catch (Exception) when (_stop.IsCancellationRequested) { break; }
            }
        });
    }

    static MessageDto Message(long id, long chat) => new()
    {
        Id = id, ChatId = chat, SenderId = 2,
        Text = id % 7 == 0 ? $"Message {id}\nA second line\nA third line" : $"Message {id}",
        SentAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(id).ToString("O")
    };

    public async Task Push(long id)
    {
        var message = Message(id, 1);
        _messages[id] = message;
        await Socket!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new WsEventDto { Type = "message", ChatId = 1, Message = message }),
            WebSocketMessageType.Text, true, _stop.Token);
    }

    async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath.Trim('/').Split('/');
            if (path[0] == "ws")
            {
                Socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var buffer = new byte[4096];
                while (!_stop.IsCancellationRequested && Socket.State == WebSocketState.Open)
                    await Socket.ReceiveAsync(buffer, _stop.Token);
                return;
            }
            object body = new { };
            if (path[0] == "users") { context.Response.StatusCode = 404; }
            else if (path.Length == 1 && path[0] == "chats")
                body = Enumerable.Range(1, 4).Select(id => new ChatDto { Id = id, Type = "group", Title = "Chat " + id }).ToArray();
            else if (path.Length >= 3 && path[0] == "chats")
            {
                var chat = long.Parse(path[1]);
                if (path[2] == "members")
                    body = new[] { new MemberDto { Id = 1, Role = "owner", DisplayName = "Me" }, new MemberDto { Id = 2, DisplayName = "Other" } };
                if (path[2] == "pins")
                    body = chat == 1 ? new[] { new PinDto { MessageId = 10, Message = _messages[10] } } : Array.Empty<PinDto>();
                if (path[2] == "messages")
                {
                    var query = context.Request.QueryString;
                    var all = _messages.Values.Where(m => m.ChatId == chat).OrderBy(m => m.Id).ToArray();
                    if (long.TryParse(query["before_id"], out var before))
                    {
                        Interlocked.Increment(ref _olderRequests);
                        var gate = Gate;
                        if (gate != null) await gate.Task.WaitAsync(_stop.Token);
                        if (FailNext) { FailNext = false; context.Response.StatusCode = 503; body = new { error = "Temporary test failure" }; }
                        else body = all.Where(m => m.Id < before).TakeLast(50).ToArray();
                    }
                    else if (long.TryParse(query["after_id"], out var after)) body = all.Where(m => m.Id > after).Take(50).ToArray();
                    else body = all.TakeLast(50).ToArray();
                }
            }
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, body, body.GetType(), cancellationToken: _stop.Token);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The client deliberately cancels a delayed request when changing chats.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Socket?.Abort();
        Socket?.Dispose();
        _listener.Close();
        _stop.Dispose();
    }
}
