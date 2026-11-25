"""Upgrade a disposable database created by an older Messenger.Api build."""
import argparse
import base64
from contextlib import closing, contextmanager
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def check_upgrade(previous, current):
    with tempfile.TemporaryDirectory(prefix="messenger-upgrade-") as directory:
        state = Path(directory)
        original = state / "original.db"
        upgraded = state / "upgraded.db"
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            address = f"http://127.0.0.1:{listener.getsockname()[1]}"
        base = address + "/messenger"
        env = {**os.environ, "ASPNETCORE_URLS": address, "MESSENGER_PATH_BASE": "/messenger",
               "MESSENGER_FILES": str(state / "files"), "MESSENGER_DEV_SEED": "false",
               "MESSENGER_ANTISPAM": "false",
               "MESSENGER_KEY": base64.b64encode(secrets.token_bytes(32)).decode()}

        def request(path, token=None, body=None, method="GET", expected=200):
            headers = {"Authorization": "Bearer " + token} if token else {}
            if body is not None:
                headers["Content-Type"] = "application/json"
            data = json.dumps(body).encode() if body is not None else None
            try:
                response = urlopen(Request(base + path, data, headers, method=method), timeout=5)
            except HTTPError as error:
                response = error
            with response:
                assert response.status == expected, (path, response.status, expected)
                content = response.read()
                return json.loads(content) if content else None

        @contextmanager
        def running(assembly, db):
            with tempfile.TemporaryFile() as log:
                process = subprocess.Popen(["dotnet", str(assembly)], cwd=state, env={**env, "Db": str(db)},
                                           stdout=log, stderr=log,
                                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                try:
                    for _ in range(100):
                        if process.poll() is not None:
                            raise RuntimeError("Backend exited before readiness")
                        try:
                            if request("/health")["status"] == "ok":
                                break
                        except URLError:
                            time.sleep(.1)
                    else:
                        raise RuntimeError("Backend did not become ready")
                    yield
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=8)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()

        with running(previous, original):
            accounts = []
            for name in ("fixtureowner", "fixtureguest"):
                auth = {"login": name, "password": secrets.token_urlsafe(20)}
                request("/auth/register", body=auth, method="POST", expected=201)
                token = request("/auth/login", body=auth, method="POST")["user_session_token"]
                accounts.append((token, request("/users/me", token)["id"]))
            owner, owner_id = accounts[0]
            guest, guest_id = accounts[1]
            group = request("/chats/group", owner, {"title": "Existing group", "member_ids": []},
                            "POST", 201)["chat_id"]
            text = "Encrypted message created before the upgrade"
            chat = f"/chats/{group}"
            message = request(chat + "/messages", owner, {"text": text}, "POST", 201)["message_id"]
            request(chat + "/pins", owner, {"message_id": message}, "POST", 204)

        with closing(sqlite3.connect(original)) as source, closing(sqlite3.connect(upgraded)) as copy:
            original_migrations = source.execute("SELECT id FROM schema_migrations ORDER BY id").fetchall()
            assert ("007_group_activity.sql",) not in original_migrations
            encrypted_before = source.execute("SELECT text FROM messages WHERE id = ?", (message,)).fetchone()
            assert encrypted_before and encrypted_before[0] != text
            source.backup(copy)

        with running(current, upgraded):
            assert request("/users/me", owner)["id"] == owner_id
            assert request(chat + "/messages", owner)[0]["text"] == text
            assert request(chat + "/pins", owner)[0]["message_id"] == message
            assert request(chat + "/activity", owner) == []
            request(chat + "/members", owner, {"user_id": guest_id}, "POST", 204)
            assert request(chat + "/activity", guest)[0]["kind"] == "joined"
            request(chat + f"/members/{guest_id}", owner, method="DELETE", expected=204)
            request(chat + "/activity", guest, expected=404)
        with running(current, upgraded):
            events = request(chat + "/activity", owner)
            assert [event["kind"] for event in events] == ["removed", "joined"]
            assert request(chat + "/messages", owner)[0]["text"] == text

        with closing(sqlite3.connect(upgraded)) as db:
            assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
            assert db.execute("PRAGMA foreign_key_check").fetchall() == []
            assert db.execute("SELECT text FROM messages WHERE id = ?", (message,)).fetchone() == encrypted_before
            assert db.execute("SELECT id FROM schema_migrations ORDER BY id").fetchall() == [
                *original_migrations, ("007_group_activity.sql",)]
        print("PASS upgrade: pre-existing session, unchanged ciphertext, history and pin, new activity after restart, SQLite integrity")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True, help="Previous Messenger.Api.dll with its build output")
    parser.add_argument("--current", type=Path, default=Path("backend/bin/Release/net8.0/Messenger.Api.dll"))
    args = parser.parse_args()
    check_upgrade(args.baseline.resolve(strict=True), args.current.resolve(strict=True))
