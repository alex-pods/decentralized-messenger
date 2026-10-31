using Dapper;
using Messenger.Api.Infrastructure;

namespace Messenger.Api.Database;

public static class DevSeed
{
    public static void Run(DbConnectionFactory db)
    {
        using var c = db.Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM users") > 0) return;

        var hash = Crypto.Encrypt("CHANGE_ME");
        c.Execute("INSERT INTO users(tag, name, password_hash) VALUES (@tag, @name, @hash)", new[]
        {
            new { tag = "sergey", name = Crypto.Encrypt("Ваулин Сергей"), hash },
            new { tag = "nikita", name = Crypto.Encrypt("Никита Бекоев"), hash },
            new { tag = "meganight", name = Crypto.Encrypt("Меганайт"), hash }
        });

        c.Execute("INSERT INTO avatars(user_id, path, content_type) VALUES (@id, @path, 'image/png')", new[]
        {
            new { id = 1, path = Crypto.Encrypt("/files/avatars/sergey.png") },
            new { id = 2, path = Crypto.Encrypt("/files/avatars/nikita.png") }
        });

        c.Execute("INSERT INTO chats(type, title, created_by) VALUES ('group', @title, 1)",
            new { title = Crypto.Encrypt("Команда") });
        c.Execute(
            "INSERT INTO users_chats(user_id, chat_id, role) VALUES (1, 1, 'owner'), (2, 1, 'member'), (3, 1, 'member')");
        c.Execute("INSERT INTO chats(type, user_a_id, user_b_id, created_by) VALUES ('direct', 1, 3, 1)");

        c.Execute("INSERT INTO messages(chat_id, sender_id, text, sent_at) VALUES (@chat, @sender, @text, @sentAt)", new[]
        {
            new { chat = 1, sender = 1, text = Crypto.Encrypt("Привет! Глянул новую схему?"), sentAt = "2026-10-20 10:00:00" },
            new { chat = 1, sender = 2, text = Crypto.Encrypt("Да, ручка → сервис → репозитории"), sentAt = "2026-10-20 10:01:00" },
            new { chat = 1, sender = 1, text = Crypto.Encrypt("Тогда собираем по ней бэкенд"), sentAt = "2026-10-20 10:02:00" },
            new { chat = 2, sender = 3, text = Crypto.Encrypt("Привет! Скинь макет, пожалуйста"), sentAt = "2026-10-20 10:05:00" },
            new { chat = 2, sender = 1, text = Crypto.Encrypt("Сейчас отправлю"), sentAt = "2026-10-20 10:06:00" }
        });
    }
}
