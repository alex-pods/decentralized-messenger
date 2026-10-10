using LocalBackend.Remote;
using Messenger.Core;
using Messenger.Core.Interfaces;
using Messenger.Core.Models;
using Messenger.Core.Remote;
using Microsoft.Data.Sqlite;

namespace LocalBackend.Checks;

// Run against a disposable backend. Faults are injected at the HTTP boundary, SQLite and the server are real.
public static class SyncSelfCheck
{
    public static async Task<int> RunAsync(string server)
    {
        var directory = Path.Combine(Path.GetTempPath(), "messenger-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var alice = new MessengerClient(server);
            using var bob = new MessengerClient(server);
            var suffix = Guid.NewGuid().ToString("N")[..10];
            await alice.Register("a" + suffix, "Check-password-42");
            await alice.Login("a" + suffix, "Check-password-42");
            await bob.Register("b" + suffix, "Check-password-42");
            await bob.Login("b" + suffix, "Check-password-42");
            var me = await alice.GetMe();
            var peer = await bob.GetMe();
            var direct = await alice.CreateDirect(peer.Id);
            var group = await alice.CreateGroup("MVP checks", [peer.Id]);
            var account = new AccountIdentity(server, me.Id);
            var remote = new FaultRemote(alice);
            var store = await SqliteStoreFactory.OpenAsync(account, directory);
            var core = new MessengerSession(account, store, remote);
            await core.UpdateProfileAsync(MessengerSession.MapProfile(me));
            await core.SyncAsync();
            Require((await core.GetChatsAsync()).Count == 2, "direct + group cached");
            Require((await core.GetMembersAsync(group)).Count == 2, "group members cached");

            // Offline enqueue, process/session recreation, lost HTTP response, retry with the same client_id.
            remote.Offline = true;
            await core.QueueMessageAsync(direct, "survives restart");
            var pending = (await core.GetMessagesAsync(direct)).Single();
            Require(pending.Id < 0 && pending.SyncState == MessageSyncState.Pending, "offline pending visible");
            await Expect<HttpRequestException>(() => core.FlushAsync());
            store = await SqliteStoreFactory.OpenAsync(account, directory);
            core = new MessengerSession(account, store, remote);
            Require((await core.GetMessagesAsync(direct)).Single().LocalId == pending.LocalId, "offline restart preserves row");
            remote.Offline = false;
            remote.LoseNextResponse = true;
            await core.RetryAsync(direct);
            await Expect<HttpRequestException>(() => core.FlushAsync());
            Require((await alice.GetMessages(direct)).Count == 1, "server accepted before lost response");
            await core.RetryAsync(direct);
            await core.FlushAsync();
            await core.SyncAsync(direct);
            var sent = (await core.GetMessagesAsync(direct)).Single();
            Require(sent.LocalId == pending.LocalId && sent.Id > 0 && sent.SyncState == MessageSyncState.Synced, "local row confirmed");
            Require((await alice.GetMessages(direct)).Count == 1, "retry did not duplicate on server");
            Require((await store.GetDueOutboxAsync()).Count == 0, "outbox completed atomically");
            var wire = (await alice.GetMessages(direct)).Single();
            await core.ApplyAsync(new WsEventDto { Type = "message", ChatId = direct, Message = wire });
            Require((await core.GetMessagesAsync(direct)).Count == 1, "HTTP + sender echo deduplicated");
            var duplicate = await alice.SendQueuedMessage(direct, "survives restart", pending.ClientId!.Value, null);
            Require(duplicate.MessageId == sent.Id && DateTimeOffset.Parse(duplicate.SentAt!) == DateTimeOffset.Parse(sent.SentAt!), "idempotent result includes original time");
            await Expect<ApiException>(() => alice.SendQueuedMessage(group, "same key other chat", pending.ClientId.Value, null));
            var otherSender = await bob.SendQueuedMessage(direct, "sender-scoped key", pending.ClientId.Value, null);
            Require(otherSender.MessageId != sent.Id, "client_id scoped by sender");
            Console.WriteLine("OK: offline enqueue, restart, lost response, retries, sender echo, idempotency.");

            // Permanent failure remains visible and can be retried explicitly.
            await core.QueueMessageAsync(direct, "retry after rejection");
            remote.RejectNext = true;
            await core.FlushAsync();
            Require((await core.GetMessagesAsync(direct)).Any(m => m.SyncState == MessageSyncState.Failed), "failed status visible");
            await core.RetryAsync(direct);
            await core.FlushAsync();
            Require((await core.GetMessagesAsync(direct)).All(m => m.SyncState == MessageSyncState.Synced), "manual retry accepted");
            await core.QueueMessageAsync(direct, "reply", sent.LocalId);
            await core.FlushAsync();
            Require((await alice.GetMessages(direct)).Single(m => m.Text == "reply").ReplyToId == sent.Id, "reply maps local ID to server ID");
            await Expect<InvalidOperationException>(() => core.QueueMessageAsync(group, "cross-chat reply", sent.LocalId));

            // A real WebSocket message must survive a later REST not_stored marker and reopening SQLite.
            var followed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var live = new LiveConnection(alice))
            {
                live.Follow(direct);
                live.Event += e =>
                {
                    if (e.Type == "followed" && e.ChatId == direct) followed.TrySetResult();
                    if (e.Type == "message" && e.Message?.Text == "live only") _ = SaveLive(e);
                };
                async Task SaveLive(WsEventDto e)
                {
                    try { await core.ApplyAsync(e); received.TrySetResult(); }
                    catch (Exception ex) { received.TrySetException(ex); }
                }
                var running = live.RunAsync();
                await followed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await bob.UpdateSettings(false);
                await bob.SendMessage(direct, "live only");
                await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                live.Dispose();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await core.SyncAsync(direct);
            Require((await core.GetMessagesAsync(direct)).Any(m => m.Text == "live only" && m.ContentState == "not_stored"), "not_stored preserves received text");
            await bob.UpdateSettings(true);
            await core.MarkReadAsync(direct);
            await core.FlushAsync();
            Require((await alice.GetMembers(direct)).Single(m => m.Id == me.Id).LastReadMessageId >= otherSender.MessageId, "read outbox reached server");
            Require(!(await core.GetChatsAsync()).Single(c => c.Id == direct).Unread, "read count cleared");
            Console.WriteLine("OK: failed/manual retry, replies, WS persistence, not_stored, read acknowledgements.");

            // Offline edits/deletes are recovered by paginated history synchronization.
            for (var i = 0; i < 105; i++) await bob.SendMessage(direct, "page " + i);
            await core.SyncAsync(direct);
            var all = await core.GetMessagesAsync(direct, 1000);
            Require(all.Count > 100 && all.Any(m => m.Id == sent.Id), "full history pagination");
            await alice.EditMessage(sent.Id, "edited while disconnected");
            await core.SyncAsync(direct);
            Require((await core.GetMessagesAsync(direct, 1000)).Single(m => m.Id == sent.Id).Text == "edited while disconnected", "offline edit recovered");
            await alice.DeleteMessage(otherSender.MessageId, false);
            remote.FailOlderPage = true;
            await Expect<HttpRequestException>(() => core.SyncAsync(direct));
            Require((await core.GetMessagesAsync(direct, 1000)).Any(m => m.Id == otherSender.MessageId), "incomplete snapshot does not hide history");
            remote.FailOlderPage = false;
            await core.SyncAsync(direct);
            Require(!(await core.GetMessagesAsync(direct, 1000)).Any(m => m.Id == otherSender.MessageId), "hidden-for-me recovered");
            await alice.DeleteMessage(sent.Id, true);
            await core.SyncAsync(direct);
            await core.ApplyAsync(new WsEventDto { Type = "message", Message = wire });
            Require((await core.GetMessagesAsync(direct, 1000)).Single(m => m.Id == sent.Id).ContentState == "deleted", "stale event cannot resurrect deleted text");
            await alice.TransferOwner(group, peer.Id);
            await core.SyncAsync(group);
            Require((await core.GetMembersAsync(group)).Single(m => m.Role == "owner").Id == peer.Id, "owner transfer cached");
            await bob.RemoveMember(group, me.Id);
            await core.SyncAsync();
            Require(!(await core.GetChatsAsync()).Any(c => c.Id == group), "removed group disappears");
            await alice.HideChat(direct);
            await core.SyncAsync();
            Require((await core.GetChatsAsync()).Count == 0, "hidden chat disappears");
            await alice.CreateDirect(peer.Id);
            await core.SyncAsync();
            Require((await core.GetChatsAsync()).Count == 1, "restored direct retains cache");
            remote.Offline = true;
            var reopened = new MessengerSession(account, await SqliteStoreFactory.OpenAsync(account, directory), remote);
            Require((await reopened.GetMessagesAsync(direct, 1000)).Any(m => m.Text == "live only"), "received text survives offline reopen");
            Console.WriteLine("OK: pagination, partial failure, edit/delete reconciliation, group roles/removal, offline reopen.");

            remote.Offline = false;
            await alice.Logout();
            var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.SessionRevoked += () => revoked.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var worker = core.RunAsync(stop.Token);
            await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await worker;
            Console.WriteLine("OK: expired/revoked authentication stops synchronization.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    sealed class FaultRemote(IMessengerRemote inner) : IMessengerRemote
    {
        public bool Offline, LoseNextResponse, RejectNext, FailOlderPage;
        void Check() { if (Offline) throw new HttpRequestException("simulated offline"); }
        public Task<ProfileDto> GetMe(CancellationToken ct = default) { Check(); return inner.GetMe(ct); }
        public Task<ProfileDto> GetUser(long id, CancellationToken ct = default) { Check(); return inner.GetUser(id, ct); }
        public Task<List<ChatDto>> GetChats(CancellationToken ct = default) { Check(); return inner.GetChats(ct); }
        public Task<ChatDto> GetChat(long id, CancellationToken ct = default) { Check(); return inner.GetChat(id, ct); }
        public Task<List<MemberDto>> GetMembers(long id, CancellationToken ct = default) { Check(); return inner.GetMembers(id, ct); }
        public Task<List<MessageDto>> GetMessages(long id, int limit = 50, long? beforeId = null, CancellationToken ct = default)
        { Check(); if (FailOlderPage && beforeId is not null) throw new HttpRequestException("lost history page"); return inner.GetMessages(id, limit, beforeId, ct); }
        public Task MarkRead(long id, long messageId, CancellationToken ct = default) { Check(); return inner.MarkRead(id, messageId, ct); }
        public async Task<MessageIdDto> SendQueuedMessage(long id, string text, Guid key, long? reply, CancellationToken ct = default)
        {
            Check();
            if (RejectNext) { RejectNext = false; throw new ApiException(400, "simulated rejection"); }
            var result = await inner.SendQueuedMessage(id, text, key, reply, ct);
            if (LoseNextResponse) { LoseNextResponse = false; throw new HttpRequestException("response lost after server commit"); }
            return result;
        }
    }
}
