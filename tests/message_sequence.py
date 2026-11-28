"""Isolated migration, HTTP and native desktop checks for per-chat message order."""
import argparse
import base64
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def check_migration(repo, source, destination):
    expected_links = {
        'users': {'id': 'u'}, 'sessions': {'user_id': 'u'}, 'avatars': {'user_id': 'u'},
        'chats': {'id': 'c', 'user_a_id': 'u', 'user_b_id': 'u', 'created_by': 'u'},
        'users_chats': {'user_id': 'u', 'chat_id': 'c', 'last_read_message_id': 'm'},
        'messages': {'id': 'm', 'chat_id': 'c', 'sender_id': 'u', 'reply_to_id': 'm'},
        'message_hidden': {'user_id': 'u', 'message_id': 'm'}, 'attachments': {'message_id': 'm'},
        'message_reactions': {'message_id': 'm', 'user_id': 'u'},
        'message_forwards': {'message_id': 'm', 'author_id': 'u'},
        'pinned_messages': {'chat_id': 'c', 'message_id': 'm', 'pinned_by': 'u'},
        'shared_themes': {'owner_id': 'u'}, 'antispam_blocks': {'user_id': 'u'},
        'antispam_attempts': {'user_id': 'u'}, 'group_activity': {'chat_id': 'c', 'user_id': 'u'},
    }
    migrations = repo / 'backend/Database/Migrations'
    with closing(sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True)) as original:
        with closing(sqlite3.connect(destination)) as copy, copy:
            original.backup(copy)
    with closing(sqlite3.connect(destination)) as db, db:
        db.execute('PRAGMA foreign_keys=ON')
        if 'sequence' not in [c[1] for c in db.execute('PRAGMA table_info(messages)')]:
            sql = (migrations / '008_message_sequence.sql').read_text(encoding='utf-8')
            db.executescript('BEGIN IMMEDIATE;\n' + sql + '\nCOMMIT;')
        assert not db.execute('PRAGMA foreign_key_check').fetchall()
        counters = dict(db.execute("SELECT name, seq FROM sqlite_sequence WHERE name IN ('sessions', 'attachments')"))
        before = {}
        for table in expected_links:
            columns = [c[1] for c in db.execute(f'PRAGMA table_info("{table}")')]
            before[table] = columns, db.execute(f'SELECT * FROM "{table}"').fetchall()

        # Capture the temporary mapping on this test connection so every FK and every
        # uninterpreted ciphertext byte can be compared. It is absent from the migrated DB.
        sql = (migrations / '009_random_entity_ids.sql').read_text(encoding='utf-8')
        snapshots = '\n'.join(f'CREATE TEMP TABLE audit_{name} AS SELECT * FROM id_{name};'
                              for name in ('users', 'chats', 'messages'))
        sql = sql.replace('DROP TABLE id_messages;', snapshots + '\nDROP TABLE id_messages;', 1)
        db.executescript('BEGIN IMMEDIATE;\n' + sql + '\nCOMMIT;')
        maps = {k: dict(db.execute(f'SELECT * FROM audit_{name}'))
                for k, name in zip('ucm', ('users', 'chats', 'messages'))}

        for table, (columns, rows) in before.items():
            selection = ','.join(f'"{c}"' for c in columns)
            actual = db.execute(f'SELECT {selection} FROM "{table}"').fetchall()
            expected = []
            for row in rows:
                data = dict(zip(columns, row))
                for column, kind in expected_links[table].items():
                    data[column] = maps[kind].get(data[column])
                if table == 'chats' and data['type'] == 'direct':
                    data['user_a_id'], data['user_b_id'] = sorted((data['user_a_id'], data['user_b_id']))
                if table == 'messages' and data['client_ref_payload'] is not None:
                    old = dict(zip(columns, row))
                    data['client_ref_payload'] = f"legacy:{old['chat_id']}|{old['reply_to_id'] or 0}|{old['client_ref_payload']}"
                if table == 'antispam_attempts' and data['attempt_key'].startswith('send:'):
                    _, old_chat, ref = data['attempt_key'].split(':', 2)
                    if old_chat.isdecimal() and int(old_chat) in maps['c']:
                        data['attempt_key'] = f"send:{maps['c'][int(old_chat)]}:{ref}"
                expected.append(tuple(data[c] for c in columns))
            assert Counter(actual) == Counter(expected), table
        assert not db.execute('PRAGMA foreign_key_check').fetchall()
        assert db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        assert dict(db.execute("SELECT name, seq FROM sqlite_sequence WHERE name IN ('sessions', 'attachments')")) == counters
        assert not db.execute("SELECT name FROM sqlite_master WHERE name LIKE 'old_%'").fetchall()
        assert not db.execute("SELECT 1 FROM messages WHERE substr(id,1,49) <> chat_id || '.' OR sequence IS NULL").fetchall()
        relay = (migrations / '010_relay_receipts.sql').read_text(encoding='utf-8')
        db.executescript('BEGIN IMMEDIATE;\n' + relay + '\nCOMMIT;')
        assert not db.execute('SELECT 1 FROM relay_receipts').fetchall()
        assert not db.execute('PRAGMA foreign_key_check').fetchall()
        assert db.execute('SELECT COUNT(*) FROM messages').fetchone()[0] == len(maps['m'])
        print(f"PASS database copy: {len(maps['u'])} users, {len(maps['c'])} chats, {len(maps['m'])} messages, all rows, references and ciphertext verified", flush=True)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--migration-source', type=Path)
    parser.add_argument('--ui', action='store_true')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix='messenger-sequence-') as directory:
        temp = Path(directory)
        if args.migration_source:
            check_migration(repo, args.migration_source, temp / 'migration-copy.db')
        for project in ['backend', 'desktop']:
            subprocess.run(['dotnet', 'build', project, '-c', 'Release', '--nologo', '-v', 'quiet',
                            '-o', str(temp / project)], cwd=repo, check=True)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        address = f'http://127.0.0.1:{port}'
        base = address + '/messenger'
        database = temp / 'check.db'
        env = {**os.environ, 'ASPNETCORE_URLS': address, 'Db': str(database),
               'MESSENGER_FILES': str(temp / 'files'), 'MESSENGER_DEV_SEED': 'false',
               'MESSENGER_ANTISPAM': 'false', 'MESSENGER_KEY': base64.b64encode(secrets.token_bytes(32)).decode(),
               'Logging__LogLevel__Default': 'Warning'}
        with (temp / 'runtime.log').open('w+', encoding='utf-8') as log:
            server = subprocess.Popen(['dotnet', str(temp / 'backend/Messenger.Api.dll')], cwd=temp, env=env,
                                      stdout=log, stderr=log,
                                      creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            def request(path, token=None, body=None, method=None):
                headers = {'Content-Type': 'application/json'}
                if token:
                    headers['Authorization'] = 'Bearer ' + token
                data = json.dumps(body).encode() if body is not None else None
                req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
                with urllib.request.urlopen(req, timeout=20) as response:
                    data = response.read()
                    return json.loads(data) if data else None
            def error(status, path, token, body=None, method=None):
                try:
                    request(path, token, body, method)
                except urllib.error.HTTPError as exception:
                    assert exception.code == status, (path, exception.code)
                    return json.loads(exception.read())
                else:
                    raise AssertionError('Expected error: ' + path)
            try:
                for _ in range(100):
                    try:
                        request('/health')
                        break
                    except OSError:
                        time.sleep(.1)
                else:
                    raise RuntimeError('Temporary backend not ready')
                def user(login):
                    credentials = {'login': login, 'password': 'sequence-check-password'}
                    request('/auth/register', body=credentials)
                    token = request('/auth/login', body=credentials)['user_session_token']
                    return token, request('/users/me', token)['id']
                alice, alice_id = user('seqalice')
                bob, bob_id = user('seqbob')
                assert all(isinstance(user_id, str) and len(user_id) == 32 for user_id in (alice_id, bob_id))
                candidate = secrets.token_hex(24)
                direct = request('/chats', alice, {'another_user_id': bob_id, 'chat_id': candidate})['chat_id']
                assert direct == candidate
                assert request('/chats', bob, {'another_user_id': alice_id, 'chat_id': secrets.token_hex(24)})['chat_id'] == direct
                error(400, '/chats', alice, {'another_user_id': bob_id, 'chat_id': 'invalid'})
                outsider, _ = user('seqoutsider')
                missing_chat = secrets.token_hex(24)
                for route in ('', '/messages', '/members', '/pins'):
                    absent = error(404, f'/chats/{missing_chat}{route}', outsider)
                    forbidden = error(404, f'/chats/{direct}{route}', outsider)
                    assert (absent['code'], absent['error']) == (forbidden['code'], forbidden['error'])
                other = request('/chats/group', alice, {'title': 'Other chat', 'member_ids': [bob_id], 'chat_id': secrets.token_hex(24)})['chat_id']
                error(409, '/chats/group', alice, {'title': 'Duplicate', 'chat_id': other})
                def message_id(chat, suffix=None):
                    return chat + '.' + (suffix or secrets.token_hex(16))
                def send(chat, text, ref=None, suffix=None):
                    body = {'text': text, 'message_id': message_id(chat, suffix)}
                    if ref:
                        body['client_ref'] = ref
                    return request(f'/chats/{chat}/messages', alice, body)['message_id']
                ids = []
                for n in range(6):
                    ids.append(send(direct, f'ordered {n}', 'first' if n == 0 else None, f'{100-n:032x}'))
                    send(other, f'interleaved {n}')
                assert send(direct, 'ordered 0', 'first') == ids[0]
                error(409, f'/chats/{direct}/messages', alice, {'text': 'different', 'client_ref': 'first', 'message_id': message_id(direct)})
                history = request(f'/chats/{direct}/messages', bob)
                assert [m['sequence'] for m in history] == list(range(1, 7))
                assert [m['id'] for m in history] == ids
                assert ids[0] > ids[1]
                error(400, f'/chats/{direct}/messages', alice, {'text':'bad prefix', 'message_id':message_id(other)})
                error(409, f'/chats/{direct}/messages', alice, {'text':'duplicate ID', 'message_id':ids[0]})
                assert request(f'/chats/{other}/messages', bob)[0]['sequence'] == 1
                with ThreadPoolExecutor(max_workers=8) as pool:
                    list(pool.map(lambda n: send(direct, f'concurrent {n}'), range(16)))
                history = request(f'/chats/{direct}/messages', bob)
                assert [m['sequence'] for m in history] == list(range(1, 23))
                with ThreadPoolExecutor(max_workers=8) as pool:
                    duplicate_ids = list(pool.map(lambda _: send(direct, 'retry', 'concurrent-retry'), range(8)))
                assert len(set(duplicate_ids)) == 1
                assert request(f'/chats/{direct}/messages', bob)[-1]['sequence'] == 23
                print('PASS sequence allocation: per-chat order, concurrent sends and idempotent retries', flush=True)

                history = request(f'/chats/{direct}/messages?after_sequence=0', bob)
                assert [m['sequence'] for m in history] == list(range(1, 24))
                third = history[2]['id']
                assert [m['sequence'] for m in request(f'/chats/{direct}/messages?before_sequence=4&limit=2', bob)] == [2, 3]
                assert [m['sequence'] for m in request(f'/chats/{direct}/messages?after_sequence=3&before_sequence=6', bob)] == [4, 5]
                assert [m['sequence'] for m in request(f'/chats/{direct}/messages?after_id={third}&limit=2', bob)] == [4, 5]
                assert [m['sequence'] for m in request(f'/chats/{direct}/messages?before_id={third}&limit=2', bob)] == [1, 2]
                other_id = request(f'/chats/{other}/messages', bob)[0]['id']
                error(400, f'/chats/{direct}/messages?before_id={other_id}', bob)
                error(400, f'/chats/{direct}/messages?before_id={third}&after_sequence=0', bob)
                error(400, f'/chats/{direct}/messages?after_sequence=-1', bob)
                chats = request('/chats', bob)
                assert next(c for c in chats if c['id'] == direct)['last_message']['sequence'] == 23
                print('PASS history: scrambled IDs, new and legacy cursors, validation and latest message', flush=True)

                request(f'/chats/{direct}/read', bob, {'message_id': history[4]['id']})
                request(f'/chats/{direct}/read', bob, {'message_id': history[1]['id']})
                read = request(f'/chats/{direct}/messages', alice)
                assert all(m['read_at'] is not None for m in read[:5])
                assert all(m['read_at'] is None for m in read[5:])
                request(f'/chats/{direct}/pins', alice, {'message_id': history[1]['id']})
                request(f'/chats/{direct}/pins', alice, {'message_id': history[4]['id']})
                assert [p['message']['sequence'] for p in request(f'/chats/{direct}/pins', bob)] == [5, 2]
                reply = request(f'/chats/{direct}/messages', alice, {'text': 'Reply', 'reply_to_id': third, 'message_id': message_id(direct)})['message_id']
                request(f'/messages/{third}', alice, {'text': 'Edited'}, 'PATCH')
                edited = next(m for m in request(f'/chats/{direct}/messages', bob) if m['id'] == third)
                assert edited['sequence'] == 3 and edited['text'] == 'Edited'
                forwarded = request(f'/messages/{third}/forward', alice, {'chat_id': other, 'message_id': message_id(other)})['message_id']
                assert next(m for m in request(f'/chats/{other}/messages', bob) if m['id'] == forwarded)['sequence'] == 7
                request(f'/messages/{reply}', alice, {'for_everyone': True}, 'DELETE')
                assert request(f'/chats/{direct}/messages', bob)[-1]['sequence'] == 24
                newest = send(direct, 'After delete')
                assert request(f'/chats/{direct}/messages', bob)[-1]['sequence'] == 25
                with closing(sqlite3.connect(database)) as db, db:
                    db.execute('DELETE FROM messages WHERE id=?', (newest,))
                newest = send(direct, 'After physical removal')
                assert request(f'/chats/{direct}/messages', bob)[-1]['sequence'] == 26
                print('PASS reads/pins/replies/edit/forward/delete: order independent of ID, no sequence reuse', flush=True)
                if args.ui:
                    fixture = temp / 'fixture.json'
                    fixture.write_text(json.dumps({'token': alice, 'bob_token': bob, 'chat_id': direct, 'other_chat_id': other,
                                                   'reply_target': third, 'old_pin': history[0]['id']}), encoding='utf-8')
                    ui_env = {**env, 'MESSENGER_SEQUENCE_FIXTURE': str(fixture)}
                    subprocess.run(['dotnet', str(temp / 'desktop/MessengerDesktop.dll'), '--preview-sequences', base, str(output)],
                                   cwd=temp, env=ui_env, stdout=log, stderr=log, check=True, timeout=120)
                with closing(sqlite3.connect(database)) as db, db:
                    assert not db.execute('PRAGMA foreign_key_check').fetchall()
                    assert db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
                    assert db.execute('SELECT COUNT(*) FROM messages WHERE sequence IS NULL').fetchone()[0] == 0
                    last_sequence = db.execute('SELECT last_message_sequence FROM chats WHERE id=?', (direct,)).fetchone()[0]
                server.terminate()
                server.wait(timeout=10)
                server = subprocess.Popen(['dotnet', str(temp / 'backend/Messenger.Api.dll')], cwd=temp, env=env,
                                          stdout=log, stderr=log,
                                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
                for _ in range(100):
                    try:
                        request('/health')
                        break
                    except OSError:
                        time.sleep(.1)
                else:
                    raise RuntimeError('Temporary backend did not restart')
                send(direct, 'After server restart')
                assert request(f'/chats/{direct}/messages', bob)[-1]['sequence'] == last_sequence + 1
                print('PASS restart: migration is not repeated, persisted counter continues after restart', flush=True)
            finally:
                server.terminate()
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()
                log.flush()
                log.seek(0)
                for line in log:
                    if line.startswith(('PASS ', 'CHECK FAIL ', 'PREVIEW FAIL ')):
                        print(line.rstrip())
    print('PASS message sequence checks:', output)


if __name__ == '__main__':
    main()
