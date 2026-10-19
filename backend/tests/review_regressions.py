"""Regressions reproduced in PR #11; each case uses an isolated real server."""
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
from integration import Server


def account(s):
    body={'login':'reviewer','password':'review-password-123'}
    s.request('POST','/auth/register',body,status=201)
    return s.request('POST','/auth/login',body)['user_session_token']


def magic_prefix(s):
    s.files.mkdir()
    path=s.files/'legacy-magic.txt'
    original=b'MSGFILE1\0this is a valid legacy plaintext attachment'
    path.write_bytes(original)
    s.start()
    assert path.read_bytes()!=original
    token=account(s)
    group=s.request('POST','/chats/group',{'title':'legacy file','member_ids':[]},token,201)['chat_id']
    mid=s.request('POST',f'/chats/{group}/messages',{'text':'file'},token,201)['message_id']
    with closing(sqlite3.connect(s.db)) as c:
        att=c.execute("INSERT INTO attachments(message_id,file_name,path_to_file,file_type,file_size_bytes) VALUES(?, 'legacy.txt', ?, 'text/plain', ?)",(mid,str(path),len(original))).lastrowid
        c.commit()
    assert s.request('GET',f'/attachments/{att}',token=token)==original
    # Crash after replacement but before journal completion: do not encrypt ciphertext again.
    ciphertext=path.read_bytes()
    s.stop()
    with closing(sqlite3.connect(s.db)) as c:
        c.execute('DELETE FROM storage_maintenance')
        c.execute('UPDATE storage_file_migrations SET completed=0'); c.commit()
    s.start()
    assert path.read_bytes()==ciphertext
    assert s.request('GET',f'/attachments/{att}',token=token)==original
    # Crash before replacement: pending source hash still matches the legacy bytes.
    s.stop()
    second=s.files/'second-legacy.txt'; second.write_bytes(original)
    with closing(sqlite3.connect(s.db)) as c:
        source_hash=c.execute('SELECT source_hash FROM storage_file_migrations WHERE path=?',(str(path),)).fetchone()[0]
        c.execute('INSERT INTO storage_file_migrations VALUES(?,?,0)',(str(second),source_hash))
        c.execute('DELETE FROM storage_maintenance'); c.commit()
    s.start()
    assert second.read_bytes()!=original
    assert s.request('GET',f'/attachments/{att}',token=token)==original


def unchanged_restart(s):
    s.start(); s.stop()
    with closing(sqlite3.connect(s.db)) as c:
        c.executescript('CREATE TABLE review_padding(value BLOB);')
        c.executemany('INSERT INTO review_padding VALUES(?)',[(bytes(4000),)]*100)
        c.execute('DELETE FROM review_padding'); c.commit()
        before=c.execute('PRAGMA freelist_count').fetchone()[0]
    assert before>0
    s.start()
    with closing(sqlite3.connect(s.db)) as c:
        assert c.execute('PRAGMA freelist_count').fetchone()[0]>=before, 'restart unexpectedly ran VACUUM'


def last_owner_rollback(s):
    s.start(); token=account(s)
    group=s.request('POST','/chats/group',{'title':'atomic leave','member_ids':[]},token,201)['chat_id']
    mid=s.request('POST',f'/chats/{group}/messages',{'text':'with attachment'},token,201)['message_id']
    s.upload(f'/messages/{mid}/attachments',b'private file',token)
    with closing(sqlite3.connect(s.db)) as c:
        c.execute("CREATE TRIGGER reject_delete BEFORE DELETE ON chats BEGIN SELECT RAISE(ABORT,'injected failure'); END"); c.commit()
    s.request('POST',f'/chats/{group}/leave',token=token,status=500)
    with closing(sqlite3.connect(s.db)) as c:
        assert c.execute("SELECT COUNT(*) FROM chat_members WHERE chat_id=? AND role='owner'",(group,)).fetchone()[0]==1, 'failed leave lost owner'
        c.execute('DROP TRIGGER reject_delete'); c.commit()
    s.request('POST',f'/chats/{group}/leave',token=token,status=204)
    assert not [p for p in (s.files/'attachments').rglob('*') if p.is_file()], 'closing group left attachment files'


def batch_upload_rollback(s):
    s.start(); token=account(s)
    group=s.request('POST','/chats/group',{'title':'atomic upload','member_ids':[]},token,201)['chat_id']
    mid=s.request('POST',f'/chats/{group}/messages',{'text':'files'},token,201)['message_id']
    with closing(sqlite3.connect(s.db)) as c:
        c.execute("CREATE TRIGGER reject_attachment BEFORE INSERT ON attachments WHEN NEW.file_name='second.txt' BEGIN SELECT RAISE(ABORT,'injected failure'); END"); c.commit()
    boundary='review-boundary'
    body=b''
    for name in ['first.txt','second.txt']:
        body+=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: text/plain\r\n\r\nprivate content\r\n').encode()
    body+=f'--{boundary}--\r\n'.encode()
    s.request('POST',f'/messages/{mid}/attachments',body,token,500,{'Content-Type':f'multipart/form-data; boundary={boundary}'})
    with closing(sqlite3.connect(s.db)) as c:
        assert c.execute('SELECT COUNT(*) FROM attachments').fetchone()[0]==0, 'partial attachment batch committed'
    assert not [p for p in (s.files/'attachments').rglob('*') if p.is_file()], 'failed upload left files'


def run():
    failed=[]
    for case in [magic_prefix,unchanged_restart,last_owner_rollback,batch_upload_rollback]:
        with tempfile.TemporaryDirectory(prefix='messenger-review-') as directory:
            s=Server(directory)
            try:
                case(s)
                print('PASS',case.__name__,flush=True)
            except Exception as e:
                print('FAIL',case.__name__,str(e)[:200],flush=True)
                failed.append(case.__name__)
            finally: s.stop()
    assert not failed,failed


if __name__=='__main__': run()
