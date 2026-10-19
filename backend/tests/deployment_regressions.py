"""Exercise the real upgrade shell script against a disposable filesystem and fake services."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def run():
    if os.name=='nt':
        print('SKIP deployment shell regressions: require Linux')
        return
    original=(Path(__file__).parents[1]/'deploy/upgrade.sh').read_text()
    for symlink in [False,True]:
        for mode in ['success','wrong_revision','backup_failure']:
            with tempfile.TemporaryDirectory(prefix='messenger-deploy-test-') as directory:
                root=Path(directory)
                replacements={
                    '/opt/messenger/releases':str(root/'releases'),
                    '/opt/messenger/backend':str(root/'backend'),
                    '/var/backups/messenger-keys':str(root/'keybackups'),
                    '/var/backups/messenger':str(root/'backups'),
                    '/var/lib/messenger-keys':str(root/'keys'),
                    '/var/lib/messenger':str(root/'data'),
                    '/etc/systemd/system/messenger-backend.service':str(root/'service/messenger-backend.service'),
                    '/var/lock/messenger-deploy.lock':str(root/'deploy.lock'),
                }
                script=original
                for source,target in replacements.items(): script=script.replace(source,target)
                release=root/'releases/new'; release.mkdir(parents=True)
                (release/'MessengerBackend.dll').write_text('new')
                old=root/'releases/old' if symlink else root/'backend'
                old.mkdir(parents=True)
                (old/'MessengerBackend.dll').write_text('old')
                if symlink: (root/'backend').symlink_to(old,target_is_directory=True)
                (root/'data').mkdir(); (root/'data/marker').write_text('original-data')
                (root/'keys').mkdir(); (root/'keys/old-key').write_text('original-key')
                (root/'service/messenger-backend.service.d').mkdir(parents=True)
                (root/'service/messenger-backend.service').write_text('original-service')
                dropin=root/'service/messenger-backend.service.d/storage-security.conf'
                dropin.write_text('original-dropin')
                binary=root/'bin'; binary.mkdir()
                scripts={
                    'id':'#!/bin/sh\necho 0\n',
                    'sleep':'#!/bin/sh\nexit 0\n',
                    'install':f'''#!/usr/bin/env python3
import os,sys
args=sys.argv[1:]; filtered=[]; i=0
while i<len(args):
    if args[i] in ['-o','-g']: i+=2
    else: filtered.append(args[i]); i+=1
os.execv({shutil.which('install')!r},['install',*filtered])
''',
                    'cp':f'''#!/usr/bin/env python3
import os,sys
if os.environ['DEPLOY_TEST_MODE']=='backup_failure' and sys.argv[-2]==os.environ['DEPLOY_TEST_ROOT']+'/data': sys.exit(1)
os.execv({shutil.which('cp')!r},['cp',*sys.argv[1:]])
''',
                    'systemctl':'''#!/usr/bin/env python3
import os,sys,pathlib
r=pathlib.Path(os.environ['DEPLOY_TEST_ROOT'])
with (r/'services.log').open('a') as f: f.write(' '.join(sys.argv[1:])+chr(10))
if sys.argv[1]=='start' and (r/'backend').resolve()==r/'releases/new':
    (r/'data/marker').write_text('new-application-mutated-data')
''',
                    'curl':'''#!/usr/bin/env python3
import os,json
revision='a'*40 if os.environ['DEPLOY_TEST_MODE']=='success' else 'b'*40
print(json.dumps({'version':'4.1.1','revision':revision}))
''',
                }
                for name,text in scripts.items():
                    p=binary/name; p.write_text(text); p.chmod(0o755)
                upgrade=root/'upgrade.sh'; upgrade.write_text(script)
                env={**os.environ,'PATH':str(binary)+os.pathsep+os.environ['PATH'],
                     'DEPLOY_TEST_ROOT':str(root),'DEPLOY_TEST_MODE':mode}
                result=subprocess.run(['bash',str(upgrade),str(release),'a'*40],env=env,capture_output=True,text=True,timeout=20)
                assert (result.returncode==0)==(mode=='success'),result.stdout+result.stderr
                if mode=='success':
                    assert (root/'backend').resolve()==release
                    assert (root/'data/marker').read_text()=='new-application-mutated-data'
                else:
                    assert (root/'backend/MessengerBackend.dll').read_text()=='old'
                    assert (root/'data/marker').read_text()=='original-data'
                    assert dropin.read_text()=='original-dropin'
                assert 'start messenger-backend' in (root/'services.log').read_text()
                assert (root/'keys/old-key').read_text()=='original-key'
                print('PASS deployment', 'symlink' if symlink else 'directory',mode,flush=True)


if __name__=='__main__': run()
