#!/usr/bin/env python3
"""Root-owned Linux agent. SSH stdin JSON only; never expose this as a public API."""
import contextlib
try:
    import fcntl
except ImportError:  # Pure validation tests can run on Windows; management requires Linux.
    fcntl = None
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import tarfile
import time
import urllib.parse
import urllib.request
import uuid

ROOT = Path('/var/lib/messenger-preview')
CODE = Path('/opt/messenger-preview')
ROUTES = Path('/etc/nginx/messenger-previews')
CONFIG = ROOT / 'config.json'
REGISTRY = ROOT / 'registry.json'
MAX_UNPACKED = 160 * 1024 * 1024
PROFILES = {'legacy': 'MessengerBackend.dll', 'v2': 'Messenger.Api.dll'}


def run(*args, **kwargs):
    return subprocess.run(list(args), check=True, capture_output=True, text=True, **kwargs)


def atomic_json(path, value):
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value, indent=2) + '\n')
    temp.chmod(0o600)
    os.replace(temp, path)


def identifier(branch):
    if not isinstance(branch, str) or not branch or len(branch)>200 or branch.startswith('-') or any(ord(c)<32 for c in branch):
        raise ValueError('Invalid branch')
    return (re.sub('[^a-z0-9]+','-',branch.lower()).strip('-')[:32] or 'branch')+'-'+hashlib.sha256(branch.encode()).hexdigest()[:12]


def env_id(value):
    if not isinstance(value,str) or not re.fullmatch('[a-z0-9][a-z0-9-]{0,44}',value):
        raise ValueError('Invalid environment ID')
    return value


def safe_remove(path, parent):
    # Never follow a registry value or uploaded symlink during recursive deletion.
    if path.parent.resolve()!=parent.resolve() or path.is_symlink():
        raise ValueError('Unsafe cleanup path')
    if path.exists(): shutil.rmtree(path)


def unpack(archive, target):
    with tarfile.open(archive,'r:gz') as bundle:
        members=bundle.getmembers()
        if len(members)>20000 or sum(m.size for m in members)>MAX_UNPACKED:
            raise ValueError('Package too large')
        seen=set()
        for member in members:
            p=Path(member.name)
            if (p.is_absolute() or '..' in p.parts or '\\' in member.name or
                not p.parts or p.parts[0] not in ('source','publish') or
                not member.isfile() or member.name in seen):
                raise ValueError('Unsafe archive member')
            seen.add(member.name)
        for member in members:
            destination=target/member.name
            destination.parent.mkdir(parents=True,exist_ok=True)
            with bundle.extractfile(member) as source, destination.open('xb') as out:
                shutil.copyfileobj(source,out)
            # No executable/setuid bits or ownership supplied by the branch.
            destination.chmod(0o644)


def unit_name(eid): return 'messenger-preview-'+env_id(eid)+'.service'
def unit_path(eid): return Path('/etc/systemd/system')/unit_name(eid)
def route_path(eid): return ROUTES/(env_id(eid)+'.conf')


def route_text(eid,port):
    prefix='/api/'+env_id(eid)
    return f'''# Managed by messenger-preview. Do not edit.
location = {prefix} {{ return 308 {prefix}/; }}
location ^~ {prefix}/ {{
    access_log /var/log/nginx/messenger-preview.log messenger_preview_safe;
    client_max_body_size 64m;
    proxy_pass http://127.0.0.1:{int(port)};
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_read_timeout 120s;
}}
'''


def set_route(eid,text):
    path=route_path(eid)
    previous=path.read_text() if path.exists() else None
    try:
        temp=path.with_suffix('.pending'); temp.write_text(text); os.replace(temp,path)
        run('nginx','-t'); run('systemctl','reload','nginx')
    except BaseException:
        if previous is None: path.unlink(missing_ok=True)
        else: path.write_text(previous)
        run('nginx','-t'); run('systemctl','reload','nginx')
        raise


