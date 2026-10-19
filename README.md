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
