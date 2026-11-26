#!/usr/bin/env python3
"""Operator CLI: fetch a selected GitHub branch, build locally, deploy a Linux preview."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid

REPOSITORY = 'https://github.com/alex-pods/decentralized-messenger.git'
AGENT = '/usr/local/lib/messenger-preview/agent.py'


def run(*args, **kwargs):
    return subprocess.run(list(args), check=True, **kwargs)


def environment_id(branch):
    if not branch or len(branch) > 200 or branch.startswith('-') or any(ord(c) < 32 for c in branch):
        raise ValueError('Invalid branch')
    slug = re.sub('[^a-z0-9]+', '-', branch.lower()).strip('-')[:32] or 'branch'
    return slug + '-' + hashlib.sha256(branch.encode()).hexdigest()[:12]


def validate_host(host):
    if not re.fullmatch(r'(?:[A-Za-z0-9][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9.-]*', host):
        raise ValueError('Use a hostname or user@hostname; configure ports/keys in SSH config')
    return host


def remote(host, request):
    result = run('ssh', '-o', 'BatchMode=yes', validate_host(host), 'python3 ' + AGENT,
                 input=json.dumps(request), capture_output=True, text=True, encoding='utf-8')
    print(result.stdout.strip())


def build_branch(branch, directory):
    environment_id(branch)
    run('git', 'check-ref-format', '--branch', branch, capture_output=True)
    checkout = directory / 'source'
    run('git', 'clone', '--depth=1', '--single-branch', '--branch', branch, REPOSITORY, str(checkout))
    selected = subprocess.check_output(['git','-C',str(checkout),'symbolic-ref','--short','HEAD'],text=True).strip()
    if selected != branch:
        raise ValueError('Requested ref must be a branch, not a tag')
    revision = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], text=True).strip()
    if not re.fullmatch('[0-9a-f]{40}', revision):
        raise ValueError('Invalid revision')
    # Profiles are operator-owned. Never execute shell commands or manifests from a branch as root.
    projects = [('legacy', 'backend/MessengerBackend.csproj'),
                ('legacy', 'backend-legacy/MessengerBackend.csproj'),
                ('v2', 'backend-v2/Messenger.Api.csproj')]
    candidates = [(profile, path) for profile, path in projects if (checkout / path).is_file()]
    return checkout, revision, candidates


def deploy(args):
    if not args.accept_build:
        raise ValueError('Building executes code from the selected branch. Review it first, then pass --accept-build on a trusted build workstation.')
    with tempfile.TemporaryDirectory(prefix='messenger-preview-') as directory:
        root = Path(directory)
        checkout, revision, candidates = build_branch(args.branch, root)
        choices = [(profile, path) for profile, path in candidates if profile == args.profile]
        if len(choices) != 1:
            raise ValueError(f'Expected one {args.profile} project, found {choices}')
        profile, project = choices[0]
        output = root / 'publish'
        run('dotnet', 'publish', str(checkout / project), '-c', 'Release', '-r', 'linux-x64',
            '--self-contained', 'false', '-o', str(output), '--nologo')
        (output / 'REVISION').write_text(revision + '\n')
        archive = root / 'bundle.tar.gz'
        # Keep source without .git, credentials, obj/bin or generated data. Stop removes all of this.
        with tarfile.open(archive, 'w:gz') as bundle:
            for path in sorted(checkout.rglob('*')):
                rel = path.relative_to(checkout)
                if any(part in ('.git', 'bin', 'obj', '__pycache__') for part in rel.parts):
                    continue
                if path.is_symlink():
                    raise ValueError('Symlinks in source packages are not supported')
                if path.is_file(): bundle.add(path, arcname='source/' + rel.as_posix(), recursive=False)
            for path in sorted(output.rglob('*')):
                if path.is_file(): bundle.add(path, arcname='publish/' + path.relative_to(output).as_posix(), recursive=False)
        upload_id = uuid.uuid4().hex
        upload = '/var/lib/messenger-preview/incoming/' + upload_id + '.tar.gz'
        run('scp', '-q', str(archive), validate_host(args.host) + ':' + upload)
        try:
            remote(args.host, {'action': 'deploy', 'branch': args.branch, 'revision': revision,
                'profile': profile, 'upload': upload_id, 'bundle_sha256': hashlib.sha256(archive.read_bytes()).hexdigest()})
        finally:
            # Exact generated path; remove a failed upload even when installation failed.
            run('ssh', '-o', 'BatchMode=yes', args.host, 'rm -f -- ' + shlex.quote(upload))


def install(args):
    agent = Path(__file__).with_name('agent.py')
    token = uuid.uuid4().hex
    upload = '/tmp/messenger-preview-agent-' + token + '.py'
    run('scp', '-q', str(agent), validate_host(args.host) + ':' + upload)
    # Only install this operator-owned agent, never a script selected from the deployed branch.
    command = ('install -d -m 755 /usr/local/lib/messenger-preview && install -m 755 ' + upload +
               ' ' + AGENT + ' && rm -f ' + upload + ' && python3 ' + AGENT)
    result = run('ssh', '-o', 'BatchMode=yes', args.host, command,
                 input=json.dumps({'action':'install','base_url':args.base_url,'nginx_site':args.nginx_site}),
                 capture_output=True, text=True, encoding='utf-8')
    print(result.stdout.strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', required=True, help='SSH config host or user@host; root privileges required remotely')
    commands = parser.add_subparsers(dest='command', required=True)
    setup = commands.add_parser('install', help='Explicitly install/update the operator agent and nginx include')
    setup.add_argument('--base-url', required=True)
    setup.add_argument('--nginx-site', required=True)
    commands.add_parser('branches')
    commands.add_parser('list')
    deploy_parser = commands.add_parser('deploy')
    deploy_parser.add_argument('--branch', required=True)
    deploy_parser.add_argument('--profile', choices=['legacy','v2'], default='legacy')
    deploy_parser.add_argument('--accept-build', action='store_true', help='Authorize executing the selected source on this build machine')
    for name in ['status','stop','logs']:
        commands.add_parser(name).add_argument('environment')
    args = parser.parse_args()
    if args.command == 'branches':
        run('git','ls-remote','--heads',REPOSITORY)
    elif args.command == 'install': install(args)
    elif args.command == 'deploy': deploy(args)
    else: remote(args.host, {'action':args.command,'environment':getattr(args,'environment',None)})


if __name__ == '__main__':
    try: main()
    except (ValueError, OSError, subprocess.CalledProcessError) as e:
        print(str(e), file=sys.stderr)
        if isinstance(e, subprocess.CalledProcessError) and e.stderr: print(e.stderr, file=sys.stderr)
        sys.exit(1)