def install(req):
    parsed=urllib.parse.urlparse(req['base_url'])
    if parsed.scheme!='https' or not parsed.hostname or parsed.path not in ('','/') or parsed.query or parsed.fragment or parsed.username:
        raise ValueError('base_url must be an HTTPS origin')
    site=Path(req['nginx_site']).resolve()
    if site.parent!=Path('/etc/nginx/sites-available').resolve() or not site.is_file():
        raise ValueError('Expected an existing site in /etc/nginx/sites-available')
    for p in (ROOT,ROOT/'incoming',ROOT/'data',CODE,ROUTES): p.mkdir(parents=True,exist_ok=True)
    (ROOT/'incoming').chmod(0o700)
    old=site.read_text()
    include='    include /etc/nginx/messenger-previews/*.conf;'
    logging=Path('/etc/nginx/conf.d/messenger-preview-logging.conf')
    log_previous=logging.read_text() if logging.exists() else None
    if include not in old:
        anchor='    server_name '+parsed.hostname+';'
        # The same name commonly appears in HTTP redirect and HTTPS server blocks.
        starts=[m.start() for m in re.finditer(r'(?m)^server\s*\{',old)]
        blocks=[(start,starts[i+1] if i+1<len(starts) else len(old)) for i,start in enumerate(starts)]
        matches=[(a,b) for a,b in blocks if anchor in old[a:b] and re.search(r'listen\s+(?:[^;\s]*:)?443\s+ssl\b',old[a:b])]
        if len(matches)!=1: raise ValueError('Expected one matching HTTPS server block')
        a,b=matches[0]
        new=old[:a]+old[a:b].replace(anchor,anchor+'\n'+include,1)+old[b:]
    else: new=old
    backup=ROOT/('nginx-before-'+time.strftime('%Y%m%dT%H%M%SZ')+'.conf')
    backup.write_text(old); backup.chmod(0o600)
    try:
        logging.write_text('log_format messenger_preview_safe \'$remote_addr [$time_local] "$request_method $uri $server_protocol" $status $body_bytes_sent\';\n')
        site.write_text(new)
        run('nginx','-t'); run('systemctl','reload','nginx')
        atomic_json(CONFIG,{'base_url':req['base_url'].rstrip('/'),'site':str(site)})
    except BaseException:
        site.write_text(old)
        if log_previous is None: logging.unlink(missing_ok=True)
        else: logging.write_text(log_previous)
        run('nginx','-t'); run('systemctl','reload','nginx')
        raise
    return {'installed':True,'base_url':req['base_url'],'nginx_backup':str(backup)}


def free_port():
    for port in range(18100,18200):
        with socket.socket() as s:
            try: s.bind(('127.0.0.1',port)); return port
            except OSError: pass
    raise ValueError('No preview ports available')


def deploy(req,registry,config):
    eid=identifier(req['branch']); profile=req['profile']; sha=req['revision']
    if profile not in PROFILES or not re.fullmatch('[0-9a-f]{40}',sha): raise ValueError('Invalid profile/revision')
    if profile!='legacy': raise ValueError('v2 is not deployable yet: build/readiness/prefix contract must be fixed first')
    previous=registry.get(eid)
    if previous and previous['status']=='running':
        raise ValueError('Environment is running. Stop it first; implicit schema updates are disabled')
    if previous and previous['revision']!=sha:
        raise ValueError('Saved data belongs to a different commit. Automatic schema updates are disabled; use an explicit new environment design')
    if sum(v['status']=='running' for v in registry.values())>=2: raise ValueError('At most two previews on this small server')
    upload=req['upload']
    if not re.fullmatch('[0-9a-f]{32}',upload): raise ValueError('Invalid upload ID')
    archive=ROOT/'incoming'/(upload+'.tar.gz')
    if not archive.is_file() or hashlib.sha256(archive.read_bytes()).hexdigest()!=req['bundle_sha256']:
        raise ValueError('Package checksum mismatch')
    if shutil.disk_usage(CODE).free<96*1024*1024: raise ValueError('At least 96 MiB free is required')
    code=CODE/eid
    if code.exists(): raise ValueError('Code directory already exists; stop/inspect it first')
    code.mkdir(mode=0o755)
    started=False
    route_before=route_path(eid).read_text() if route_path(eid).exists() else None
    published=False
    attempted=False
    try:
        unpack(archive,code)
        binary=code/'publish'/PROFILES[profile]
        if not binary.is_file() or (code/'publish/REVISION').read_text().strip()!=sha:
            raise ValueError('Invalid publish artifact')
        port=free_port(); prefix='/api/'+eid
        # StateDirectory supplies a distinct private writable tree and stable ownership via systemd.
        unit=f'''[Unit]
Description=Messenger preview {eid}
After=network.target
[Service]
Type=simple
DynamicUser=yes
StateDirectory=messenger-preview-{eid}
StateDirectoryMode=0700
WorkingDirectory={code}/publish
ExecStart=/usr/bin/dotnet {binary}
Environment=ASPNETCORE_URLS=http://127.0.0.1:{port}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=MESSENGER_PATH_BASE={prefix}
Environment=MESSENGER_DB=/var/lib/messenger-preview-{eid}/messenger.db
Environment=MESSENGER_FILES=/var/lib/messenger-preview-{eid}/files
Environment=MESSENGER_KEYS=/var/lib/messenger-preview-{eid}/keys
UMask=0077
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
NoNewPrivileges=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictSUIDSGID=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
CapabilityBoundingSet=
IPAddressDeny=any
IPAddressAllow=localhost
MemoryMax=192M
CPUQuota=50%
TasksMax=64
TimeoutStopSec=15s
KillMode=control-group
Restart=no
[Install]
WantedBy=multi-user.target
'''
        record={'environment':eid,'branch':req['branch'],'revision':sha,'profile':profile,
                'url':config['base_url']+prefix,'port':port,'status':'starting',
                'state_directory':'/var/lib/messenger-preview-'+eid}
        registry[eid]=record
        atomic_json(REGISTRY,registry)
        attempted=True
        unit_path(eid).write_text(unit)
        run('systemctl','daemon-reload'); run('systemctl','start',unit_name(eid)); started=True
        ready=False
        for _ in range(60):
            try:
                with urllib.request.urlopen(f'http://127.0.0.1:{port}{prefix}/health',timeout=2) as response:
                    health=json.load(response)
                if health.get('revision')==sha and health.get('status')=='ok': ready=True; break
            except (OSError,ValueError): pass
            time.sleep(.5)
        if not ready: raise ValueError('Readiness failed; inspect journalctl; no public route was changed')
        set_route(eid,route_text(eid,port))
        published=True
        record['status']='running'
        registry[eid]=record
        atomic_json(REGISTRY,registry)
        return record
    except BaseException:
        if published:
            if route_before is None:
                route_path(eid).unlink(missing_ok=True)
                run('nginx','-t'); run('systemctl','reload','nginx')
            else: set_route(eid,route_before)
        if started or unit_path(eid).exists(): run('systemctl','stop',unit_name(eid))
        unit_path(eid).unlink(missing_ok=True); run('systemctl','daemon-reload')
        safe_remove(code,CODE)
        if attempted:
            registry[eid]['status']='failed'
            atomic_json(REGISTRY,registry)
        raise
    finally: archive.unlink(missing_ok=True)


