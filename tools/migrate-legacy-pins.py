"""Migrate a database copy and convert numeric Desktop pins using that copy's IDs."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import tempfile
from urllib.parse import urlsplit

MIGRATIONS = Path(__file__).resolve().parents[1] / "backend/Database/Migrations"
ENTITY_MIGRATION = "009_random_entity_ids.sql"
PIN_CHECKPOINT = "DROP TABLE id_messages;"


def pin_filename(address, user_id):
    key = hashlib.sha256(f"{address.rstrip('/')}:{user_id}".encode()).hexdigest().upper()
    return key + ".json"


def address(value):
    result = value.strip().rstrip("/")
    url = urlsplit(result)
    if url.scheme not in ("http", "https") or not url.hostname or url.username is not None or url.query or url.fragment:
        raise argparse.ArgumentTypeError("Specify the full Desktop server URL, including its API prefix.")
    return result


def run_sql(db, sql):
    # executescript commits any active transaction. Keep the export inside 009 instead.
    pending = []
    for line in sql.splitlines(keepends=True):
        pending.append(line)
        if sqlite3.complete_statement("".join(pending)):
            db.execute("".join(pending))
            pending.clear()
    if "".join(pending).strip():
        raise ValueError("Incomplete SQL migration")


def numeric_id(value):
    return type(value) is int and 0 < value <= 2**63 - 1


def export_device_pins(db, source, output, legacy_address, target_address):
    output.mkdir(mode=0o700, parents=True)
    imported = skipped = 0
    matched = False
    for old_user, new_user in db.execute("SELECT old_id, new_id FROM id_users"):
        old_path = source / pin_filename(legacy_address, old_user)
        if not old_path.is_file():
            continue
        matched = True
        entries = json.loads(old_path.read_text(encoding="utf-8-sig"))
        if not isinstance(entries, list):
            raise ValueError(f"Expected a Desktop pins array in {old_path.name}")
        converted = []
        seen = set()
        for pin in entries:
            if not isinstance(pin, dict) or not numeric_id(pin.get("ChatId")) or not numeric_id(pin.get("MessageId")):
                raise ValueError(f"Invalid numeric Desktop pin in {old_path.name}")
            text = pin.get("Text")
            if not isinstance(text, str):
                raise ValueError(f"Invalid Desktop pin text in {old_path.name}")

            mapped = db.execute("""
                SELECT m.chat_id, m.id, m.sender_id, m.sequence
                FROM id_messages old_message
                JOIN messages m ON m.id = old_message.new_id
                JOIN id_chats old_chat ON old_chat.new_id = m.chat_id
                JOIN users_chats uc ON uc.chat_id = m.chat_id AND uc.user_id = ?
                WHERE old_chat.old_id = ? AND old_message.old_id = ? AND m.content_state = 'stored'
                  AND NOT EXISTS (SELECT 1 FROM message_hidden h WHERE h.message_id = m.id AND h.user_id = ?)
                """, (new_user, pin["ChatId"], pin["MessageId"], new_user)).fetchone()
            if mapped is None or mapped[1] in seen:
                skipped += 1
                continue
            chat_id, message_id, sender_id, sequence = mapped
            seen.add(message_id)
            converted.append({
                "ChatId": chat_id, "MessageId": message_id, "Text": text, "SenderId": sender_id,
                "Sequence": sequence,
            })
            if isinstance(pin.get("SentAt"), str):
                converted[-1]["SentAt"] = pin["SentAt"]

        if converted:
            path = output / pin_filename(target_address, new_user)
            with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w", encoding="utf-8") as file:
                json.dump(converted, file, ensure_ascii=False)
        imported += len(converted)
    if not matched:
        raise ValueError(f"No numeric pin files match the legacy URL and users in {source}")
    return imported, skipped


def migrate(source, output, pin_directories, legacy_address, target_address):
    source = source.resolve(strict=True)
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError(f"Output already exists: {output}")
    for directory in pin_directories:
        if not directory.is_dir():
            raise NotADirectoryError(str(directory))
    stage = Path(tempfile.mkdtemp(prefix=f".{output.name}-", dir=output.parent)).resolve()
    try:
        database = stage / "messenger.db"
        with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True)) as original:
            with closing(sqlite3.connect(database)) as copy:
                original.backup(copy)

        counts = []
        with closing(sqlite3.connect(database, isolation_level=None)) as db:
            db.execute("PRAGMA journal_mode = DELETE")
            db.execute("PRAGMA foreign_keys = ON")
            db.execute("PRAGMA temp_store = MEMORY")
            db.execute("CREATE TABLE IF NOT EXISTS schema_migrations(id TEXT NOT NULL PRIMARY KEY)")
            applied = {row[0] for row in db.execute("SELECT id FROM schema_migrations")}
            if ENTITY_MIGRATION in applied:
                raise ValueError("Migration 009 is already applied. Use the database backup from before 009.")

            for path in sorted(MIGRATIONS.glob("*.sql")):
                if path.name in applied:
                    continue
                sql = path.read_text(encoding="utf-8")
                db.execute("BEGIN IMMEDIATE")
                if path.name == ENTITY_MIGRATION:
                    if sql.count(PIN_CHECKPOINT) != 1:
                        raise ValueError("Migration 009 no longer has a unique pin export checkpoint")
                    if db.execute("PRAGMA foreign_key_check").fetchone():
                        raise ValueError("Database has broken references before migration 009")
                    before, after = sql.split(PIN_CHECKPOINT)
                    run_sql(db, before)
                    for i, pins in enumerate(pin_directories, 1):
                        counts.append(export_device_pins(db, pins, stage / "pins" / f"device-{i}",
                                                         legacy_address, target_address))
                    run_sql(db, PIN_CHECKPOINT + after)
                else:
                    run_sql(db, sql)
                db.execute("INSERT INTO schema_migrations(id) VALUES (?)", (path.name,))
                db.execute("COMMIT")

            if not counts:
                raise ValueError("Migration 009 is missing")
            if db.execute("PRAGMA foreign_key_check").fetchone() or db.execute("PRAGMA integrity_check").fetchone() != ("ok",):
                raise ValueError("Migrated database failed integrity checks")

        if output.exists():
            raise FileExistsError(f"Output already exists: {output}")
        stage.rename(output)
        return counts
    finally:
        if stage.exists() and stage.resolve() == stage and stage.parent == output.parent:
            shutil.rmtree(stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", required=True, type=Path, help="SQLite backup from before migration 009")
    parser.add_argument("--pins", required=True, action="append", type=Path, help="Old Desktop pins directory; repeat per device")
    parser.add_argument("--legacy-address", required=True, type=address, help="URL used by the old Desktop client")
    parser.add_argument("--address", type=address, help="Canonical URL used by the updated Desktop client")
    parser.add_argument("--out", required=True, type=Path, help="New directory for the migrated database and converted pins")
    args = parser.parse_args()
    try:
        counts = migrate(args.db, args.out, args.pins, args.legacy_address, args.address or args.legacy_address)
    except (OSError, ValueError, sqlite3.Error) as error:
        parser.exit(1, f"Migration failed: {error}\n")
    for i, (imported, skipped) in enumerate(counts, 1):
        print(f"device-{i}: converted {imported}, skipped {skipped}")


if __name__ == "__main__":
    main()
