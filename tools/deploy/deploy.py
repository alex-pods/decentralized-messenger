#!/usr/bin/env python3
"""Deploy a selected Messenger branch in Docker over OpenSSH."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tarfile
import tempfile
import zlib
from urllib.parse import urlsplit

DEFAULT_REPO = 'https://github.com/alex-pods/decentralized-messenger.git'


def repository(value):
    url = urlsplit(value)
    host = bool(url.hostname and re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]*', url.hostname))
    user = url.username is None or bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', url.username))
    https = (url.scheme == 'https' and host and '@' not in url.netloc
             and not url.query and not url.fragment)
    ssh = (url.scheme == 'ssh' and host and user and not url.password
           and not url.query and not url.fragment)
    scp = re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*@[A-Za-z0-9][A-Za-z0-9.-]*:[A-Za-z0-9_./-]+', value)
    if any(c.isspace() or ord(c) < 32 for c in value) or not (https or ssh or scp):
        raise ValueError('Repository must be an HTTPS or SSH Git URL, without embedded passwords/tokens')
    return value


def branch_name(value):
    if not value or len(value) > 200 or value.startswith('-') or any(ord(c) < 32 for c in value):
        raise ValueError('Invalid branch')
    subprocess.run(['git', 'check-ref-format', '--branch', value], check=True, capture_output=True)
    return value


def instance_name(repo, branch):
    slug = re.sub('[^a-z0-9]+', '-', branch.lower()).strip('-')[:24] or 'branch'
    return slug + '-' + hashlib.sha256((repo + '\n' + branch).encode()).hexdigest()[:10]


class SSH:
    def __init__(self, args):
        if not re.fullmatch(r'(?:[A-Za-z0-9][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9_.-]*', args.host or ''):
            raise ValueError('Use an SSH config alias or user@hostname')
        if args.ssh_port is not None and not 1 <= args.ssh_port <= 65535:
            raise ValueError('Invalid SSH port')
        self.host, self.sudo = args.host, args.sudo
        self.options = ['-o', 'BatchMode=' + ('no' if args.ask_password else 'yes'),
                        '-o', 'ConnectTimeout=15', '-o', 'StrictHostKeyChecking=yes']
        self.port = args.ssh_port
        if args.key:
            key = Path(args.key).expanduser().resolve(strict=True)
            if not key.is_file(): raise ValueError('SSH key must be a file')
            self.options += ['-i', str(key), '-o', 'IdentitiesOnly=yes']

    def call(self, request):
        source = Path(__file__).with_name('remote.py').read_text(encoding='utf-8')
        # Keep the SSH command below Windows' command-line limit without leaving a remote helper file.
        payload = base64.b64encode(zlib.compress(source.encode('utf-8'))).decode('ascii')
        bootstrap = "import base64,zlib;exec(compile(zlib.decompress(base64.b64decode('" + payload + "')),'<messenger-deploy>','exec'))"
        command = ('sudo -n ' if self.sudo else '') + 'python3 -c ' + shlex.quote(bootstrap)
        port = ['-p', str(self.port)] if self.port is not None else []
        result = subprocess.run(['ssh', *self.options, *port, self.host, command],
                                input=json.dumps(request), text=True, encoding='utf-8',
                                stdout=subprocess.PIPE)
        if result.returncode:
            raise ValueError(f"SSH operation {request['action']} failed (exit {result.returncode}); see remote error above")
        return json.loads(result.stdout)

    def upload(self, path, target):
        # target is an agent-generated path, never caller-controlled shell text.
        if not re.fullmatch(r'/tmp/messenger-deploy-[a-z0-9_]+/bundle.tar.gz', target):
            raise ValueError('Invalid upload path returned by server')
        port = ['-P', str(self.port)] if self.port is not None else []
        subprocess.run(['scp', *self.options, *port, str(path), self.host + ':' + target], check=True)


def build(args, root, runtime):
    checkout = root / 'source'
    # Disable local Git hooks and reject file/ext transports; submodules are not initialized.
    subprocess.run(['git', '-c', 'core.hooksPath=/dev/null', '-c', 'protocol.file.allow=never',
                    '-c', 'protocol.ext.allow=never', 'clone', '--depth=1', '--single-branch',
                    '--branch', args.branch, '--', args.repo, str(checkout)], check=True)
    selected = subprocess.check_output(['git', '-C', str(checkout), 'symbolic-ref', '--short', 'HEAD'], text=True).strip()
    if selected != args.branch:
        raise ValueError('Select a branch, not a tag')
    revision = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip()
    if not re.fullmatch('[0-9a-f]{40}', revision): raise ValueError('Invalid Git revision')
    if args.project:
        relative = Path(args.project)
        if relative.is_absolute() or '..' in relative.parts or '\\' in args.project:
            raise ValueError('--project must be relative to the repository')
        candidates = [checkout / relative]
    else:
        candidates = [checkout / p for p in ['backend/MessengerBackend.csproj', 'backend-legacy/MessengerBackend.csproj'] if (checkout / p).is_file()]
    if len(candidates) != 1 or candidates[0].suffix != '.csproj':
        raise ValueError('Expected one Messenger backend project; specify --project for a compatible fork')
    project = candidates[0].resolve(strict=True)
    if not project.is_relative_to(checkout.resolve()): raise ValueError('Project escapes checkout')
    publish = root / 'publish'
    subprocess.run(['dotnet', 'publish', str(project), '-c', 'Release', '-r', runtime,
                    '--self-contained', 'false', '-p:UseAppHost=false', '-o', str(publish), '--nologo'], check=True)
    if not (publish / 'MessengerBackend.dll').is_file():
        raise ValueError('Compatible project must publish MessengerBackend.dll')
    (publish / 'REVISION').write_text(revision + '\n', encoding='utf-8')
    archive = root / 'bundle.tar.gz'
    with tarfile.open(archive, 'w:gz') as bundle:
        for path in sorted(publish.rglob('*')):
            if path.is_symlink(): raise ValueError('Published symlinks are not supported')
            if path.is_file():
                if path.name == 'Dockerfile': raise ValueError('Reserved published file: Dockerfile')
                info = bundle.gettarinfo(str(path), arcname=path.relative_to(publish).as_posix())
                info.mode = 0o644
                info.uid = info.gid = 0
                info.uname = info.gname = ''
                with path.open('rb') as stream: bundle.addfile(info, stream)
        bundle.add(Path(__file__).resolve().parents[2] / 'backend/Dockerfile', arcname='Dockerfile')
    return archive, revision


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', help='SSH config alias or user@host (root, or --sudo with passwordless sudo)')
    parser.add_argument('--ssh-port', type=int, help='Override SSH config port (otherwise OpenSSH defaults apply)')
    parser.add_argument('--key', help='Private key path; otherwise use SSH config/agent')
    parser.add_argument('--ask-password', action='store_true', help='Allow native OpenSSH password/passphrase prompts; requires a terminal')
    parser.add_argument('--sudo', action='store_true', help='Use passwordless sudo remotely')
    commands = parser.add_subparsers(dest='command', required=True)
    branches = commands.add_parser('branches', help='List branches without deploying')
    branches.add_argument('--repo', type=repository, default=DEFAULT_REPO)
    deploy = commands.add_parser('deploy')
    deploy.add_argument('--repo', type=repository, default=DEFAULT_REPO)
    deploy.add_argument('--branch', type=branch_name, required=True)
    deploy.add_argument('--name', help='Instance name; defaults to repository + branch hash')
    deploy.add_argument('--project', help='Compatible Messenger .csproj path relative to repository')
    deploy.add_argument('--port', type=int, default=18090, help='Backend TCP port (not the SSH port)')
    deploy.add_argument('--listen', choices=['127.0.0.1', '0.0.0.0'], default='127.0.0.1')
    deploy.add_argument('--path-base', default='', help='Optional proxy prefix, e.g. /api/dev')
    deploy.add_argument('--accept-build', action='store_true', help='Execute reviewed branch code on this build machine')
    for action in ['status', 'logs', 'start', 'stop', 'remove']:
        cmd = commands.add_parser(action)
        cmd.add_argument('--name', required=True)
        if action == 'remove': cmd.add_argument('--purge-data', action='store_true', help='Also permanently delete this instance database, files and keys')
    args = parser.parse_args()
    if args.command == 'branches':
        subprocess.run(['git', '-c', 'protocol.file.allow=never', '-c', 'protocol.ext.allow=never', 'ls-remote', '--heads', '--', args.repo], check=True)
        return
    ssh = SSH(args)
    if args.command != 'deploy':
        print(json.dumps(ssh.call({'action': args.command, 'name': args.name, 'purge_data': getattr(args, 'purge_data', False)}), ensure_ascii=False, indent=2))
        return
    if not args.accept_build: raise ValueError('Review the selected branch and pass --accept-build; building executes its code locally')
    name = args.name or instance_name(args.repo, args.branch)
    request = {'action': 'preflight', 'name': name, 'repo': args.repo, 'branch': args.branch,
               'port': args.port, 'listen': args.listen, 'path_base': args.path_base}
    info = ssh.call(request)
    with tempfile.TemporaryDirectory(prefix='messenger-deploy-') as directory:
        archive, revision = build(args, Path(directory), info['runtime'])
        upload = ssh.call({'action': 'prepare', 'name': name})['upload']
        try:
            ssh.upload(archive, upload)
            digest = hashlib.sha256()
            with archive.open('rb') as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''): digest.update(block)
            request.update(action='deploy', upload=upload, revision=revision,
                           sha256=digest.hexdigest())
            print(json.dumps(ssh.call(request), ensure_ascii=False, indent=2))
        finally:
            ssh.call({'action': 'discard', 'name': name, 'upload': upload})


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