def stop(eid,registry):
    eid=env_id(eid)
    if eid not in registry: raise ValueError('Unknown environment')
    # Keep an explicit tombstone route: it must not fall through to another site's upstream.
    prefix='/api/'+eid
    set_route(eid,f'location = {prefix} {{ return 503; }}\nlocation ^~ {prefix}/ {{ return 503; }}\n')
    if unit_path(eid).exists(): run('systemctl','stop',unit_name(eid))
    unit_path(eid).unlink(missing_ok=True)
    run('systemctl','daemon-reload')
    safe_remove(CODE/eid,CODE)
    registry[eid]['status']='stopped'
    atomic_json(REGISTRY,registry)
    return {**registry[eid],'code_removed':True,'database_files_keys_retained':True}


def main(req):
    if fcntl is None: raise ValueError('Agent requires Linux')
    if os.geteuid()!=0: raise ValueError('Root operator required')
    if req['action']=='install': return install(req)
    ROOT.mkdir(parents=True,exist_ok=True)
    with (ROOT/'lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        registry=json.loads(REGISTRY.read_text()) if REGISTRY.exists() else {}
        config=json.loads(CONFIG.read_text())
        action=req['action']
        if action=='deploy': return deploy(req,registry,config)
        if action=='list': return list(registry.values())
        eid=env_id(req['environment'])
        if eid not in registry: raise ValueError('Unknown environment')
        if action=='stop': return stop(eid,registry)
        if action=='logs':
            # Application logs may contain sensitive data; returned only over the operator's SSH.
            return {'journal':run('journalctl','-u',unit_name(eid),'-n','80','--no-pager','-o','cat').stdout}
        if action=='status':
            state=subprocess.run(['systemctl','is-active',unit_name(eid)],capture_output=True,text=True).stdout.strip()
            return {**registry[eid],'service_state':state,'code_present':(CODE/eid).exists()}
        raise ValueError('Unknown action')


if __name__=='__main__':
    try:
        request=sys.stdin.read(32769)
        if len(request)>32768: raise ValueError('Request too large')
        print(json.dumps(main(json.loads(request)),ensure_ascii=False,indent=2))
    except Exception as e:
        print(str(e),file=sys.stderr)
        if isinstance(e,subprocess.CalledProcessError) and e.stderr: print(e.stderr,file=sys.stderr)
        sys.exit(1)
