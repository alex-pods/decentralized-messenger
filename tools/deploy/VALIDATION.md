# Проверки Docker deployment — 2026-10-31

Тестовый сервер: Debian 11 x64, Docker 28.3.3/Buildx 0.26.1, Python 3.9.2. Клиент: Windows, Python 3.12, .NET SDK 10.0.300. Ветка приложения `dev`, commit `1ecf87726851642c8c0678409963a7068355c419`.

- SSH с явным ключом и SSH config; локальная публикация, передача и Docker build через стадию `prebuilt`. SDK и системный .NET на сервере не использовались; systemd units не создавались.
- Health и Swagger с `/api/deploy-test`; существующий десктопный self-check проверил сообщения, direct/group чаты, client_ref, редактирование, пагинацию, удаление и WebSocket через SSH-туннель.
- После stop/remove и повторного deploy той же ревизии сохранились пользователь, сессия и зашифрованный профиль в Docker volume.
- После проверки выполнен `remove --purge-data`, проверено отсутствие тестовых контейнера, image, volume, build context и слушающего порта. Кеш этой проверки очищается адресно по его IDs; чужие образы, кеши, сервисы и базы не очищаются.
- CI проверяет Python CLI на Windows/Linux и обычную полную сборку Docker Compose из исходников, readiness, Swagger и restart с сохранённым volume.

Парольный вход, sudo и arm64 в live-прогоне не проверялись. Автоматическая миграция между разными commit не реализована.
