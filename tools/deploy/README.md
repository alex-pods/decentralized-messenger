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

`--listen 0.0.0.0` явно публикует порт на всех интерфейсах. `--path-base /api/dev` задаёт префикс. `--project` выбирает совместимый проект форка, публикующий `MessengerBackend.dll`. Нужны настройки `MESSENGER_*` и `/health` с `status: ok` и `revision` из `REVISION`; несовместимый backend-v2 требует адаптера.

Установленный контейнер не перезаписывается. После `remove` та же ревизия использует сохранённый volume. Другая ветка/репозиторий/ревизия над этим volume отклоняются: сначала нужны backup и явная миграция либо другое имя. При ошибке readiness созданный контейнер удаляется, volume сохраняется. `--accept-build` означает разрешение выполнять код ветки на машине сборки; используйте проверенные ветки или отдельный disposable runner. Docker не служит гарантией безопасности враждебного кода.

Проверки: `python -m unittest discover -s tools/deploy -v`. В CI также выполняются полная сборка через Compose, health/Swagger и перезапуск. Результаты live-проверки: [VALIDATION.md](VALIDATION.md).
