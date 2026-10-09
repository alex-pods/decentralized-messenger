using LocalBackend.Db;
using LocalBackend.Models;
using LocalBackend.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LocalBackend.Checks;

// Отдельный режим проверки на временной БД, без сети и без пользовательских данных.
public static class GroupSelfCheck
{
    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "messenger-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = MessengerDb.CreateFactory(Path.Combine(directory, "test.db"));
            await DatabaseInitializer.InitializeAsync(factory);
            await DatabaseInitializer.InitializeAsync(factory);
            var store = new MessageStore(factory);
            var now = DateTime.UtcNow;
            for (var id = 1; id <= 3; id++)
                await store.UpsertUserAsync(new User
                {
                    Id = id, Tag = "user" + id, DisplayName = "User " + id, JoinDate = now
                });

            await store.UpsertChatAsync(new ChatInfo(10, "direct", null, 2, now));
            await store.ReplaceMembersAsync(10,
                [new(1, "member", now), new(2, "member", now)]);
            await store.UpsertChatAsync(new ChatInfo(20, "group", "Study", null, now));
            await store.ReplaceMembersAsync(20,
                [new(1, "owner", now), new(2, "member", now)]);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var chat = await db.Chats.FindAsync(20L);
                chat!.DraftText = "unsent draft";
                chat.IsPinned = true;
                await db.SaveChangesAsync();
            }
            await store.UpsertChatAsync(new ChatInfo(20, "group", "New title", null, now));
            var group = (await store.GetChatListAsync()).Single(c => c.Id == 20);
            Require(group.Title == "New title" && group.DraftText == "unsent draft" && group.IsPinned,
                "Обновление чата должно сохранять черновик и закрепление.");

            await store.UpsertMemberAsync(20, new(3, "member", now));
            await store.UpsertMemberAsync(20, new(3, "admin", now));
            Require((await store.GetMembersAsync(20)).Single(m => m.UserId == 3).Role == "admin",
                "Роль участника должна обновиться без дубликата.");
            await store.RemoveMemberAsync(20, 3);
            await store.RemoveMemberAsync(20, 3);

            // Передача владения: прежний owner -> admin, новый -> owner.
            await store.ReplaceMembersAsync(20,
                [new(1, "admin", now), new(2, "owner", now)]);
            Require((await store.GetMembersAsync(20)).Single(m => m.Role == "owner").UserId == 2,
                "Владелец должен смениться.");

            // Ошибка после DELETE внутри ReplaceMembersAsync: проверяем откат состава.
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER RejectTestMember BEFORE INSERT ON ChatMembers
                    WHEN NEW.UserId = 3 BEGIN SELECT RAISE(ABORT, 'test insert failure'); END;
                    """);
            try
            {
                await store.ReplaceMembersAsync(20,
                    [new(2, "owner", now), new(3, "member", now)]);
                throw new InvalidOperationException("Ожидалась ошибка вставки участника.");
            }
            catch (DbUpdateException) { }
            var members = await store.GetMembersAsync(20);
            Require(members.Count == 2 && members.Any(m => m.UserId == 1) &&
                members.Single(m => m.Role == "owner").UserId == 2,
                "Неудачная замена должна сохранить прежний состав.");
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectTestMember;");

            var pending = await store.EnqueueOutgoingAsync(20, 1, "hello group");
            Require((await store.GetDueOutboxAsync()).Any(o => o.MessageId == pending.Id),
                "Сообщение должно попасть в Outbox.");
            await store.MarkSentAsync(pending.ClientId, 100, now);
            Require(!(await store.GetDueOutboxAsync()).Any(o => o.MessageId == pending.Id),
                "Подтверждённая отправка должна уйти из Outbox.");
            var saved = (await store.GetMessagesPageAsync(20)).Single();
            Require(saved.ServerId == 100 && saved.SyncState == MessageSyncState.Synced,
                "Серверный ID и статус должны сохраниться.");

            Console.WriteLine("OK: schema, chats, local settings, members, owner transfer, rollback, outbox.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* уборка временной БД не скрывает результат проверки */ }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
