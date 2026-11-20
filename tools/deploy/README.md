# Бэкенд в Docker

Из корня репозитория:

```sh
docker compose up --build -d
# http://127.0.0.1:18090/swagger
docker compose logs -f
docker compose down
```

Docker сам собирает .NET-проект через multi-stage [backend/Dockerfile](../../backend/Dockerfile). SDK на хосте не нужен. БД, вложения и ключи шифрования хранятся вместе в named volume `messenger-data`. `down` сохраняет данные, `down -v` удаляет их **необратимо**. Compose из разных каталогов/project name создаёт разные volumes.

Настройки: `PORT` (18090), `BIND_ADDRESS` (127.0.0.1), `MESSENGER_PATH_BASE` (пустой), `REVISION` (unknown). Для точного commit перед сборкой можно задать `REVISION=$(git rev-parse HEAD)`; в PowerShell: `$env:REVISION = git rev-parse HEAD`.

Контейнер работает от UID 1654, с read-only корневой ФС, без capabilities и с лимитами RAM/процессов. Для внешнего доступа используйте HTTPS reverse proxy, сохраняющий `MESSENGER_PATH_BASE`. `BIND_ADDRESS=0.0.0.0` открывает порт на сетевых интерфейсах; TLS/firewall Compose не настраивает. Перед заменой версии снимайте резервную копию volume: автоматического отката миграций нет.

## Развёртывание выбранной ветки по SSH

```sh
python tools/deploy/deploy.py --host root@your-server --key ~/.ssh/backend_key deploy --repo https://github.com/alex-pods/decentralized-messenger.git --branch dev --name my-dev --port 18090 --accept-build
```

CLI скачивает выбранную ветку локально, фиксирует commit, публикует `MessengerBackend.dll` и передаёт файлы по SSH. Сервер собирает образ **тем же Dockerfile**, выбрав стадию `prebuilt`: серверный SDK и исходники не нужны. Это также позволяет разворачивать старые совместимые ветки, в которых ещё нет Dockerfile. Временные checkout, publish и серверный build context удаляются; приложение остаётся только в Docker image.

Требования: локально Python 3.10+, Git, .NET 8 SDK или новее, `ssh`/`scp`; на Linux-сервере Python 3.9+, Docker Engine с Buildx/BuildKit, root SSH либо `--sudo` с passwordless sudo. Поддерживаются Linux x64/arm64, на машине оператора — Windows/Linux. Docker на машине оператора не нужен. Серверу требуется место для runtime image, build cache и данных; формальная проверка 256 МиБ временного места не заменяет планирование диска.

Без `--key` используются SSH config/agent. `--ssh-port 2222` меняет порт SSH; без него действует SSH config. `--ask-password` разрешает стандартный запрос пароля/passphrase самим OpenSSH в терминале. Пароль не передаётся аргументом и не сохраняется. Fingerprint сервера нужно проверить заранее обычным `ssh`: утилита не отключает host-key checking. Git использует свою локальную авторизацию; ключ сервера не копируется в Git-контейнер или образ.

```sh
python tools/deploy/deploy.py branches --repo https://github.com/alex-pods/decentralized-messenger.git
python tools/deploy/deploy.py --host root@your-server status --name my-dev
python tools/deploy/deploy.py --host root@your-server logs --name my-dev
python tools/deploy/deploy.py --host root@your-server stop --name my-dev
python tools/deploy/deploy.py --host root@your-server start --name my-dev
python tools/deploy/deploy.py --host root@your-server remove --name my-dev
python tools/deploy/deploy.py --host root@your-server remove --name my-dev --purge-data
```

CLI создаёт контейнер `messenger-<name>`, образ `messenger-deploy/<name>:<commit>` и том `messenger-<name>-data`. Docker восстанавливает работающий контейнер после перезагрузки (`unless-stopped`). `stop` сохраняет контейнер/образ/данные; `remove` удаляет контейнер и образы экземпляра, сохраняя volume. `--purge-data` дополнительно удаляет volume. Чужие контейнеры/volumes/images без метки утилиты не изменяются. Общий Docker build cache автоматически не очищается.

Без `--name` имя зависит от репозитория и ветки. Для нескольких экземпляров указывайте разные порты. API по умолчанию слушает только loopback сервера; для доступа с рабочей машины:

```sh
ssh -N -L 18090:127.0.0.1:18090 root@your-server
```

`--listen 0.0.0.0` явно публикует порт на всех интерфейсах. `--path-base /api/dev` задаёт префикс. По умолчанию выбран профиль `legacy`: `--project` выбирает совместимый проект форка, публикующий `MessengerBackend.dll`; нужны настройки `MESSENGER_*` и `/health` с `status: ok` и `revision` из `REVISION`.

### Backend v2

