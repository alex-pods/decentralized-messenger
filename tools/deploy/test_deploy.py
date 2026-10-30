import argparse
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import deploy
import remote


class DeployTests(unittest.TestCase):
    def test_ssh_key_and_host_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            key=Path(directory)/'key with spaces'; key.touch()
            args=argparse.Namespace(host='alice@example.org',ssh_port=2222,key=str(key),sudo=True,ask_password=False)
            ssh=deploy.SSH(args)
            with patch('subprocess.run') as run:
                run.return_value.stdout='{}'; run.return_value.returncode=0
                ssh.call({'action':'status','name':'test'})
                self.assertIn(str(key.resolve()),run.call_args.args[0])
                self.assertIn('StrictHostKeyChecking=yes',run.call_args.args[0])
            args.host='-oProxyCommand=evil'
            with self.assertRaises(ValueError): deploy.SSH(args)

    def test_repo_rejects_secrets_and_local_transport(self):
        for value in ['file:///tmp/repo','ext::sh evil','https://user:token@host/repo','https://host/repo?secret=x']:
            with self.subTest(value=value),self.assertRaises(ValueError): deploy.repository(value)
        self.assertEqual(deploy.repository(deploy.DEFAULT_REPO),deploy.DEFAULT_REPO)
        self.assertNotEqual(deploy.instance_name('https://a/repo','dev'),deploy.instance_name('https://b/repo','dev'))

    def test_archive_rejects_escape_links_and_duplicates(self):
        for names,kind in [(['../outside'],tarfile.REGTYPE),(['/outside'],tarfile.REGTYPE),
                            (['a\\b'],tarfile.REGTYPE),(['link'],tarfile.SYMTYPE),(['link'],tarfile.LNKTYPE),(['same','same'],tarfile.REGTYPE)]:
            with self.subTest(names=names),tempfile.TemporaryDirectory() as directory:
                root=Path(directory); archive=root/'bundle.tar.gz'; output=root/'out'; output.mkdir()
                with tarfile.open(archive,'w:gz') as bundle:
                    for name in names:
                        item=tarfile.TarInfo(name); item.type=kind; bundle.addfile(item,io.BytesIO())
                with self.assertRaises(ValueError): remote.unpack(archive,output)

    def test_foreign_container_cannot_be_removed(self):
        with patch('subprocess.run') as run:
            run.return_value.returncode=0
            run.return_value.stdout=json.dumps([{'Config':{'Labels':{}},'Id':'foreign'}])
            with self.assertRaisesRegex(ValueError,'unmanaged'): remote.dispatch({'action':'remove','name':'test','purge_data':True})
            self.assertEqual(run.call_count,1)

    def test_remove_preserves_volume_until_explicit_purge(self):
        for purge in [False,True]:
            with self.subTest(purge=purge),patch.object(remote,'inspect',side_effect=[{'Id':'container-id'},{'Labels':{remote.MANAGED:'true'}}]),patch.object(remote,'docker') as docker:
                docker.return_value.stdout='messenger-deploy/test:revision\n'
                result=remote.dispatch({'action':'remove','name':'test','purge_data':purge})
                self.assertEqual(result['data_retained'],not purge)
                calls=[c.args for c in docker.call_args_list]
                self.assertEqual(any(c[:2]==('volume','rm') for c in calls),purge)

    def test_changed_revision_cannot_use_existing_volume(self):
        req={'name':'test','repo':'https://host/repo','branch':'dev','revision':'a'*40}
        with patch.object(remote,'preflight'),patch.object(remote,'inspect',return_value={'Labels':{remote.SOURCE:'different'}}),patch.object(remote,'docker') as docker:
            with self.assertRaisesRegex(ValueError,'different source/revision'): remote.deploy(req)
            docker.assert_not_called()

    def test_input_cannot_inject_docker_options(self):
        req={'action':'preflight','name':'test','port':18090,'listen':'127.0.0.1','path_base':'/api/dev'}
        remote.validate(req)
        for field,value in [('name','--privileged'),('name','../other'),('port',True),('path_base','/bad\nvalue'),('listen','host;evil')]:
            with self.subTest(field=field),self.assertRaises(ValueError): remote.validate({**req,field:value})

    def test_upload_cleanup_bounds(self):
        for value in ['/etc/bundle.tar.gz','/tmp/messenger-deploy-x/../../bundle.tar.gz']:
            with self.assertRaises(ValueError): remote.upload_path(value)

    def test_failed_readiness_removes_only_created_container_and_keeps_volume(self):
        with tempfile.TemporaryDirectory() as directory:
            archive=Path(directory)/'bundle'; archive.write_bytes(b'fake')
            req={'name':'test','repo':'https://host/repo','branch':'dev','revision':'a'*40,
                 'port':18090,'listen':'127.0.0.1','path_base':'','upload':'unused',
                 'sha256':__import__('hashlib').sha256(b'fake').hexdigest()}
            def unpack(_archive,out): (out/'REVISION').write_text('a'*40)
            with patch.object(remote,'preflight'),patch.object(remote,'inspect',return_value=None),patch.object(remote,'upload_path',return_value=archive),patch.object(remote,'unpack',side_effect=unpack),patch.object(remote,'docker') as docker,patch.object(remote,'wait_ready',side_effect=ValueError('readiness')),patch('subprocess.run'):
                docker.return_value.stdout='created-id'
                with self.assertRaisesRegex(ValueError,'readiness'): remote.deploy(req)
                calls=[c.args for c in docker.call_args_list]
                self.assertIn(('rm','-f','created-id'),calls)
                self.assertFalse(any(c[:2]==('volume','rm') for c in calls))


if __name__=='__main__': unittest.main()
