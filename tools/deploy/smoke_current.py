"""Disposable-instance smoke test: creates accounts and a message. Never use on production."""
import argparse
import hashlib
import json
import subprocess
import time
import urllib.request
import uuid


def check(base, container=None, expect_assets=False):
    def request(path, data=None, token=None):
        headers={'Content-Type':'application/json'}
        if token: headers['Authorization']='Bearer '+token
        with urllib.request.urlopen(urllib.request.Request(base+path,
                data=json.dumps(data).encode() if data is not None else None, headers=headers),timeout=20) as response:
            payload=response.read()
            return json.loads(payload) if payload else None

    def healthy():
        for _ in range(60):
            try:
                assert request('/health')['service']=='messenger-backend-v2'
                return
            except (OSError,AssertionError): time.sleep(.5)
        raise RuntimeError('Backend did not become healthy')

    def check_assets(token):
        if not expect_assets:
            capabilities = request('/capabilities')['antispam']
            assert not capabilities['enabled'] and not capabilities['concentration'], 'Optional mode is on by default'
            assert request('/antispam/media', token=token)['clips'] == [], 'Default deployment loaded optional assets'
            return

        deadline = time.monotonic() + 90
        while not request('/capabilities')['antispam']['concentration']:
            if time.monotonic() >= deadline:
                raise RuntimeError('Optional asset catalog did not become ready')
            time.sleep(.5)
        clips = request('/antispam/media', token=token)['clips']
        assert len(clips) == 8, 'Incomplete asset catalog'
        clip = clips[0]
        with urllib.request.urlopen(urllib.request.Request(base+'/antispam/media/'+clip['id'],
                headers={'Authorization':'Bearer '+token}), timeout=30) as response:
            data = response.read()
        assert len(data) == clip['bytes'] and hashlib.sha256(data).hexdigest() == clip['sha256']

    healthy()
    users=[]
    for _ in range(2):
        credentials={'login':'smoke'+uuid.uuid4().hex[:10],'password':uuid.uuid4().hex}
        request('/auth/register',credentials)
        token=request('/auth/login',credentials)['user_session_token']
        users.append((token,request('/users/me',token=token)['id']))
    token=users[0][0]
    chat=request('/chats',{'another_user_id':users[1][1]},token)['chat_id']
    text='Persistent message '+uuid.uuid4().hex
    request(f'/chats/{chat}/messages',{'text':text,'client_ref':uuid.uuid4().hex},token)
    check_assets(token)
    if container:
        subprocess.run(['docker','restart',container],check=True,stdout=subprocess.DEVNULL)
        healthy()
    assert request('/users/me',token=token)['id']==users[0][1]
    messages=request(f'/chats/{chat}/messages',token=token)
    assert any(message['text']==text for message in messages), 'Message/key did not survive restart'
    assets = 'asset catalog and download checksum' if expect_assets else 'optional assets disabled by default'
    print('PASS current backend: auth, stored message, '+assets+
          (', session and decryption after restart' if container else ''),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url',required=True)
    parser.add_argument('--restart-container',help='Restart only this disposable test container')
    parser.add_argument('--expect-assets', action='store_true', help='Wait for optional assets and verify a download')
    args=parser.parse_args()
    check(args.base_url.rstrip('/'), args.restart_container, args.expect_assets)
