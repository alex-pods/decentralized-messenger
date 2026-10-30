from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import Mock,patch
import migration


SQL='''CREATE TABLE schema_migrations(id TEXT PRIMARY KEY);
INSERT INTO schema_migrations VALUES('001');
CREATE TABLE users(id INTEGER PRIMARY KEY, tag TEXT NOT NULL UNIQUE);
CREATE TABLE messages(id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id),text TEXT NOT NULL);'''


class MigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.state=self.root/'target'; self.state.mkdir()
        self.source=self.root/'source.db'
        self.target=self.state/'messenger.db'
        for path in [self.source,self.target]:
            with closing(sqlite3.connect(path)) as c: c.executescript(SQL); c.commit()
        with closing(sqlite3.connect(self.source)) as c:
            c.execute("INSERT INTO users VALUES(1,'alice')")
            c.execute("INSERT INTO messages VALUES(1,1,'preserved message')"); c.commit()
        (self.root/'files').mkdir(); (self.root/'files/avatar').write_bytes(b'preserved file')
        (self.root/'keys').mkdir(); (self.root/'keys/key.xml').write_bytes(b'preserved key')
        (self.state/'old-marker').write_bytes(b'target backup')
        self.request={'name':'test','from_db':str(self.source),'apply':False,'source_stopped':True}
        container={'Id':'fixture','Mounts':[{'Name':'fixture-volume','Destination':'/state'}],
                   'Config':{'User':'1654:1654','Env':['MESSENGER_DB=/state/messenger.db','APP_DLL=MessengerBackend.dll']},
                   'State':{'Running':True},'HostConfig':{'PortBindings':{'8080/tcp':[{'HostPort':'18090'}]}}}
        self.inspect=lambda kind,name: container if kind=='container' else {'Name':'fixture-volume','Mountpoint':str(self.state)}
        self.docker=Mock(); self.docker.return_value.stdout='a'*40
        self.ready=Mock()
        self.backups=patch.object(migration,'BACKUPS',self.root/'backups'); self.backups.start(); self.addCleanup(self.backups.stop)
        self.owner=patch.object(migration,'secure_owner'); self.owner.start(); self.addCleanup(self.owner.stop)

    def run_import(self,**args): return migration.import_state({**self.request,**args},self.docker,self.inspect,self.ready)
    def users(self,path):
        with closing(sqlite3.connect(path)) as c: return c.execute('SELECT COUNT(*) FROM users').fetchone()[0]

    def test_dry_run_does_not_stop_or_change_target(self):
        result=self.run_import()
        self.assertTrue(result['compatible']); self.assertFalse(result['applied'])
        self.docker.assert_not_called(); self.assertEqual(self.users(self.target),0)
        self.assertFalse((self.root/'backups').exists())

    def test_schema_and_applied_migrations_must_match(self):
        with closing(sqlite3.connect(self.source)) as c:
            c.execute('ALTER TABLE users ADD COLUMN name TEXT'); c.execute("INSERT INTO schema_migrations VALUES('002')"); c.commit()
        result=self.run_import()
        self.assertFalse(result['compatible'])
        with self.assertRaisesRegex(ValueError,'schema_mismatch'): self.run_import(apply=True)
        self.docker.assert_not_called(); self.assertEqual(self.users(self.target),0)

    def test_nonempty_target_never_merged_or_replaced(self):
        with closing(sqlite3.connect(self.target)) as c: c.execute("INSERT INTO users VALUES(2,'bob')"); c.commit()
        with self.assertRaisesRegex(ValueError,'nonempty'): self.run_import(apply=True)
        self.docker.assert_not_called(); self.assertEqual(self.users(self.target),1)

    def test_apply_requires_explicit_stopped_source(self):
        with self.assertRaisesRegex(ValueError,'source-stopped'): self.run_import(apply=True,source_stopped=False)
        self.docker.assert_not_called()

    def test_identical_schema_imports_database_files_keys_and_keeps_backup(self):
        result=self.run_import(apply=True)
        self.assertTrue(result['applied'])
        self.assertEqual(self.users(self.target),1); self.assertEqual(self.users(self.source),1)
        self.assertEqual((self.state/'files/avatar').read_bytes(),b'preserved file')
        self.assertEqual((self.state/'keys/key.xml').read_bytes(),b'preserved key')
        backup=Path(result['backup'])
        self.assertEqual(self.users(backup/'before/messenger.db'),0)
        self.assertEqual((backup/'before/old-marker').read_bytes(),b'target backup')
        self.assertTrue((backup/'manifest.json').is_file()); self.ready.assert_called_once()

    def test_readiness_failure_restores_previous_state(self):
        self.ready.side_effect=[ValueError('wrong encryption key'),None]
        with self.assertRaisesRegex(ValueError,'previous state restored'): self.run_import(apply=True)
        self.assertEqual(self.users(self.target),0); self.assertEqual(self.users(self.source),1)
        self.assertEqual((self.state/'old-marker').read_bytes(),b'target backup')
        self.assertFalse((self.state/'files/avatar').exists()); self.assertEqual(self.ready.call_count,2)

    def test_snapshot_includes_committed_wal_content(self):
        with closing(sqlite3.connect(self.source)) as writer:
            writer.execute('PRAGMA journal_mode=WAL')
            writer.execute("INSERT INTO users VALUES(2,'wal-user')"); writer.commit()
            self.assertTrue(Path(str(self.source)+'-wal').exists())
            snapshot=self.root/'snapshot.db'
            migration.snapshot(self.source,snapshot)
            self.assertEqual(self.users(snapshot),2)

    def test_same_database_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'overlap'): self.run_import(from_db=str(self.target))
        self.docker.assert_not_called()

    def test_encrypted_database_requires_keys(self):
        for path in [self.source,self.target]:
            with closing(sqlite3.connect(path)) as c:
                c.execute('CREATE TABLE storage_metadata(id INTEGER PRIMARY KEY,key_probe TEXT)')
                c.execute("INSERT INTO storage_metadata VALUES(1,'encrypted probe')"); c.commit()
        (self.root/'keys/key.xml').unlink()
        result=self.run_import()
        self.assertFalse(result['compatible']); self.assertIn('DataProtection',result['errors'][0])
        self.docker.assert_not_called()

    def test_target_created_user_during_staging_is_not_lost(self):
        original=migration.snapshot
        def snapshot(source,destination):
            original(source,destination)
            with closing(sqlite3.connect(self.target)) as c: c.execute("INSERT INTO users VALUES(3,'new-user')"); c.commit()
        with patch.object(migration,'snapshot',side_effect=snapshot):
            with self.assertRaisesRegex(ValueError,'no target changes'): self.run_import(apply=True)
        self.assertEqual(self.users(self.target),1); self.docker.assert_not_called()


if __name__=='__main__': unittest.main()
