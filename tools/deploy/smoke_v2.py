"""Opt-in v2 HTTP/WebSocket smoke test. Uses fresh random accounts; run against a test database.

Requires: pip install websockets==13.1
"""
import argparse
import asyncio
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import websockets


def request(base, path, body=None, token=None, method=None, expected=200, raw=None, content_type=None):
    headers={}
    if body is not None: headers['Content-Type']='application/json'
    if token: headers['Authorization']='Bearer '+token
    if content_type: headers['Content-Type']=content_type
    data=raw if raw is not None else json.dumps(body).encode() if body is not None else None
    try:
        response=urllib.request.urlopen(urllib.request.Request(base+path,data=data,headers=headers,method=method),timeout=15)
    except urllib.error.HTTPError as error: response=error
    with response:
        payload=response.read()
        assert response.code in (expected if isinstance(expected,tuple) else (expected,)), (path,response.code,payload[:200])
        if response.headers.get('Content-Type','').startswith('application/json') or response.headers.get('Content-Type','').startswith('application/problem+json'):
            return json.loads(payload) if payload else None
        return payload


async def event(ws, kind):
    for _ in range(20):
        data=json.loads(await asyncio.wait_for(ws.recv(),5))
        if data['type']==kind: return data
    raise AssertionError('Missing event '+kind)


