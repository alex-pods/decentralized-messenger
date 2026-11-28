using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MessengerDesktop;

public static partial class Preview
{
    static async Task AuditSequences(Window window, Navigator nav)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("MESSENGER_SEQUENCE_FIXTURE")!));
        var data = fixture.RootElement;
        var transport = new SequenceHistoryTransport();
        var api = new MessengerClient(Server!, data.GetProperty("token").GetString(), transport);
        using var bob = new MessengerClient(Server!, data.GetProperty("bob_token").GetString());
        var me = await api.GetMe();
        var chatId = data.GetProperty("chat_id").GetString()!;
        var otherId = data.GetProperty("other_chat_id").GetString()!;
        var replyTarget = data.GetProperty("reply_target").GetString()!;
        var oldPin = data.GetProperty("old_pin").GetString()!;
        LocalPinStore.Save(api.ServerBase, me.Id, [new(chatId, oldPin, "Legacy local pin")]);
        var vm = new ShellViewModel(nav, api, me);
        nav.Go(vm);
        await Until(() => vm.Chats.Count == 2, "Sequence chats did not load");
        var chat = vm.Chats.Single(c => c.Id == chatId);
        var other = vm.Chats.Single(c => c.Id == otherId);
        vm.SelectedChat = chat;
        await Until(() => vm.Messages.Count == 25, "Sequence history did not load");
        Expect(vm.Messages.Select(m => m.Sequence).SequenceEqual(vm.Messages.Select(m => m.Sequence).Order()),
            "Client sorted messages by ID instead of sequence");
        Expect(string.CompareOrdinal(vm.Messages[0].Id, vm.Messages[1].Id) > 0, "Fixture IDs were not scrambled");
        var at = DateTimeOffset.UtcNow;
        var timeline = ShellViewModel.MergeTimeline([
            new MessageItem { Id = "later", Sequence = 1, SentAt = at },
            new MessageItem { Id = "earlier", Sequence = 2, SentAt = at.AddMinutes(-1) }], []);
        Expect(timeline.OfType<MessageItem>().Select(m => m.Sequence).SequenceEqual([1L, 2L]),
            "Clock changes reordered message timeline");
        await OnUiAsync(async () =>
        {
            vm.Draft = "Sent through sequence-aware composer";
            await vm.SendCommand.ExecuteAsync(null);
        });
        await Until(() => vm.Messages.LastOrDefault()?.Text == "Sent through sequence-aware composer",
            "Composer did not merge the sent message");
        await bob.MarkRead(chatId, vm.Messages[^1].Id);
        await Until(() => vm.Messages[^1].Meta.Contains("прочитано"), "Read event did not use last_read_sequence");
        await Until(() => vm.PinList.Count == 3, "Shared or imported pins did not load");
        Expect(vm.PinList.Where(p => !p.Local).Select(p => p.Sequence).SequenceEqual([5L, 2L]), "Pins were sorted by ID");
        var visible = vm.Messages.Single(m => m.Sequence == 3);
        vm.SyncPinToVisible(visible);
        Expect(vm.PinText == vm.PinList.Single(p => p.Sequence == 2).Text, "Visible pin selection used ID order");
        await Shot(window, "sequence-scrambled-ids");
        Console.WriteLine("PASS sequence UI: scrambled IDs, clock changes, send/merge, read events and pin order");

        await Until(() => vm.Status == "онлайн", "WebSocket did not connect");
        var expectedCount = vm.Messages.Count + 55;
        const System.Reflection.BindingFlags privateField = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var live = (LiveConnection)typeof(ShellViewModel).GetField("_live", privateField)!.GetValue(vm)!;
        ((System.Net.WebSockets.ClientWebSocket)typeof(LiveConnection).GetField("_ws", privateField)!.GetValue(live)!).Abort();
        await Until(() => vm.Status == "переподключение…", "WebSocket disconnect was not observed");
        var edited = vm.Messages.LastOrDefault(m => m.Mine && m.Text == "Sent through sequence-aware composer")!.Id;
        await api.EditMessage(edited, "Edited during disconnect");
        await Task.WhenAll(Enumerable.Range(0, 55).Select(n => api.SendMessage(chatId, "Reconnect check " + n)));
        await Until(() => vm.Status == "онлайн" && vm.Messages.Count == expectedCount,
            "Reconnect did not recover missing messages by sequence");
        Expect(vm.Messages.Select(m => m.Id).Distinct().Count() == expectedCount,
            "Reconnect duplicated messages");
        Expect(vm.Messages.Single(m => m.Id == edited).Text == "Edited during disconnect",
            "Reconnect missed an edit before the last known sequence");
        Console.WriteLine("PASS sequence UI: real WebSocket interruption and catch-up after_sequence");

        // More than one page; a bookmark in the oldest page must be upgraded by its ID.
        for (var n = 0; n < 55; n++) await api.SendMessage(chatId, "Older history check " + n);
        await Until(() => vm.Messages.Count >= 80, "Live sequence messages did not arrive");
        vm.SelectedChat = other;
        vm.SelectedChat = chat;
        await Until(() => vm.Messages.Count == 50 && vm.PinList.Any(pin => pin.Local && pin.Sequence == 1),
            "Legacy local pin lost its sequence after reopening the chat");
        await OnUiAsync(() => vm.JumpToReply(replyTarget));
        await Until(() => vm.SelectedMessage?.Id == replyTarget, "Reply jump did not load older sequence pages");
        Expect(vm.Messages.Select(m => m.Sequence).SequenceEqual(vm.Messages.Select(m => m.Sequence).Order()),
            "Older pages broke sequence order");
        Expect(vm.Messages.Select(m => m.Id).Distinct().Count() == vm.Messages.Count,
            "History jump duplicated messages");
        var newer = await api.SendMessage(otherId, "Newer chat with a smaller per-chat sequence");
        await Until(() => vm.Chats.First().Id == otherId, "Chat list compared sequences from different chats");
        await OnUiAsync(async () =>
        {
            vm.SelectedMessage = vm.Messages.Single(m => m.Id == replyTarget);
            vm.ShowForwardCommand.Execute(null);
            await vm.ForwardToCommand.ExecuteAsync(other);
        });
        vm.SelectedChat = other;
        await Until(() => vm.Messages.Any(m => m.IsForwarded), "Forwarded message did not load");
        await Shot(window, "sequence-forward-and-chat-order");
        Console.WriteLine("PASS sequence UI: legacy local pins, multi-page reply jump, forwarding and chat activity order");

        await AuditRelay(window, vm, api, bob, chatId, otherId);
        transport.Offline = true;
        vm.SelectedChat = vm.Chats.Single(c => c.Id == otherId);
        vm.SelectedChat = vm.Chats.Single(c => c.Id == chatId);
        await Until(() => vm.Messages.Count == 50 && vm.Messages[^1].Text == "Older history check 54" &&
            vm.Error?.Contains("история с устройства") == true, "Offline history did not load from disk");
        await OnUiAsync(() => vm.JumpToReply(replyTarget));
        await Until(() => vm.SelectedMessage?.Id == replyTarget, "Offline reply jump did not page through saved history");
        Expect(vm.PinList.Any(pin => pin.Local && pin.MessageId == oldPin), "Offline personal pin was missing");
        transport.Offline = false;
        transport.DeniedChat = chatId;
        vm.SelectedChat = vm.Chats.Single(c => c.Id == otherId);
        vm.SelectedChat = vm.Chats.Single(c => c.Id == chatId);
        await Until(() => vm.Chats.All(c => c.Id != chatId) && vm.SelectedChat == null && vm.Messages.Count == 0,
            "The client fell back to cache for a denied chat");
        Console.WriteLine("PASS local history UI: offline reopen, cached reply jump and pins, access denied clears the chat");
        window.Close();
    }

    private static async Task AuditRelay(Window window, ShellViewModel vm, MessengerClient api, MessengerClient bob,
        string chatId, string otherId)
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        socket.Options.SetRequestHeader("Messenger-Message-Relay", "1");
        await bob.ConnectAuthenticatedSocket(socket, bob.WebSocketUri(), ct);
        await ReadEvent("connected");
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { action = "follow", chat_id = chatId }).AsMemory(),
            System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        await ReadEvent("followed");

        vm.SelectedChat = vm.Chats.Single(chat => chat.Id == chatId);
        await Until(() => vm.CanToggleRelay && vm.Messages.Count == 50, "Relay control did not become available");
        const string text = "Live only from the Desktop composer";
        vm.ToggleRelayCommand.Execute(null);
        vm.Draft = text;
        vm.SelectedChat = vm.Chats.Single(chat => chat.Id == otherId);
        Expect(!vm.RelayArmed, "Relay mode affected an unrelated chat");
        vm.SelectedChat = vm.Chats.Single(chat => chat.Id == chatId);
        Expect(vm.RelayArmed && vm.Draft == text, "Returning to a relay draft switched it back to stored mode");
        await Until(() => vm.Messages.Count == 50, "Relay chat did not reopen");
        await OnUiAsync(() => vm.SendCommand.ExecuteAsync(null));
        var outgoing = await ReadEvent("message_relayed");
        Expect(outgoing.Message?.Text == text && vm.Messages.LastOrDefault()?.IsRelayed == true &&
            !vm.RelayArmed && vm.Draft.Length == 0, "Relay composer did not send or reset correctly");

        var replyId = chatId + "." + Guid.NewGuid().ToString("N");
        await bob.RelayMessage(chatId, replyId, "Live only from Bob");
        await Until(() => vm.Messages.Any(message => message.Id == replyId && message.IsRelayed), "Live incoming text was not shown");
        vm.SelectedChat = vm.Chats.Single(chat => chat.Id == otherId);
        vm.SelectedChat = vm.Chats.Single(chat => chat.Id == chatId);
        await Until(() => vm.Messages.Count == 52, "Relay disappeared while the session was still open");
        await Shot(window, "sequence-relay");

        var stored = new LocalHistoryStore(api.ServerFingerprint!, (await api.GetMe()).Id);
        Expect(stored.ReadMessages(chatId, 100).All(message => message.ContentState != "relayed"),
            "The Desktop cache recorded live messages");
        Expect((await api.GetMessages(chatId)).All(message => message.Id != outgoing.Message!.Id && message.Id != replyId),
            "The relay became part of regular history");
        foreach (var id in new[] { outgoing.Message!.Id, replyId })
        {
            vm.SelectedMessage = vm.Messages.Single(message => message.Id == id);
            Expect(!vm.CanPinLocally && !vm.CanForward && !vm.CanDeleteEveryone, "Stored message actions were exposed on a relay");
            await OnUiAsync(() => vm.DeleteForMeCommand.ExecuteAsync(null));
        }
        Console.WriteLine("PASS relay UI: per-chat draft mode, two-way rendering, session-only history and local dismissal");

        async Task<WsEventDto> ReadEvent(string type)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var frame = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (!frame.EndOfMessage || frame.MessageType != System.Net.WebSockets.WebSocketMessageType.Text)
                    throw new Exception("Invalid relay test frame");
                var evt = JsonSerializer.Deserialize<WsEventDto>(buffer.AsSpan(0, frame.Count), MessengerClient.Json);
                if (evt?.Type == type) return evt;
            }
        }
    }

    private sealed class SequenceHistoryTransport() : DelegatingHandler(
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        internal volatile bool Offline;
        internal volatile string? DeniedChat;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("Sequence test transport is offline");
            if (DeniedChat is { } chat && request.RequestUri!.AbsolutePath.Contains("/chats/" + chat + "/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            return base.SendAsync(request, ct);
        }
    }
}
