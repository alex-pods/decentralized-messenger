"""Disposable anti-spam backend, protocol checks and optional native desktop flow."""
import argparse, base64, os, secrets, socket, subprocess, tempfile, time, urllib.request
from pathlib import Path

parser=argparse.ArgumentParser()
parser.add_argument('--ui',action='store_true')
parser.add_argument('--client',type=Path)
parser.add_argument('--media',type=Path)
parser.add_argument('--concentration',action='store_true')
parser.add_argument('--skip-protocol',action='store_true',help='Rerun only the native UI after protocol checks have passed')
parser.add_argument('--scene-only',action='store_true',help='Capture the scene and stop before running the gameplay audit')
parser.add_argument('--clip',help='Use one catalog clip for a reproducible scene screenshot')
parser.add_argument('--audible',action='store_true',help='Enable sound for the full native round')
parser.add_argument('--round-only',action='store_true',help='Run the full round without repeating interruption checks')
parser.add_argument('--out',type=Path,default=Path('artifacts/antispam'))
args=parser.parse_args()
repo=Path(__file__).resolve().parents[1]
output=args.out.resolve();output.mkdir(parents=True,exist_ok=False)
testbin=output/'checks'
subprocess.run(['dotnet','build','tests/AntispamChecks','-c','Release','-o',str(testbin),'--nologo','-v','quiet',
                '-p:VlcWindowsX86Enabled=false','-p:VlcWindowsArm64Enabled=false'],cwd=repo,check=True)
checks=testbin/'AntispamChecks.dll'
subprocess.run(['dotnet',str(checks)],cwd=repo,check=True)
with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
address=f'http://127.0.0.1:{port}'
with tempfile.TemporaryDirectory(prefix='messenger-antispam-integration-') as directory:
    temp=Path(directory)
    env={**os.environ,'ASPNETCORE_URLS':address,'Db':str(temp/'check.db'),'MESSENGER_FILES':str(temp/'files'),
         'MESSENGER_DEV_SEED':'false','MESSENGER_ANTISPAM':'true','MESSENGER_KEY':base64.b64encode(secrets.token_bytes(32)).decode(),
         'Logging__LogLevel__Default':'Warning'}
    if args.media:env['MESSENGER_ANTISPAM_MEDIA']=str(args.media.resolve())
    if args.concentration:env['MESSENGER_PREVIEW_CONCENTRATION']='true'
    if args.scene_only:env['MESSENGER_PREVIEW_SCENE_ONLY']='true'
    if args.clip:env['MESSENGER_PREVIEW_CLIP']=args.clip
    if args.audible:env['MESSENGER_PREVIEW_AUDIBLE']='true'
    if args.round_only:env['MESSENGER_PREVIEW_ROUND_ONLY']='true'
    with (temp/'runtime.log').open('w+',encoding='utf-8') as log:
        server=subprocess.Popen(['dotnet',str(testbin/'Messenger.Api.dll')],cwd=temp,env=env,stdout=log,stderr=log,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
        try:
            for _ in range(100):
                try:
                    with urllib.request.urlopen(address+'/messenger/health',timeout=1):break
                except OSError:time.sleep(.1)
            else:raise RuntimeError('Backend not ready')
            if not args.skip_protocol:
                subprocess.run(['dotnet',str(checks),'--server',address+'/messenger'],cwd=temp,stdout=log,stderr=log,check=True,timeout=90)
            if args.ui:
                client=args.client.resolve() if args.client else testbin/'MessengerDesktop.dll'
                subprocess.run(['dotnet',str(client),'--preview-antispam',address+'/messenger',str(output)],
                               cwd=temp,env=env,stdout=log,stderr=log,check=True,timeout=210)
        finally:
            server.terminate()
            try:server.wait(timeout=10)
            except subprocess.TimeoutExpired:server.kill();server.wait()
            log.flush();log.seek(0)
            (output/'runtime.log').write_text(log.read(),encoding='utf-8');log.seek(0)
            for line in log:
                if line.startswith(('PASS ','CHECK FAIL ','Unhandled exception.')):print(line.rstrip())
print('PASS anti-spam integration:',output)
