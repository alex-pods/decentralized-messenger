using Messenger.Api.Repositories;
using Messenger.Api.Repositories.Models;

namespace Messenger.Api.Database;

public static class DevSeed
{
    public static async Task Run(DbConnectionFactory db)
    {
        var users = new UsersRepository(db);
        if (await users.GetByTag("sergey") is not null) return;

        var sergey = await users.Insert("sergey", "Ваулин Сергей", "CHANGE_ME");
        var nikita = await users.Insert("nikita", "Никита Бекоев", "CHANGE_ME");
        var meganight = await users.Insert("meganight", "Меганайт", "CHANGE_ME");

        var chats = new ChatsRepository(db);
        var team = await chats.CreateGroupAsync(sergey, "Команда", [nikita, meganight]);
        var direct = await chats.GetOrCreateDirectAsync(sergey, meganight);

        var messages = new MessagesRepository(db);
        await messages.AddAsync(new NewMessage(team, sergey, "Привет! Глянул новую схему?", null, null));
        await messages.AddAsync(new NewMessage(team, nikita, "Да, ручка → сервис → репозитории", null, null));
        await messages.AddAsync(new NewMessage(team, sergey, "Тогда собираем по ней бэкенд", null, null));
        await messages.AddAsync(new NewMessage(direct.ChatId, meganight, "Привет! Скинь макет, пожалуйста", null, null));
        await messages.AddAsync(new NewMessage(direct.ChatId, sergey, "Сейчас отправлю", null, null));
    }
}
