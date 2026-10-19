#!/usr/bin/env bash
# Framework-dependent linux-x64 publish must already be uploaded to a new release directory.
# Usage (root): bash upgrade.sh /opt/messenger/releases/<revision> <full-git-revision>
set -Eeuo pipefail
release=$(realpath "$1")
revision=$2
[[ "$release" == /opt/messenger/releases/* && -f "$release/MessengerBackend.dll" ]]
[[ "$revision" =~ ^[0-9a-f]{40}$ ]]
[[ $(id -u) == 0 ]]
stamp=$(date -u +%Y%m%dT%H%M%SZ)
backup="/var/backups/messenger/$stamp"
install -d -m 700 "$backup"
printf '%s\n' "$revision" > "$backup/target-revision"
cp -a /etc/systemd/system/messenger-backend.service "$backup/service"
if [[ -d /etc/systemd/system/messenger-backend.service.d ]]; then
    cp -a /etc/systemd/system/messenger-backend.service.d "$backup/service.d"
fi
cp -aL /opt/messenger/backend "$backup/backend"
trap 'systemctl start messenger-backend' ERR
systemctl stop messenger-backend
cp -a /var/lib/messenger "$backup/data"
if [[ -d /var/lib/messenger-keys ]]; then cp -a /var/lib/messenger-keys "$backup/keys"; fi

rollback() {
    trap - ERR
    systemctl stop messenger-backend || true
    if [[ -L /opt/messenger/backend ]]; then unlink /opt/messenger/backend; fi
    if [[ -d "/opt/messenger/backend.before-$stamp" ]]; then
        mv "/opt/messenger/backend.before-$stamp" /opt/messenger/backend
    fi
    mv /var/lib/messenger "/var/lib/messenger.failed-$stamp"
    cp -a "$backup/data" /var/lib/messenger
    if [[ -f "$backup/service.d/storage-security.conf" ]]; then
        cp -a "$backup/service.d/storage-security.conf" /etc/systemd/system/messenger-backend.service.d/storage-security.conf
    else
        rm -f /etc/systemd/system/messenger-backend.service.d/storage-security.conf
    fi
    systemctl daemon-reload
    systemctl start messenger-backend
    echo "Upgrade failed; previous application/data restored. Backup: $backup" >&2
    exit 1
}
trap rollback ERR

install -d -m 700 -o messenger -g messenger /var/lib/messenger-keys
install -d /etc/systemd/system/messenger-backend.service.d
cat > /etc/systemd/system/messenger-backend.service.d/storage-security.conf <<'EOF'
[Service]
Environment=MESSENGER_KEYS=/var/lib/messenger-keys
ReadWritePaths=/var/lib/messenger-keys
UMask=0077
EOF
printf '%s\n' "$revision" > "$release/REVISION"
mv /opt/messenger/backend "/opt/messenger/backend.before-$stamp"
ln -s "$release" /opt/messenger/backend
systemctl daemon-reload
systemctl start messenger-backend
healthy=false
for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8090/messenger/health | python3 -c 'import json,sys; assert json.load(sys.stdin)["version"] == "4.1.0"' 2>/dev/null; then
        healthy=true; break
    fi
    sleep 1
done
[[ "$healthy" == true ]]
install -d -m 700 /var/backups/messenger-keys
cp -a /var/lib/messenger-keys "/var/backups/messenger-keys/$stamp"
trap - ERR
echo "Deployed $revision; backup: $backup"
