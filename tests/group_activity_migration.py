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

        def request(path, token=None, body=None, method="GET", expected=200, raw=None, content_type=None):
            headers = {"Authorization": "Bearer " + token} if token else {}
            if body is not None:
                headers["Content-Type"] = "application/json"
            if content_type is not None:
                headers["Content-Type"] = content_type
            data = raw if raw is not None else json.dumps(body).encode() if body is not None else None
            try:
                response = urlopen(Request(base + path, data, headers, method=method), timeout=5)
            except HTTPError as error:
                response = error
            with response:
                assert response.status == expected, (path, response.status, expected)
                content = response.read()
                return json.loads(content) if content and "application/json" in response.headers.get("Content-Type", "") else content or None

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
            original_text = "Encrypted message created before the upgrade"
            text = "Edited encrypted message before the upgrade"
            chat = f"/chats/{group}"
            message = request(chat + "/messages", owner,
                              {"text": original_text, "client_ref": "before-upgrade"}, "POST", 201)["message_id"]
            request(f"/messages/{message}", owner, {"text": text}, "PATCH", 204)
            request(chat + "/messages", owner, {"text": "Reply", "reply_to_id": message}, "POST", 201)
            request(f"/messages/{message}/forward", owner, {"chat_id": group}, "POST", 201)
            request(f"/messages/{message}/reactions", owner, {"emoji": "👍"}, "POST", 204)
            boundary = secrets.token_hex(16)
            payload = b"pre-upgrade attachment"
            multipart = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"check.txt\"\r\n"
                         f"Content-Type: text/plain\r\n\r\n").encode() + payload + f"\r\n--{boundary}--\r\n".encode()
            attachment = request(f"/messages/{message}/attachments", owner, method="POST", expected=201,
                                 raw=multipart, content_type="multipart/form-data; boundary=" + boundary)[0]["id"]
            request(chat + "/pins", owner, {"message_id": message}, "POST", 204)

        with closing(sqlite3.connect(original)) as source, closing(sqlite3.connect(upgraded)) as copy:
            original_migrations = source.execute("SELECT id FROM schema_migrations ORDER BY id").fetchall()
            assert ("009_random_entity_ids.sql",) not in original_migrations
            encrypted_before = source.execute("SELECT text FROM messages WHERE id = ?", (message,)).fetchone()
            assert encrypted_before and encrypted_before[0] != text
            source.backup(copy)

        with running(current, upgraded):
            new_owner = request("/users/me", owner)["id"]
            new_guest = request("/users/me", guest)["id"]
            assert isinstance(new_owner, str) and isinstance(new_guest, str)
            assert new_owner != owner_id and new_guest != guest_id
            new_group = request("/chats", owner)[0]["id"]
            assert isinstance(new_group, str) and new_group != group
            chat = f"/chats/{new_group}"
            history = request(chat + "/messages", owner)
            new_message = history[0]["id"]
            assert new_message.startswith(new_group + ".") and new_message != message
            assert history[0]["text"] == text and history[0]["sender_id"] == new_owner
            assert history[1]["reply_to_id"] == new_message
            assert history[2]["forwarded_from"]["author_id"] == new_owner
            assert history[0]["reactions"][0]["user_ids"] == [new_owner]
            assert history[0]["attachments"][0]["id"] == attachment
            assert request(f"/attachments/{attachment}", owner) == payload
            assert request(chat + "/pins", owner)[0]["message_id"] == new_message
            resend = {"message_id": new_group + "." + secrets.token_hex(16),
                      "text": original_text, "client_ref": "before-upgrade"}
            assert request(chat + "/messages", owner, resend, "POST")["message_id"] == new_message
            request(chat + "/messages", owner, {**resend, "text": "Conflicting retry"}, "POST", 409)
            assert request(chat + "/activity", owner) == []
            request(chat + "/members", owner, {"user_id": new_guest}, "POST", 204)
            assert request(chat + "/activity", guest)[0]["kind"] == "joined"
            request(chat + f"/members/{new_guest}", owner, method="DELETE", expected=204)
            request(chat + "/activity", guest, expected=404)
        with running(current, upgraded):
            events = request(chat + "/activity", owner)
            assert [event["kind"] for event in events] == ["removed", "joined"]
            assert [event["userId"] for event in events] == [new_guest, new_guest]
            assert request(chat + "/messages", owner)[0]["text"] == text
            assert request(chat + "/messages", owner, resend, "POST")["message_id"] == new_message

        with closing(sqlite3.connect(upgraded)) as db:
            assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
            assert db.execute("PRAGMA foreign_key_check").fetchall() == []
            assert db.execute("SELECT text FROM messages WHERE id = ?", (new_message,)).fetchone() == encrypted_before
            expected = set(original_migrations) | {("006_antispam.sql",), ("007_group_activity.sql",),
                                                  ("008_message_sequence.sql",), ("009_random_entity_ids.sql",)}
            assert set(db.execute("SELECT id FROM schema_migrations").fetchall()) == expected
        print("PASS upgrade: sessions, encrypted history, replies, forwards, reactions, attachments, pins, original client_ref and restart")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True, help="Previous Messenger.Api.dll with its build output")
    parser.add_argument("--current", type=Path, default=Path("backend/bin/Release/net8.0/Messenger.Api.dll"))
    args = parser.parse_args()
    check_upgrade(args.baseline.resolve(strict=True), args.current.resolve(strict=True))
