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
        var api = new MessengerClient(Server!, data.GetProperty("token").GetString());
        using var bob = new MessengerClient(Server!, data.GetProperty("bob_token").GetString());
        var me = await api.GetMe();
        var chatId = data.GetProperty("chat_id").GetInt64();
        var otherId = data.GetProperty("other_chat_id").GetInt64();
        var replyTarget = data.GetProperty("reply_target").GetInt64();
        var oldPin = data.GetProperty("old_pin").GetInt64();
        var vm = new ShellViewModel(nav, api, me);
        nav.Go(vm);
        await Until(() => vm.Chats.Count == 2, "Sequence chats did not load");
        var chat = vm.Chats.Single(c => c.Id == chatId);
        var other = vm.Chats.Single(c => c.Id == otherId);
        vm.SelectedChat = chat;
        await Until(() => vm.Messages.Count == 25, "Sequence history did not load");
        Expect(vm.Messages.Select(m => m.Sequence).SequenceEqual(vm.Messages.Select(m => m.Sequence).Order()),
            "Client sorted messages by ID instead of sequence");
        Expect(vm.Messages[0].Id > vm.Messages[1].Id, "Fixture IDs were not scrambled");
        var at = DateTimeOffset.UtcNow;
        var timeline = ShellViewModel.MergeTimeline([
            new MessageItem { Id = 9, Sequence = 1, SentAt = at },
            new MessageItem { Id = 1, Sequence = 2, SentAt = at.AddMinutes(-1) }], []);
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
        await Until(() => vm.PinList.Count == 2, "Shared pins did not load");
        Expect(vm.PinList.Select(p => p.Sequence).SequenceEqual([5L, 2L]), "Pins were sorted by ID");
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
        await Task.WhenAll(Enumerable.Range(0, 55).Select(n => api.SendMessage(chatId, "Reconnect check " + n)));
        await Until(() => vm.Status == "онлайн" && vm.Messages.Count == expectedCount,
            "Reconnect did not recover missing messages by sequence");
        Expect(vm.Messages.Select(m => m.Id).Distinct().Count() == expectedCount,
            "Reconnect duplicated messages");
        Console.WriteLine("PASS sequence UI: real WebSocket interruption and catch-up after_sequence");

        // More than one page; a bookmark in the oldest page must be upgraded by its ID.
        for (var n = 0; n < 55; n++) await api.SendMessage(chatId, "Older history check " + n);
        await Until(() => vm.Messages.Count >= 80, "Live sequence messages did not arrive");
        LocalPinStore.Save(api.ServerBase, me.Id, [new(chatId, oldPin, "Legacy local pin")]);
        vm.SelectedChat = other;
        vm.SelectedChat = chat;
        await Until(() => vm.Messages.Count == 50 && LocalPinStore.Load(api.ServerBase, me.Id)
            .Single().Sequence == 1, "Legacy local pin was not upgraded across history pages");
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
        window.Close();
    }
}
