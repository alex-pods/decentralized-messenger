## Запуск приложения

Нужен [.NET 8](https://dotnet.microsoft.com/download) или новее.

Из корня репозитория:

```bash
dotnet run --project desktop/MessengerDesktop.csproj
```

В поле «Сервер» вставьте:

```text
https://sftp-ftp-server.duckdns.org/messenger
```

Нажмите «Регистрация» (логин 3–16 символов, пароль от 8) и войдите. Сессия сохранится на этом компьютере.

Архитектура, миграции, шифрование и запуск тестов бэкенда: [docs/backend-architecture.md](docs/backend-architecture.md).

## Развёртывание и тестовые окружения

Для актуального бэкенда используйте [Docker Compose и развёртывание по SSH](tools/deploy/README.md).
БД, вложения и ключи сохраняются в отдельном Docker volume каждого экземпляра.

Исторический [Preview CLI из PR #16](tools/preview/README.md) сохранён для legacy-веток:
systemd/nginx, маршруты `/api/<id>/`, удаление кода при остановке с сохранением данных.
Для текущего `dev` используйте Docker-утилиту из PR #19; обе истории входят в эту ветку.
