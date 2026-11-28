"""Convert numeric pins with real IDs from migration 009 on a read-only SQLite backup."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import runpy
import sqlite3
import tempfile

TOOL = runpy.run_path(str(Path(__file__).resolve().parents[1] / "tools/migrate-legacy-pins.py"))
LEGACY = "https://old.example.invalid/api/dev"
CURRENT = "https://current.example.invalid/api/dev"


def pin_name(url, user):
    return hashlib.sha256(f"{url}:{user}".encode("utf-8")).hexdigest().upper() + ".json"


def run(source, work):
    first, second = work / "first", work / "second"
    first.mkdir()
    second.mkdir()
    output = work / "converted"
    with closing(sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True)) as db:
        user, tag, chat, message, sent_at = db.execute("""
            SELECT u.id, u.tag, m.chat_id, m.id, m.sent_at FROM messages m
            JOIN users_chats uc ON uc.chat_id=m.chat_id
            JOIN users u ON u.id=uc.user_id
            WHERE m.content_state='stored'
              AND NOT EXISTS (SELECT 1 FROM message_hidden h WHERE h.user_id=u.id AND h.message_id=m.id)
            LIMIT 1
            """).fetchone()
        outsider = db.execute("SELECT id FROM users WHERE id NOT IN (SELECT user_id FROM users_chats WHERE chat_id=?) LIMIT 1", (chat,)).fetchone()[0]
        total = db.execute("SELECT COUNT(*) FROM messages").fetchone()[0]
    original_digest = hashlib.sha256(source.read_bytes()).digest()
    pin = {"ChatId": chat, "MessageId": message, "Text": "fixture 1", "SentAt": sent_at.replace(" ", "T") + "Z"}
    (first / pin_name(LEGACY, user)).write_text(json.dumps([pin, pin, {**pin, "ChatId": chat + 1}]), encoding="utf-8")
    (first / pin_name(LEGACY, outsider)).write_text(json.dumps([pin]), encoding="utf-8")
    (second / pin_name(LEGACY, user)).write_text(json.dumps([{**pin, "Text": "fixture 2"}]), encoding="utf-8")
    inputs = {path: path.read_bytes() for device in (first, second) for path in device.glob("*.json")}

    assert TOOL["migrate"](source, output, [first, second], LEGACY, CURRENT) == [(1, 3), (1, 0)]
    with closing(sqlite3.connect(output / "messenger.db")) as db:
        new_user = db.execute("SELECT id FROM users WHERE tag=?", (tag,)).fetchone()[0]
        assert db.execute("SELECT COUNT(*) FROM messages").fetchone()[0] == total
        assert not db.execute("PRAGMA foreign_key_check").fetchall()
        assert not db.execute("SELECT 1 FROM sqlite_master WHERE name IN ('id_users','id_chats','id_messages')").fetchall()
        assert db.execute("SELECT 1 FROM schema_migrations WHERE id='010_relay_receipts.sql'").fetchone()
        for i in (1, 2):
            directory = output / "pins" / f"device-{i}"
            files = list(directory.glob("*.json"))
            assert [path.name for path in files] == [pin_name(CURRENT, new_user)]
            item, = json.loads(files[0].read_text(encoding="utf-8"))
            assert item["Text"] == f"fixture {i}"
            assert item["MessageId"].startswith(item["ChatId"] + ".")
            assert db.execute("""
                SELECT 1 FROM messages m
                JOIN users_chats uc ON uc.chat_id=m.chat_id AND uc.user_id=?
                WHERE m.id=? AND m.chat_id=? AND m.sender_id=? AND m.sequence=? AND m.sent_at=?
                """, (new_user, item["MessageId"], item["ChatId"], item["SenderId"], item["Sequence"], sent_at)).fetchone()

    assert original_digest == hashlib.sha256(source.read_bytes()).digest()
    assert all(path.read_bytes() == content for path, content in inputs.items())
    try:
        TOOL["migrate"](output / "messenger.db", work / "rerun", [first], LEGACY, CURRENT)
    except ValueError as error:
        assert "already applied" in str(error)
    else:
        raise AssertionError("Converted DB accepted as an input")
    assert not (work / "rerun").exists()

    (first / pin_name(LEGACY, user)).write_text("{broken", encoding="utf-8")
    try:
        TOOL["migrate"](source, work / "bad", [first], LEGACY, CURRENT)
    except ValueError:
        assert not (work / "bad").exists()
    else:
        raise AssertionError("Corrupt client pins accepted")

    print(f"PASS numeric personal pins: two devices, one verified ID mapping, denied/duplicate pins skipped, {total} messages preserved, original files intact")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--migration-source", required=True, type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="messenger-legacy-pins-check-") as temp:
        run(args.migration_source, Path(temp))
