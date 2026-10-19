"""Real HTTP + desktop/WS regression suite, isolated from production data. Python 3 stdlib only."""
import base64
import concurrent.futures
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / 'backend/bin/Release/net8.0/MessengerBackend.dll'


class Server:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.db = self.directory / 'messenger.db'
        self.files = self.directory / 'files'
        self.keys = self.directory / 'keys'
        with socket.socket() as s:
            s.bind(('127.0.0.1', 0))
            self.port = s.getsockname()[1]
        self.url = f'http://127.0.0.1:{self.port}/messenger'
        self.process = None

    def start(self):
        self.log = open(self.directory / 'server.log', 'ab')
        self.process = subprocess.Popen(['dotnet', str(DLL)], stdout=self.log, stderr=self.log,
            env={**os.environ, 'ASPNETCORE_URLS': f'http://127.0.0.1:{self.port}',
                 'MESSENGER_DB': str(self.db), 'MESSENGER_FILES': str(self.files),
                 'MESSENGER_KEYS': str(self.keys), 'MESSENGER_PATH_BASE': '/messenger'})
        for _ in range(150):
            if self.process.poll() is not None:
                raise AssertionError((self.directory / 'server.log').read_text(errors='replace'))
            try:
                self.request('GET', '/health')
                return
            except (OSError, AssertionError):
                time.sleep(.1)
        raise AssertionError('server did not start')

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=15)
        if hasattr(self, 'log'):
            self.log.close()

    def request(self, method, path, data=None, token=None, status=200, headers=None):
        h = dict(headers or {})
        if token:
            h['Authorization'] = 'Bearer ' + token
        if isinstance(data, (dict, list)):
            data = json.dumps(data).encode()
            h['Content-Type'] = 'application/json'
        req = urllib.request.Request(self.url + path, data=data, headers=h, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=15)
        except urllib.error.HTTPError as e:
            response = e
        body = response.read()
        assert response.code == status, (method, path, response.code, body)
        if response.headers.get('Content-Type', '').startswith('application/json') and body:
            return json.loads(body)
        return body

    def upload(self, path, data, token, name='sample.txt', status=201):
        boundary = uuid.uuid4().hex
        name = name.replace('"', '\\"')
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n'
                'Content-Type: application/octet-stream\r\n\r\n').encode() + data + f'\r\n--{boundary}--\r\n'.encode()
        return self.request('POST', path, body, token, status,
                            {'Content-Type': f'multipart/form-data; boundary={boundary}'})


