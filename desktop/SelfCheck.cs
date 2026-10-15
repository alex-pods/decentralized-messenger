using System.Text.Json;

namespace MessengerDesktop;

/// <summary>
/// Прогон клиента по живому серверу v4: регистрация, Bearer, личный чат, WS, группа, client_ref, after_id.
/// Запуск: dotnet run --project desktop/MessengerDesktop.csproj -- --check http://127.0.0.1:5080/messenger
/// </summary>
public static class SelfCheck
{
    public static int Run(string server)
    {
        try
        {
            RunAsync(server.Trim().TrimEnd('/')).GetAwaiter().GetResult();
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            return 1;
        }
    }

    static async Task RunAsync(string server)
    {
        using (var probe = new MessengerClient(server))
        {
            var health = await probe.Send<JsonElement>(HttpMethod.Get, "health", null, CancellationToken.None);
            if (health.GetProperty("status").GetString() != "ok")
                throw new Exception("health не ok");
            if (health.TryGetProperty("version", out var ver) && ver.GetString() is { } v && !v.StartsWith("4."))
                Console.WriteLine("warn: ожидалась версия 4.x, сервер ответил " + v);
        }
        Console.WriteLine("health");

        var tagA = Tag();
        var tagB = Tag();
        const string password = "password1";

        using var a = new MessengerClient(server);
        using var b = new MessengerClient(server);
        await a.Register(tagA, password);
        await b.Register(tagB, password);
        await a.Login(tagA, password);
        await b.Login(tagB, password);
        var meA = await a.GetMe();
        var meB = await b.GetMe();
        Console.WriteLine($"users {tagA}={meA.Id} {tagB}={meB.Id}");

        var found = await a.Search(tagB);
        if (found.All(u => u.Id != meB.Id)) throw new Exception("поиск не нашёл второго пользователя");

        var chatId = await a.CreateDirect(meB.Id);
        var again = await b.CreateDirect(meA.Id);
        if (again != chatId) throw new Exception("личный чат пары должен быть один");
        Console.WriteLine("direct " + chatId);

        using var liveB = new LiveConnection(b);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var got = new TaskCompletionSource<WsEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        liveB.State += s => { if (s == "онлайн") online.TrySetResult(); };
        liveB.Event += e =>
        {
            if (e.Type == "message" && e.ChatId == chatId) got.TrySetResult(e);
        };
        var run = liveB.RunAsync();
        await online.Task.WaitAsync(TimeSpan.FromSeconds(8));
        liveB.Follow(chatId);
        await Task.Delay(400);

        var clientRef = "check-" + Guid.NewGuid().ToString("N")[..12];
        var messageId = await a.SendMessage(chatId, "привет из проверки", clientRef: clientRef);
        var againId = await a.SendMessage(chatId, "привет из проверки", clientRef: clientRef);
        if (againId != messageId) throw new Exception("client_ref должен вернуть тот же message_id");
        try
        {
            await a.SendMessage(chatId, "ДРУГОЙ текст", clientRef: clientRef);
            throw new Exception("ожидали 409 client_ref_conflict");
        }
        catch (ApiException ex) when (ex.Status == 409 && ex.Code == "client_ref_conflict") { /* ok */ }

        var evt = await got.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (evt.Message?.Text != "привет из проверки") throw new Exception("WS не принёс текст");
        var history = await b.GetMessages(chatId);
        if (history.All(m => m.Id != messageId)) throw new Exception("история не содержит сообщение");
        await b.MarkRead(chatId, messageId);
        Console.WriteLine("message+ws+client_ref " + messageId);

        await a.EditMessage(messageId, "привет, уже изменённое");
        var edited = await b.GetMessages(chatId);
        if (edited.First(m => m.Id == messageId).Text != "привет, уже изменённое")
            throw new Exception("редактирование не сохранилось");
        Console.WriteLine("edit");

        var mid2 = await a.SendMessage(chatId, "второе", clientRef: Guid.NewGuid().ToString("N"));
        var mid3 = await a.SendMessage(chatId, "третье", clientRef: Guid.NewGuid().ToString("N"));
        var after = await b.GetMessages(chatId, limit: 50, afterId: messageId);
        if (after.Count < 2 || after[0].Id <= messageId)
            throw new Exception("after_id должен вернуть сообщения новее курсора по возрастанию");
        if (after.All(m => m.Id != mid2) || after.All(m => m.Id != mid3))
            throw new Exception("after_id не догнал новые сообщения");
        Console.WriteLine("after_id");

        var groupId = await a.CreateGroup("проверка " + tagA, new[] { meB.Id });
        var members = await a.GetMembers(groupId);
        if (members.Count != 2 || members.All(m => m.Role != "owner"))
            throw new Exception("группа создалась неверно");
        await a.SendMessage(groupId, "в группе", clientRef: Guid.NewGuid().ToString("N"));
        await b.Leave(groupId);
        var afterLeave = await a.GetMembers(groupId);
        if (afterLeave.Any(m => m.Id == meB.Id)) throw new Exception("участник не вышел");
        Console.WriteLine("group " + groupId);

        await a.DeleteMessage(messageId, true);
        var deleted = (await b.GetMessages(chatId)).First(m => m.Id == messageId);
        if (deleted.ContentState != "deleted") throw new Exception("удаление для всех не сработало");
        Console.WriteLine("delete");

        liveB.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* сокет уже закрыт */ }
    }

    static string Tag() => "u" + Guid.NewGuid().ToString("N")[..8];
}
