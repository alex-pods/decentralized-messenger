-- Демо-данные, чтобы ручки сразу что-то возвращали. В проде этой миграции быть не должно.
INSERT INTO users(tag, name) VALUES ('ivan', 'Иван'), ('nikita', 'Никита Бекоев'), ('anna', 'Анна');
INSERT INTO avatars(user_id, url) VALUES (1, '/files/avatars/ivan.png'), (2, '/files/avatars/nikita.png');
INSERT INTO chats(title) VALUES ('Команда');
INSERT INTO messages(chat_id, sender_id, text, sent_at) VALUES
    (1, 1, 'Привет! Глянул новую схему?',            '2026-10-20T10:00:00Z'),
    (1, 2, 'Да, ручка → сервис → репозитории',        '2026-10-20T10:01:00Z'),
    (1, 1, 'Тогда собираем по ней бэкенд',           '2026-10-20T10:02:00Z');
