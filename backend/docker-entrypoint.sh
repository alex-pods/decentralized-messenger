#!/bin/sh
set -eu
umask 077

# Current backend accepts a base64 key; keep its raw 32-byte source in the volume.
# Never generate a key in the image or replace an existing state key.
if [ "$APP_DLL" = Messenger.Api.dll ] && [ -z "${MESSENGER_KEY:-}" ]; then
    key_path="${MESSENGER_KEY_FILE:-/state/app.key}"
    if [ ! -e "$key_path" ]; then
        temporary_key=$(mktemp "${key_path}.XXXXXX")
        trap 'rm -f "$temporary_key"' EXIT HUP INT TERM
        head -c 32 /dev/urandom > "$temporary_key"
        # Hard-link creation is atomic and does not overwrite a competing creator.
        ln "$temporary_key" "$key_path" 2>/dev/null || test -f "$key_path"
        rm -f "$temporary_key"
        trap - EXIT HUP INT TERM
    fi
    test ! -L "$key_path"
    test "$(wc -c < "$key_path")" -eq 32
    MESSENGER_KEY=$(base64 < "$key_path" | tr -d '\n')
    export MESSENGER_KEY
fi

exec dotnet "/app/$APP_DLL"