def run():
    with tempfile.TemporaryDirectory(prefix='messenger-integration-') as directory:
        s = Server(directory)
        try:
            # Original v4 database, including unencrypted history and files.
            with closing(sqlite3.connect(s.db)) as db:
                db.executescript((Path(__file__).parent / 'fixtures/v4.sql').read_text())
                salt = b'0123456789abcdef'
                digest = hashlib.pbkdf2_hmac('sha256', b'password123', salt, 240000)
                pw = f'pbkdf2_sha256$240000${base64.b64encode(salt).decode()}${base64.b64encode(digest).decode()}'
                db.execute("INSERT INTO users(id,tag,password_hash,display_name,bio,join_date) VALUES(1,'legacy',?,'Legacy','old bio','2026-01-01')", (pw,))
                db.execute("INSERT INTO users(id,tag,password_hash,display_name,join_date) VALUES(2,'peer',?,'Peer','2026-01-01')", (pw,))
                db.execute("INSERT INTO chats VALUES(1,'direct',NULL,'2026-01-01',1)")
                db.executemany("INSERT INTO chat_members(chat_id,user_id,role,joined_at) VALUES(1,?,'member','2026-01-01')", [(1,), (2,)])
                db.execute('INSERT INTO direct_chats VALUES(1,1,2)')
                db.execute("INSERT INTO messages(id,chat_id,sender_id,text,sent_at) VALUES(1,1,1,'legacy secret message','2026-01-01')")
                attachment = s.files / 'attachments/1/legacy.txt'
                attachment.parent.mkdir(parents=True)
                attachment.write_bytes(b'legacy secret file')
                orphan = s.files / 'old-avatar.png'
                orphan.write_bytes(b'old private orphan avatar')
                db.execute("INSERT INTO attachments VALUES(1,1,'legacy.txt',?,'text/plain',18)", (str(attachment),))
                db.commit()
            s.start()
            login = lambda tag: s.request('POST', '/auth/login', {'login': tag, 'password': 'password123'})['user_session_token']
            a, peer = login('legacy'), login('peer')
            assert s.request('GET', '/users/me', token=a)['bio'] == 'old bio'
            assert s.request('GET', '/chats/1/messages', token=a)[0]['text'] == 'legacy secret message'
            assert s.request('GET', '/attachments/1', token=peer) == b'legacy secret file'
            assert attachment.read_bytes().startswith(b'MSGFILE1\0')
            assert orphan.read_bytes().startswith(b'MSGFILE1\0')
            with closing(sqlite3.connect(s.db)) as db:
                assert db.execute('SELECT text FROM messages WHERE id=1').fetchone()[0] != 'legacy secret message'
                assert db.execute('SELECT bio FROM users WHERE id=1').fetchone()[0] != 'old bio'
                assert db.execute('SELECT password_hash FROM users WHERE id=1').fetchone()[0].startswith('pbkdf2_sha256$600000$')
                assert db.execute('SELECT COUNT(*) FROM schema_migrations').fetchone()[0] == 4
            assert b'legacy secret message' not in s.db.read_bytes()
            print('PASS legacy schema/data/file migration, password upgrade, plaintext scrubbing')

            s.request('POST', '/auth/register', {'login': 'outsider', 'password': 'password123'}, status=201)
            outsider = login('outsider')
            s.request('GET', '/chats/1/messages', token=outsider, status=403)
            s.request('GET', '/attachments/1', token=outsider, status=403)
            s.request('GET', '/users/me', status=401)
            s.request('POST', '/auth/register', {'login': 'LEGACY', 'password': 'password123'}, status=409)
            s.request('POST', '/auth/login', {'login': 'legacy', 'password': 'x' * 1025}, status=401)
            s.request('POST', '/auth/login', b'{broken', status=400, headers={'Content-Type':'application/json'})
            s.request('POST', '/auth/login', {'login':'legacy', 'password':'password123', 'extra':1}, status=400)
            s.request('POST', '/auth/login', b'text', status=415, headers={'Content-Type':'text/plain'})
            s.request('GET', '/ws?token=' + a, status=403, headers={'Origin':'https://evil.example'})
            print('PASS authorization, validation, duplicate login, cross-origin WS rejection')

            def send(text, ref=None):
                return s.request('POST', '/chats/1/messages', {'text':text, 'client_ref':ref}, a, 201)['message_id']
            mid = send('secret new message', 'retry-1')
            assert s.request('POST', '/chats/1/messages', {'text':'secret new message', 'client_ref':'retry-1'}, a)['message_id'] == mid
            s.request('POST', '/chats/1/messages', {'text':'different', 'client_ref':'retry-1'}, a, 409)
            s.request('PATCH', f'/messages/{mid}', {'text':'edited secret'}, a, 204)
            assert s.request('POST', '/chats/1/messages', {'text':'secret new message', 'client_ref':'retry-1'}, a)['message_id'] == mid
            s.request('PATCH', '/users/me', {'bio':'new secret bio'}, a, 204)
            s.request('PATCH', '/users/me', {'display_name':'New name'}, a, 204)
            assert s.request('GET', '/users/me', token=a)['bio'] == 'new secret bio'
            assert s.request('GET', '/chats/1/messages', token=peer)[-1]['text'] == 'edited secret'
            att = s.upload(f'/messages/{mid}/attachments', b'new private attachment', a, 'name"test.txt')[0]
            assert s.request('GET', f"/attachments/{att['id']}", token=peer) == b'new private attachment'
            with closing(sqlite3.connect(s.db)) as db:
                path = db.execute('SELECT path_to_file FROM attachments WHERE id=?',(att['id'],)).fetchone()[0]
                assert b'new private attachment' not in Path(path).read_bytes()
            # Real 1x1 PNG.
            def chunk(kind, data):
                return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
            png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR',struct.pack('>IIBBBBB',1,1,8,6,0,0,0)) + chunk(b'IDAT',zlib.compress(b'\x00\xff\x00\x00\xff')) + chunk(b'IEND',b'')
            s.upload('/users/me/avatar', png[:24], a, 'fake.png', status=400)
            s.upload('/users/me/avatar', png, a, 'avatar.png')
            assert s.request('GET', '/users/1/avatar', token=peer) == png
            print('PASS encrypted message/profile edits, attachment and avatar round trips')

            # Hidden newest message must fall back to the previous visible message.
            latest = send('hide me')
            s.request('DELETE', f'/messages/{latest}', {'for_everyone':False}, a, 204)
            chats = s.request('GET', '/chats', token=a)
            assert next(c for c in chats if c['id'] == 1)['last_message']['id'] == mid
            # Member cursors from an unrelated group cannot affect direct chat read_at.
            s.request('POST', '/chats/1/read', {'message_id':mid}, peer, 204)
            s.request('POST', '/chats/group', {'title':'unrelated','member_ids':[2,3]}, a, 201)
            assert next(c for c in s.request('GET', '/chats', token=a) if c['id']==1)['last_message']['read_at'] is not None
            s.request('DELETE', f'/messages/{mid}', {'for_everyone':True}, a, 204)
            s.request('GET', f"/attachments/{att['id']}", token=peer, status=404)
            assert not Path(path).exists()
            with closing(sqlite3.connect(s.db)) as db:
                assert db.execute('SELECT text FROM messages WHERE id=?',(mid,)).fetchone()[0] == ''
            print('PASS hidden preview, read isolation, deleted content and file erasure')

            # Concurrent direct chat creation must produce one complete pair.
            def direct(_):
                return s.request('POST', '/chats', {'another_user_id':3}, a, status=200)['chat_id']
            first = s.request('POST', '/chats', {'another_user_id':3}, a, 201)['chat_id']
            with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
                assert set(pool.map(direct, range(12))) == {first}
            group = s.request('POST','/chats/group',{'title':'owners','member_ids':[2]},a,201)['chat_id']
            s.request('POST',f'/chats/{group}/owner',{'user_id':2},a,204)
            members = s.request('GET',f'/chats/{group}/members',token=peer)
            assert sum(m['role']=='owner' for m in members)==1
            s.request('POST',f'/chats/{group}/owner',{'user_id':1},a,403)
            with closing(sqlite3.connect(s.db)) as db:
                count_before = db.execute('SELECT COUNT(*) FROM chats').fetchone()[0]
                db.execute("CREATE TRIGGER test_reject_member BEFORE INSERT ON chat_members WHEN NEW.user_id=3 BEGIN SELECT RAISE(ABORT,'injected failure'); END")
                db.commit()
            s.request('POST','/chats/group',{'title':'must roll back','member_ids':[3]},a,500)
            with closing(sqlite3.connect(s.db)) as db:
                assert db.execute('SELECT COUNT(*) FROM chats').fetchone()[0] == count_before
                db.execute('DROP TRIGGER test_reject_member'); db.commit()
            print('PASS concurrent direct creation and group ownership invariants')

            swagger = s.request('GET','/swagger/v1/swagger.json')
            assert swagger['servers'][0]['url']=='/messenger'
            assert not swagger['paths']['/auth/login']['post'].get('security')
            assert swagger['paths']['/users/me']['get']['security']
            assert '415' in swagger['paths']['/messages/{message_id}']['delete']['responses']
            schemas=swagger['components']['schemas']
            def refs(value):
                if isinstance(value,dict):
                    for k,v in value.items():
                        if k=='$ref' and v.startswith('#/components/schemas/'):
                            assert v.rsplit('/',1)[1] in schemas, v
                        else: refs(v)
                elif isinstance(value,list):
                    for v in value: refs(v)
            refs(swagger)
            print('PASS OpenAPI path base, auth, error schemas and reference integrity')

            protocol = ROOT / 'tests/ProtocolChecks/bin/Release/net8.0/ProtocolChecks.dll'
            if protocol.exists():
                subprocess.run(['dotnet',str(protocol),s.url,str(s.process.pid)],check=True,timeout=90)

            s.stop(); s.start()
            assert s.request('GET','/users/me',token=a)['bio']=='new secret bio'
            assert s.request('GET','/attachments/1',token=peer)==b'legacy secret file'
            assert s.request('GET','/users/1/avatar',token=a)==png
            print('PASS restart decrypts with persisted keys, migration is idempotent')

            desktop = ROOT / 'desktop/bin/Release/net8.0/MessengerDesktop.dll'
            if desktop.exists():
                subprocess.run(['dotnet',str(desktop),'--check',s.url],check=True,timeout=90)
                print('PASS desktop client self-check including WebSocket events')
            s.request('POST','/auth/logout',token=a,status=204)
            s.request('GET','/users/me',token=a,status=401)
            for _ in range(32):
                req=urllib.request.Request(s.url+'/auth/login',data=b'{}',headers={'Content-Type':'application/json'},method='POST')
                try: response=urllib.request.urlopen(req)
                except urllib.error.HTTPError as e: response=e
                if response.code==429:
                    assert response.headers['Retry-After']=='60'; break
            else: raise AssertionError('auth limiter did not reject')
            print('PASS logout revocation and auth rate limit')

            s.stop()
            s.keys.rename(s.directory/'keys-backup')
            s.log=open(s.directory/'missing-keys.log','wb')
            p=subprocess.Popen(['dotnet',str(DLL)],stdout=s.log,stderr=s.log,
                env={**os.environ,'MESSENGER_DB':str(s.db),'MESSENGER_FILES':str(s.files),'MESSENGER_KEYS':str(s.keys)})
            try:
                assert p.wait(timeout=15)!=0
            finally:
                if p.poll() is None: p.kill(); p.wait()
                s.log.close()
            print('PASS missing encryption keys prevent startup')
        finally:
            s.stop()


if __name__=='__main__':
    run()
