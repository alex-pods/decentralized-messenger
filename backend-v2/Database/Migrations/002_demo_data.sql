INSERT INTO users(tag, name, password_hash) VALUES
    ('sergey',    'Ваулин Сергей', 'CHANGE_ME'),
    ('nikita',    'Никита Бекоев', 'CHANGE_ME'),
    ('meganight', 'Меганайт',      'CHANGE_ME');

INSERT INTO avatars(user_id, path, content_type) VALUES
    (1, '/files/avatars/sergey.png', 'image/png'),
    (2, '/files/avatars/nikita.png', 'image/png');

INSERT INTO chats(type, title, created_by) VALUES
    ('group', 'Команда', 1);

INSERT INTO users_chats(user_id, chat_id, role) VALUES
    (1, 1, 'owner'),
    (2, 1, 'member'),
    (3, 1, 'member');

INSERT INTO chats(type, user_a_id, user_b_id, created_by) VALUES
    ('direct', 1, 3, 1);

INSERT INTO messages(chat_id, sender_id, text, sent_at) VALUES
    (1, 1, 'Привет! Глянул новую схему?',      '2026-10-20 10:00:00'),
    (1, 2, 'Да, ручка → сервис → репозитории', '2026-10-20 10:01:00'),
    (1, 1, 'Тогда собираем по ней бэкенд',     '2026-10-20 10:02:00'),
    (2, 3, 'Привет! Скинь макет, пожалуйста',  '2026-10-20 10:05:00'),
    (2, 1, 'Сейчас отправлю',                  '2026-10-20 10:06:00');