```sh
python tools/deploy/deploy.py --host root@your-server deploy --branch feature-backend-v2-websocket --profile v2 --name backend-v2-websocket --port 18120 --path-base /api/feature-backend-v2-websocket --accept-build
```

Профиль `v2` собирает `backend-v2/Messenger.Api.csproj` под `linux-musl-x64/arm64`, использует меньший официальный ASP.NET Alpine image, запускает `Messenger.Api.dll` и передаёт `Db=/state/messenger.db`. Для Alpine включён invariant globalization. `MESSENGER_FILES` и path base сохраняются. Из publish исключается **только** `Database/Migrations/002_demo_data.sql`: пустая серверная БД не заполняется демонстрационными аккаунтами/сообщениями. Структурные миграции 001/003 выполняются самим приложением.

В этой ветке `/health` не возвращает revision: readiness проверяет `status=ok` и `service=messenger-backend-v2`, а происхождение сборки подтверждают проверенные digest/REVISION в архиве и source label контейнера/тома. Legacy по-прежнему требует точный revision в health. Профили имеют разные source identity; БД legacy в v2 автоматически не переносится. Swagger и шифрование содержимого на диске в проверенной ветке v2 отсутствуют.

Опциональный HTTP/WebSocket smoke-тест **создаёт случайные тестовые аккаунты**: запускайте его только на тестовой БД. Локальный режим сам создаёт/удаляет временную БД и проверяет повторный запуск:

```sh
python -m pip install websockets==13.1
dotnet build /path/to/v2-checkout/backend-v2/Messenger.Api.csproj -c Release
python tools/deploy/smoke_v2.py --local-output /path/to/v2-checkout/backend-v2/bin/Release/net8.0
python tools/deploy/smoke_v2.py --base-url https://your-server/api/feature-backend-v2-websocket
```

Установленный контейнер не перезаписывается. После `remove` та же ревизия использует сохранённый volume. Другая ветка/репозиторий/ревизия над этим volume отклоняются: сначала нужны backup и явная миграция либо другое имя. При ошибке readiness созданный контейнер удаляется, volume сохраняется. `--accept-build` означает разрешение выполнять код ветки на машине сборки; используйте проверенные ветки или отдельный disposable runner. Docker не служит гарантией безопасности враждебного кода.

### Импорт другой SQLite-базы

Сначала разверните целевой экземпляр: приложение должно создать свою схему. Затем можно проверить другую БД **на том же SSH-сервере**:

```sh
python tools/deploy/deploy.py --host root@your-server migrate --name my-dev --from-db /var/lib/source/messenger.db
```

По умолчанию это только проверка, без остановки контейнера и записи данных. Ответ содержит `compatible`, причины отказа и количества записей. Проверяются определения таблиц/индексов/триггеров/views, `user_version`, применённые миграции, SQLite integrity/foreign keys и файлы. Слияния записей нет: целевая БД должна быть пустой (служебные метаданные не считаются пользовательскими данными).

Если проверка успешна, остановите **источник** штатной командой его сервиса, затем выполните:

```sh
python tools/deploy/deploy.py --host root@your-server migrate --name my-dev --from-db /var/lib/source/messenger.db --apply --source-stopped
```

`--source-stopped` — утверждение оператора, что писатель источника действительно остановлен; утилита сама его не останавливает. Это необходимо для согласованного состояния БД и файлов. Рядом с исходной БД автоматически ищутся `files/` и `keys/`; другие абсолютные пути задаются `--from-files /path/files --from-keys /path/keys`. Зашифрованная БД без исходных DataProtection keys отклоняется. Совместимость самих ключей окончательно проверяет приложение при readiness; ошибочные ключи вызывают откат. Файлы, ключи и SQL-состояние копируются без расшифровки. Символические ссылки/специальные файлы внутри состояния не поддерживаются.

Утилита создаёт согласованный снимок через SQLite backup API (учитывает WAL), кратко останавливает цель, повторно проверяет её пустоту/схему и сохраняет прежнее состояние в root-only `/var/backups/messenger-deploy/<name>/<timestamp>/`. После копирования запускает контейнер и проверяет readiness/целостность. При ошибке восстанавливает предыдущее состояние. Исходная БД и её файлы не изменяются. После импорта у экземпляров независимые копии данных; последующие сообщения между ними не синхронизируются. Резервные копии автоматически не удаляются; при сбое питания/недоступности Docker восстановление может потребовать администратора.

**Legacy `dev` → текущая v2 не совместимы:** различаются схема, миграции и формат шифрования. Команда откажет до остановки контейнера. Для такого перехода нужен отдельный явно описанный и протестированный конвертер; автоматического снятия шифрования/угадывания соответствий полей нет.

Проверки: `python -m unittest discover -s tools/deploy -v`. В CI также выполняются полная сборка через Compose, health/Swagger и перезапуск. Результаты live-проверки: [VALIDATION.md](VALIDATION.md).
