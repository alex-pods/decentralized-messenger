"""Cold, schema-identical SQLite state import. Never merges or guesses schema conversions."""
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import stat
import subprocess
import tempfile

BACKUPS = Path('/var/backups/messenger-deploy')
METADATA = {'schema_migrations', 'storage_metadata', 'storage_file_migrations', 'storage_maintenance'}


def quote(name): return '"' + name.replace('"', '""') + '"'


def connect(path):
    connection = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=15)
    connection.execute('PRAGMA query_only=ON')
    return connection


def schema(connection):
    # Preserve string literals. Formatting differences in CREATE statements are harmless.
    normalize = lambda value: re.sub(r''''(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|\[[^\]]*\]|\s+''',
                                     lambda m: '' if m[0].isspace() else m[0], value or '')
    return {(kind, name): normalize(sql) for kind, name, sql in connection.execute(
        "SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name")}


def counts(connection):
    return {name: connection.execute('SELECT COUNT(*) FROM '+quote(name)).fetchone()[0]
            for name, in connection.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")}


def inspect_databases(source, target):
    with closing(connect(source)) as src, closing(connect(target)) as dst:
        a, b = schema(src), schema(dst)
        source_counts, target_counts = counts(src), counts(dst)
        errors = []
        if a != b: errors.append('schema_mismatch: an explicit data converter is required')
        if bool(source_counts.get('storage_metadata')) != bool(target_counts.get('storage_metadata')):
            errors.append('Storage encryption formats differ; automatic decryption/downgrade is not supported')
        if src.execute('PRAGMA user_version').fetchone() != dst.execute('PRAGMA user_version').fetchone():
            errors.append('SQLite user_version differs')
        if 'schema_migrations' in source_counts and 'schema_migrations' in target_counts:
            if src.execute('SELECT id FROM schema_migrations ORDER BY id').fetchall() != dst.execute('SELECT id FROM schema_migrations ORDER BY id').fetchall():
                errors.append('Applied migrations differ')
        for label, connection in [('source', src), ('target', dst)]:
            if connection.execute('PRAGMA integrity_check').fetchall() != [('ok',)]: errors.append(label+' integrity check failed')
            if connection.execute('PRAGMA foreign_key_check').fetchall(): errors.append(label+' foreign key check failed')
        if any(value for name,value in target_counts.items() if name not in METADATA):
            errors.append('Target contains application data; merging/replacing a nonempty database is not supported')
        changed = sorted(kind+':'+name for kind,name in a.keys() | b.keys() if a.get((kind,name)) != b.get((kind,name)))
        return {'compatible': not errors, 'errors':errors, 'changed_schema_objects':changed,
                'source_rows':source_counts, 'target_rows':target_counts,
                'source_encrypted':bool(source_counts.get('storage_metadata'))}


def safe_tree(root):
    if not root.exists(): return 0
    if root.is_symlink() or not root.is_dir(): raise ValueError('Expected a real state directory')
    size = 0
    for entry in root.rglob('*'):
        mode = entry.lstat().st_mode
        if not (stat.S_ISREG(mode) or stat.S_ISDIR(mode)):
            raise ValueError('State symlinks/devices are not supported')
        if stat.S_ISREG(mode): size += entry.stat().st_size
    return size


def validate_files(source, files, keys, encrypted):
    safe_tree(files); safe_tree(keys)
    if encrypted and (not keys.is_dir() or not list(keys.glob('*.xml'))):
        raise ValueError('Encrypted database requires its original DataProtection keys directory')
    with closing(connect(source)) as c:
        tables = counts(c)
        for table, column in [('users','path_to_avatar_file'),('attachments','path_to_file'),('avatars','path'),('attachments','storage_path')]:
            if table not in tables: continue
            columns = {row[1] for row in c.execute('PRAGMA table_info('+quote(table)+')')}
            if column not in columns: continue
            for value, in c.execute('SELECT '+quote(column)+' FROM '+quote(table)+' WHERE '+quote(column)+' IS NOT NULL'):
                relative = Path(value)
                resolved = (files / relative).resolve()
                if relative.is_absolute() or not resolved.is_relative_to(files.resolve()) or not resolved.is_file():
                    raise ValueError('Missing or unsafe referenced file; database import refused')


def snapshot(source, destination):
    # SQLite backup includes committed WAL content; copying the .db file alone would not.
    with closing(connect(source)) as src, closing(sqlite3.connect(destination)) as dst:
        src.backup(dst)
    os.chmod(destination,0o600)


def secure_owner(root):
    for entry in [root, *root.rglob('*')]:
        os.chmod(entry,0o700 if entry.is_dir() else 0o600)
        os.chown(entry,1654,1654,follow_symlinks=False)


def move_contents(source, destination):
    destination.mkdir(mode=0o700,parents=True,exist_ok=True)
    for entry in list(source.iterdir()):
        if (destination/entry.name).exists(): raise ValueError('Import destination is not empty')
        shutil.move(str(entry),str(destination/entry.name))


def import_state(request, docker, inspect, wait_ready):
    name=request['name']
    container=inspect('container','messenger-'+name)
    volume=inspect('volume','messenger-'+name+'-data')
    if not container or not volume: raise ValueError('Import requires an installed, managed target instance')
    root=Path(volume['Mountpoint']).resolve(strict=True)
    source=Path(request['from_db']).expanduser()
    if not source.is_absolute(): raise ValueError('--from-db must be an absolute path on the SSH server')
    source=source.resolve(strict=True)
    if not source.is_file(): raise ValueError('Source database is not a regular file')
    target=root/'messenger.db'
    for key in ['from_files','from_keys']:
        if request.get(key) and not Path(request[key]).is_absolute(): raise ValueError('--'+key.replace('_','-')+' must be an absolute server path')
    files=Path(request.get('from_files') or source.parent/'files').resolve()
    keys=Path(request.get('from_keys') or source.parent/'keys').resolve()
    if source.is_relative_to(root) or source.samefile(target): raise ValueError('Source and target overlap')
    for path in [files,keys]:
        if path == path.parent or path.is_relative_to(root) or root.is_relative_to(path) or source.is_relative_to(path):
            raise ValueError('Source file/key directory overlaps database or target')
    mounts=container.get('Mounts',[])
    if not any(m.get('Name')==volume['Name'] and m.get('Destination')=='/state' for m in mounts):
        raise ValueError('Target container does not mount the expected state volume')
    env=dict(value.split('=',1) for value in container['Config']['Env'] if '=' in value)
    if env.get('MESSENGER_DB') != '/state/messenger.db': raise ValueError('Unexpected target database path')
    if container['Config'].get('User') not in ['1654','1654:1654']: raise ValueError('Unexpected target state owner')
    if env.get('APP_DLL','MessengerBackend.dll') not in ['MessengerBackend.dll','Messenger.Api.dll']:
        raise ValueError('Unsupported target application')
    if env.get('APP_DLL')=='Messenger.Api.dll' and env.get('Db')!='/state/messenger.db':
        raise ValueError('Unexpected v2 database path')
    report=inspect_databases(source,target)
    report.update(applied=False, name=name)
    if not report['compatible']:
        if request.get('apply'): raise ValueError('; '.join(report['errors']))
        return report
    try:
        validate_files(source,files,keys,report['source_encrypted'])
        safe_tree(root)
    except ValueError as error:
        if request.get('apply'): raise
        report.update(compatible=False,errors=[str(error)])
        return report
    if not request.get('apply'): return report
    if not request.get('source_stopped'):
        raise ValueError('Stop the source writer first, then pass --source-stopped; source service is never stopped implicitly')
    backup_root=BACKUPS/name
    backup_root.mkdir(mode=0o700,parents=True,exist_ok=True)
    os.chmod(BACKUPS,0o700); os.chmod(backup_root,0o700)
    with closing(connect(source)) as c:
        database_size=c.execute('PRAGMA page_count').fetchone()[0]*c.execute('PRAGMA page_size').fetchone()[0]
    required=database_size+safe_tree(files)+safe_tree(keys)+safe_tree(root)
    if shutil.disk_usage(backup_root).free < required*2+64*1024**2: raise ValueError('Insufficient backup/staging space')
    stamp=datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ-')
    backup=Path(tempfile.mkdtemp(prefix=stamp,dir=backup_root))
    stage=backup/'staged'; stage.mkdir(mode=0o700)
    snapshot(source,stage/'messenger.db')
    snapshot_digest=hashlib.sha256((stage/'messenger.db').read_bytes()).hexdigest()
    for src,folder in [(files,'files'),(keys,'keys')]:
        if src.is_dir(): shutil.copytree(src,stage/folder)
    # Inspect the snapshot too, then recheck target after stopping to avoid clobbering new users.
    if not inspect_databases(stage/'messenger.db',target)['compatible']: raise ValueError('Source changed during staging; no target changes applied')
    was_running=container['State']['Running']
    profile='v2' if env.get('APP_DLL')=='Messenger.Api.dll' else 'legacy'
    bindings=container['HostConfig']['PortBindings'].get('8080/tcp',[])
    if len(bindings)!=1: raise ValueError('Unable to determine target health port')
    revision=docker('exec',container['Id'],'cat','/app/REVISION',capture_output=True).stdout.strip() if was_running else None
    if not was_running: raise ValueError('Start the target before import so readiness and rollback can be verified')
    health={'port':int(bindings[0]['HostPort']),'path_base':env.get('MESSENGER_PATH_BASE',''),'profile':profile,'revision':revision}
    before=backup/'before'
    modified=False
    docker('stop',container['Id'],stdout=subprocess.DEVNULL)
    try:
        if not inspect_databases(stage/'messenger.db',target)['compatible']: raise ValueError('Target changed before stop; import refused')
        safe_tree(root)
        shutil.copytree(root,before)
        # Both copies contain only validated files/dirs; never rename Docker's volume mountpoint.
        modified=True
        move_contents(root,backup/'replaced')
        move_contents(stage,root)
        secure_owner(root)
        docker('start',container['Id'],stdout=subprocess.DEVNULL)
        wait_ready(health)
        with closing(connect(target)) as c:
            if c.execute('PRAGMA integrity_check').fetchall()!=[('ok',)] or c.execute('PRAGMA foreign_key_check').fetchall():
                raise ValueError('Imported database validation failed')
        report.update(applied=True,backup=str(backup))
        (backup/'manifest.json').write_text(json.dumps({'source':str(source),'snapshot_sha256':snapshot_digest,'result':report},indent=2))
        os.chmod(backup/'manifest.json',0o600)
        return report
    except Exception as error:
        docker('stop',container['Id'],stdout=subprocess.DEVNULL)
        if modified:
            move_contents(root,backup/'failed')
            shutil.copytree(before,root,dirs_exist_ok=True)
            secure_owner(root)
        docker('start',container['Id'],stdout=subprocess.DEVNULL)
        wait_ready(health)
        raise ValueError('Import failed; previous state restored. Backup: '+str(backup)+'. '+str(error)) from error