async def smoke(base):
    assert request(base,'/health')['service']=='messenger-backend-v2'
    assert '/ws?' in request(base,'/ws/protocol')['url']
    request(base,'/users/me',expected=401)
    credentials=[{'login':'v2'+uuid.uuid4().hex[:10],'password':uuid.uuid4().hex} for _ in range(3)]
    tokens=[]; users=[]
    for account in credentials:
        request(base,'/auth/register',account,expected=201)
        token=request(base,'/auth/login',account)['user_session_token']
        tokens.append(token); users.append(request(base,'/users/me',token=token)['id'])
    request(base,'/auth/login',{'login':credentials[0]['login'],'password':'wrong-pass'},expected=401)
    second=request(base,'/auth/login',credentials[0])['user_session_token']
    wsbase=base.replace('https://','wss://').replace('http://','ws://')
    for suffix,origin,status in [('?token=invalid',None,401),('?token='+tokens[0],'https://untrusted.invalid',403)]:
        try:
            async with websockets.connect(wsbase+'/ws'+suffix,origin=origin): pass
        except websockets.exceptions.InvalidStatusCode as error: assert error.status_code==status
        else: raise AssertionError('Unauthorized websocket accepted')
    async with websockets.connect(wsbase+'/ws?token='+tokens[0]) as a, websockets.connect(wsbase+'/ws?user_session_token='+tokens[1]) as b, websockets.connect(wsbase+'/ws?token='+tokens[2]) as outsider:
        for ws in [a,b,outsider]: await event(ws,'connected')
        await a.send(['{"action":','"ping"}']); await event(a,'pong')
        await a.send('{'); await event(a,'error')
        chat=request(base,'/chats/group',{'title':'v2 smoke','member_ids':[users[1]]},tokens[0],expected=201)['chat_id']
        for ws in [a,b]:
            await ws.send(json.dumps({'action':'follow','chat_id':chat})); await event(ws,'followed')
        await outsider.send(json.dumps({'action':'follow','chat_id':chat})); await event(outsider,'error')
        request(base,f'/chats/{chat}/messages',token=tokens[2],expected=(403,404))
        content={'text':'v2 websocket smoke','client_ref':uuid.uuid4().hex}
        message=request(base,f'/chats/{chat}/messages',content,tokens[0],expected=201)['message_id']
        delivered=await event(b,'message')
        assert delivered['message']['id']==message and delivered['message']['text']==content['text']
        assert request(base,f'/chats/{chat}/messages',content,tokens[0])['message_id']==message
        request(base,f'/chats/{chat}/messages',{**content,'text':'different'},tokens[0],expected=409)
        request(base,f'/messages/{message}',{'text':'forbidden'},tokens[1],'PATCH',expected=403)
        request(base,f'/messages/{message}',{'text':'edited'},tokens[0],'PATCH',expected=204)
        assert (await event(b,'message_edited'))['message']['text']=='edited'
        request(base,f'/messages/{message}/reactions',{'emoji':'👍'},tokens[1],expected=204)
        assert (await event(a,'reaction_added'))['emoji']=='👍'
        boundary=uuid.uuid4().hex
        raw=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="smoke.txt"\r\nContent-Type: text/plain\r\n\r\nhello v2\r\n--{boundary}--\r\n').encode()
        attachment=request(base,f'/messages/{message}/attachments',token=tokens[0],method='POST',expected=201,raw=raw,content_type='multipart/form-data; boundary='+boundary)[0]
        assert request(base,'/attachments/'+str(attachment['id']),token=tokens[1])==b'hello v2'
        request(base,'/attachments/'+str(attachment['id']),token=tokens[2],expected=(403,404))
        await event(b,'attachment_added')
        request(base,f'/messages/{message}',{'for_everyone':True},tokens[0],'DELETE',expected=204)
        await event(b,'message_deleted')
        request(base,'/auth/logout',token=tokens[0],method='POST',expected=204)
        try:
            while True: await asyncio.wait_for(a.recv(),5)
        except websockets.exceptions.ConnectionClosed as error: assert error.code==4001,error.code
        request(base,'/users/me',token=tokens[0],expected=401)
        assert request(base,'/users/me',token=second)['id']==users[0]
        await b.send('{"action":"ping"}'); await event(b,'pong')
        await outsider.send('{"action":"ping"}'); await event(outsider,'pong')
    for payload in [b'binary','x'*17000]:
        async with websockets.connect(wsbase+'/ws?token='+second) as ws:
            await event(ws,'connected'); await ws.send(payload)
            try: await asyncio.wait_for(ws.recv(),5)
            except websockets.exceptions.ConnectionClosed as error: assert error.code==1009,error.code
            else: raise AssertionError('Oversize/binary frame accepted')
    print('PASS v2 auth, HTTP access controls, fragmented WS, follow/events, dedup, edits/reactions/files/deletion, logout session isolation, binary/oversize closes',flush=True)
    return second,users[0]


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url')
    parser.add_argument('--local-output',type=Path,help='Built backend-v2/bin/Release/net8.0; copied to a disposable test directory')
    args=parser.parse_args()
    if args.base_url:
        asyncio.run(smoke(args.base_url.rstrip('/'))); return
    if not args.local_output: parser.error('Choose --base-url or --local-output')
    with tempfile.TemporaryDirectory(prefix='v2-smoke-') as directory:
        root=Path(directory); app=root/'app'; shutil.copytree(args.local_output,app)
        (app/'Database/Migrations/002_demo_data.sql').unlink(missing_ok=True)
        base='http://127.0.0.1:18931/api/v2-test'
        env={**os.environ,'ASPNETCORE_URLS':'http://127.0.0.1:18931','MESSENGER_PATH_BASE':'/api/v2-test','Db':str(root/'messenger.db'), 'MESSENGER_FILES':str(root/'files'),'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics':'Warning'}
        def start(log):
            proc=subprocess.Popen(['dotnet',str(app/'Messenger.Api.dll')],env=env,stdout=log,stderr=log)
            for _ in range(60):
                if proc.poll() is not None: raise RuntimeError('Local v2 startup failed')
                try: request(base,'/health'); return proc
                except OSError: time.sleep(.2)
            proc.terminate(); proc.wait(); raise RuntimeError('Startup timeout')
        with (root/'server.log').open('w') as log:
            proc=start(log)
            try: token,user=asyncio.run(smoke(base))
            finally: proc.terminate(); proc.wait(timeout=15)
            proc=start(log)
            try: assert request(base,'/users/me',token=token)['id']==user
            finally: proc.terminate(); proc.wait(timeout=15)
        with closing(sqlite3.connect(root/'messenger.db')) as db:
            assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
            assert not db.execute('PRAGMA foreign_key_check').fetchall()
            assert db.execute('SELECT COUNT(*) FROM schema_migrations').fetchone()[0]==2
        print('PASS v2 restart, session persistence, migrations applied once, SQLite integrity/FKs; disposable test data removed')


if __name__=='__main__': main()
