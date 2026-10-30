"""Docker-only helper sent over SSH. No systemd units, host runtime or permanent agent."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

MANAGED = 'io.messenger.deploy'
SOURCE = 'io.messenger.source'
INSTANCE = 'io.messenger.instance'


def docker(*args, **kwargs):
    return subprocess.run(['docker', *args], check=True, text=True, **kwargs)


def inspect(kind, name):
    result = subprocess.run(['docker', kind, 'inspect', name], capture_output=True, text=True)
    if result.returncode:
        if 'no such' in result.stderr.lower(): return None
        raise ValueError(result.stderr.strip())
    value = json.loads(result.stdout)[0]
    labels = value.get('Config', {}).get('Labels') if kind in ('container', 'image') else value.get('Labels')
    if (labels or {}).get(MANAGED) != 'true': raise ValueError('Refusing to touch unmanaged ' + kind + ': ' + name)
    return value


def upload_path(value):
    if not re.fullmatch(r'/tmp/messenger-deploy-[a-z0-9_]+/bundle.tar.gz', value or ''):
        raise ValueError('Invalid upload path')
    path = Path(value)
    if path.is_symlink() or path.parent.is_symlink(): raise ValueError('Upload symlink refused')
    if path.parent.exists() and path.parent.stat().st_uid not in (0, int(os.environ.get('SUDO_UID', os.getuid()))):
        raise ValueError('Upload owner mismatch')
    return path


def discard(value):
    path = upload_path(value)
    if path.parent.exists(): shutil.rmtree(path.parent)


def unpack(archive, output):
    total, seen = 0, set()
    with tarfile.open(archive, 'r:gz') as bundle:
        for item in bundle:
            path = PurePosixPath(item.name)
            total += item.size
            if (not item.isfile() or path.is_absolute() or '..' in path.parts or '\\' in item.name
                    or not path.parts or path.as_posix() in seen or total > 512 * 1024 ** 2 or len(seen) > 20000):
                raise ValueError('Unsafe deployment archive')
            seen.add(path.as_posix())
            if shutil.disk_usage(output).free < item.size + 64 * 1024 ** 2: raise ValueError('Insufficient disk space')
            target = output.joinpath(*path.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with bundle.extractfile(item) as source, target.open('xb') as destination:
                shutil.copyfileobj(source, destination)
            target.chmod(0o644)


def validate(request):
    if not re.fullmatch(r'[a-z][a-z0-9-]{0,47}', request.get('name', '')): raise ValueError('Invalid instance name')
    if request.get('action') not in ['preflight', 'prepare', 'discard', 'deploy', 'status', 'logs', 'start', 'stop', 'remove']:
        raise ValueError('Unknown action')
    if type(request.get('purge_data', False)) is not bool: raise ValueError('Invalid purge flag')
    if request['action'] in ['preflight', 'deploy']:
        if type(request.get('port')) is not int or not 1024 <= request['port'] <= 65535: raise ValueError('Invalid API port')
        if request.get('listen') not in ['127.0.0.1', '0.0.0.0']: raise ValueError('Invalid bind address')
        if not re.fullmatch(r'(?:/[A-Za-z0-9_-]+)*', request.get('path_base', '')): raise ValueError('Invalid path base')
    if request['action'] == 'deploy':
        if not re.fullmatch('[0-9a-f]{40}', request.get('revision', '')): raise ValueError('Invalid revision')
        if not re.fullmatch('[0-9a-f]{64}', request.get('sha256', '')): raise ValueError('Invalid digest')


def identity(request):
    return hashlib.sha256(json.dumps([request['repo'], request['branch'], request['revision']]).encode()).hexdigest()


def preflight(request):
    info = json.loads(docker('info', '--format', '{{json .}}', capture_output=True).stdout)
    if info['OSType'] != 'linux' or info['Architecture'] not in ['x86_64', 'aarch64', 'arm64']:
        raise ValueError('A Linux x64/arm64 Docker server is required')
    if inspect('container', 'messenger-' + request['name']): raise ValueError('Container already exists; remove it before deploy')
    if shutil.disk_usage('/tmp').free < 256 * 1024 ** 2: raise ValueError('Need 256 MiB free plus Docker image storage')
    return {'runtime': 'linux-x64' if info['Architecture'] == 'x86_64' else 'linux-arm64'}


def wait_ready(request, timeout=40):
    url = f"http://127.0.0.1:{request['port']}{request['path_base']}/health"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with opener.open(url, timeout=2) as response: health = json.load(response)
            if health.get('status') == 'ok' and health.get('revision') == request['revision']: return
        except (OSError, ValueError): pass
        time.sleep(.5)
    raise ValueError('Readiness failed; container removed, data volume retained')


def deploy(request):
    preflight(request)
    name = request['name']
    container, volume = 'messenger-' + name, 'messenger-' + name + '-data'
    image = 'messenger-deploy/' + name + ':' + request['revision']
    source = identity(request)
    previous = inspect('volume', volume)
    if previous and previous['Labels'].get(SOURCE) != source:
        raise ValueError('Volume belongs to a different source/revision; migrate explicitly or use a new name')
    inspect('image', image)  # Never replace an unmanaged tag.
    archive = upload_path(request['upload'])
    digest = hashlib.sha256()
    with archive.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''): digest.update(chunk)
    if digest.hexdigest() != request['sha256']: raise ValueError('Bundle digest mismatch')
    container_id = None
    built = False
    try:
        with tempfile.TemporaryDirectory(prefix='messenger-image-') as directory:
            output = Path(directory)
            unpack(archive, output)
            if (output/'REVISION').read_text().strip() != request['revision']: raise ValueError('Bundle revision mismatch')
            docker('build', '--build-arg', 'BUILD_STAGE=prebuilt', '--label', MANAGED+'=true',
                   '--label', INSTANCE+'='+name, '--label', SOURCE+'='+source, '-t', image, directory,
                   env={**os.environ, 'DOCKER_BUILDKIT':'1'}, stdout=sys.stderr)
        built = True
        if not previous:
            docker('volume', 'create', '--label', MANAGED+'=true', '--label', SOURCE+'='+source, volume, stdout=subprocess.DEVNULL)
        # Fixed initialization command; no selected branch code is executed as root.
        docker('run', '--rm', '--network', 'none', '--user', '0:0', '--mount', 'type=volume,src='+volume+',dst=/state',
               '--entrypoint', 'sh', image, '-c', 'chown 1654:1654 /state && chmod 700 /state', stdout=subprocess.DEVNULL)
        container_id = docker('create', '--name', container, '--label', MANAGED+'=true',
            '--label', SOURCE+'='+source, '--restart', 'unless-stopped', '--read-only',
            '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--pids-limit', '128',
            '--memory', '256m', '--cpus', '1', '--tmpfs', '/tmp:rw,nosuid,noexec,size=64m',
            '--log-opt', 'max-size=10m', '--log-opt', 'max-file=2',
            '-p', f"{request['listen']}:{request['port']}:8080",
            '--mount', 'type=volume,src='+volume+',dst=/state',
            '-e', 'MESSENGER_PATH_BASE='+request['path_base'], image, capture_output=True).stdout.strip()
        docker('start', container_id, stdout=subprocess.DEVNULL)
        wait_ready(request)
        return {'name':name, 'revision':request['revision'], 'container':container, 'volume':volume,
                'url':f"http://{request['listen']}:{request['port']}{request['path_base']}"}
    except Exception:
        if container_id: docker('rm', '-f', container_id, stdout=subprocess.DEVNULL)
        if built: subprocess.run(['docker','image','rm', image], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        raise


def dispatch(request):
    validate(request)
    action, name = request['action'], request['name']
    container, volume = 'messenger-'+name, 'messenger-'+name+'-data'
    if action == 'preflight': return preflight(request)
    if action == 'prepare':
        directory = Path(tempfile.mkdtemp(prefix='messenger-deploy-', dir='/tmp'))
        if 'SUDO_UID' in os.environ: os.chown(directory, int(os.environ['SUDO_UID']), int(os.environ['SUDO_GID']))
        return {'upload': str(directory/'bundle.tar.gz')}
    if action == 'discard': discard(request['upload']); return {'status':'discarded'}
    if action == 'deploy': return deploy(request)
    current = inspect('container', container)
    if action == 'status': return {'name':name,'state': current['State'] if current else 'absent', 'data_retained': bool(inspect('volume',volume))}
    if action == 'remove':
        data = inspect('volume',volume)  # Validate ownership before removing anything.
        images = docker('image','ls','--filter','label='+MANAGED+'=true','--filter','label='+INSTANCE+'='+name,
                        '--format','{{.Repository}}:{{.Tag}}', capture_output=True).stdout.splitlines()
        if current: docker('rm','-f',current['Id'],stdout=subprocess.DEVNULL)
        for image in images:
            if image != '<none>:<none>': docker('image','rm',image,stdout=subprocess.DEVNULL)
        if request.get('purge_data') and data: docker('volume','rm',volume,stdout=subprocess.DEVNULL)
        return {'name':name,'status':'removed','data_retained': bool(data) and not request.get('purge_data',False)}
    if not current: raise ValueError('Container not found')
    if action == 'logs':
        result = docker('logs','--tail','80',container,capture_output=True)
        return {'name':name,'logs':result.stdout+result.stderr}
    docker(action,container,stdout=subprocess.DEVNULL)
    return {'name':name,'status':action,'data_retained':True}


def main():
    import fcntl
    # Root or passwordless sudo; also serializes port/container/volume allocation.
    if os.geteuid() != 0: raise ValueError('Use root SSH or --sudo')
    with Path('/run/messenger-docker-deploy.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        print(json.dumps(dispatch(json.load(sys.stdin))))


if __name__ == '__main__':
    try: main()
    except Exception as error:
        print(str(error),file=sys.stderr)
        sys.exit(1)
