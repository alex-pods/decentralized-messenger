using System.Text.Json;

namespace MessengerDesktop;

/// <summary>
/// Прогон клиента по живому серверу: регистрация, личный чат, WebSocket, группа, save_history.
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

        var messageId = await a.SendMessage(chatId, "привет из проверки");
        var evt = await got.Task.WaitAsync(TimeSpan.FromSeconds(8));
        if (evt.Message?.Text != "привет из проверки") throw new Exception("WS не принёс текст");
        var history = await b.GetMessages(chatId);
        if (history.All(m => m.Id != messageId)) throw new Exception("история не содержит сообщение");
        await b.MarkRead(chatId, messageId);
        Console.WriteLine("message+ws " + messageId);

        await a.EditMessage(messageId, "привет, уже изменённое");
        var edited = await b.GetMessages(chatId);
        if (edited.First(m => m.Id == messageId).Text != "привет, уже изменённое")
            throw new Exception("редактирование не сохранилось");
        Console.WriteLine("edit");

        var groupId = await a.CreateGroup("проверка " + tagA, new[] { meB.Id });
        var members = await a.GetMembers(groupId);
        if (members.Count != 2 || members.All(m => m.Role != "owner"))
            throw new Exception("группа создалась неверно");
        await a.SendMessage(groupId, "в группе");
        await b.Leave(groupId);
        var afterLeave = await a.GetMembers(groupId);
        if (afterLeave.Any(m => m.Id == meB.Id)) throw new Exception("участник не вышел");
        Console.WriteLine("group " + groupId);

        await b.UpdateSettings(false);
        var secretId = await b.SendMessage(chatId, "секрет-" + tagB);
        var stored = await a.GetMessages(chatId);
        var secret = stored.First(m => m.Id == secretId);
        if (secret.ContentState != "not_stored" || !string.IsNullOrEmpty(secret.Text))
            throw new Exception("save_history=false должен оставить заглушку без текста");
        Console.WriteLine("save_history");

        await a.DeleteMessage(messageId, true);
        var deleted = (await b.GetMessages(chatId)).First(m => m.Id == messageId);
        if (deleted.ContentState != "deleted") throw new Exception("удаление для всех не сработало");
        Console.WriteLine("delete");

        liveB.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* сокет уже закрыт */ }
    }

    static string Tag() => "u" + Guid.NewGuid().ToString("N")[..8];
}
