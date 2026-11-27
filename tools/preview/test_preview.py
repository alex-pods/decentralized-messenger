import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import agent
import preview


class ValidationTests(unittest.TestCase):
    def test_branch_slug(self):
        self.assertEqual(preview.environment_id('dev'),agent.identifier('dev'))
        self.assertNotEqual(preview.environment_id('feature/chats'),preview.environment_id('feature-chats'))
        self.assertLessEqual(len(preview.environment_id('x'*200)),45)
        for branch in ['', '-bad', 'bad\nref']:
            with self.assertRaises(ValueError): preview.environment_id(branch)

    def test_host_no_shell(self):
        self.assertEqual(preview.validate_host('root@176.53.160.4'),'root@176.53.160.4')
        for host in ['-oProxyCommand=x','-o@host','host;touch /x','root@host\ncmd']:
            with self.assertRaises(ValueError): preview.validate_host(host)

    def archive(self,directory,name,link=False):
        archive=Path(directory)/'upload.tar.gz'
        with tarfile.open(archive,'w:gz') as bundle:
            info=tarfile.TarInfo(name)
            if link:
                info.type=tarfile.SYMTYPE; info.linkname='/etc/passwd'; bundle.addfile(info)
            else:
                info.size=2; bundle.addfile(info,io.BytesIO(b'ok'))
        return archive

    def test_archive_traversal_and_symlinks(self):
        with tempfile.TemporaryDirectory() as d:
            for name,link in [('../escape',False),('/etc/test',False),('publish/link',True),('other/file',False),('publish\\bad',False)]:
                with self.subTest(name=name),self.assertRaises(ValueError):
                    agent.unpack(self.archive(d,name,link),Path(d)/'out')
            agent.unpack(self.archive(d,'publish/MessengerBackend.dll'),Path(d)/'valid')
            self.assertEqual((Path(d)/'valid/publish/MessengerBackend.dll').read_bytes(),b'ok')

    def test_cleanup_does_not_follow_links(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); (root/'code').mkdir(); (root/'other').mkdir()
            with self.assertRaises(ValueError): agent.safe_remove(root/'other',root/'code')

    def test_stop_preserves_state_and_removes_code(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); code=root/'code'; code.mkdir(); routes=root/'routes'; routes.mkdir()
            units=root/'units'; units.mkdir(); state=root/'data'; state.mkdir()
            (state/'database').write_bytes(b'private')
            eid=agent.identifier('dev'); (code/eid).mkdir(); (code/eid/'source.cs').write_text('code')
            (units/agent.unit_name(eid)).write_text('unit')
            registry={eid:{'environment':eid,'status':'running','state_directory':str(state)}}
            calls=[]
            with patch.object(agent,'CODE',code),patch.object(agent,'ROUTES',routes),patch.object(agent,'REGISTRY',root/'registry.json'),\
                 patch.object(agent,'unit_path',lambda e:units/agent.unit_name(e)),\
                 patch.object(agent,'run',lambda *a,**k:calls.append(a)):
                result=agent.stop(eid,registry)
                agent.stop(eid,registry)  # idempotent
            self.assertTrue(result['database_files_keys_retained'])
            self.assertEqual((state/'database').read_bytes(),b'private')
            self.assertFalse((code/eid).exists())
            self.assertIn('return 503',(routes/(eid+'.conf')).read_text())
            self.assertIn(('systemctl','stop',agent.unit_name(eid)),calls)

    def test_nginx_validation_failure_restores_route(self):
        with tempfile.TemporaryDirectory() as d:
            routes=Path(d); eid=agent.identifier('dev'); file=routes/(eid+'.conf'); file.write_text('old')
            attempts=[]
            def run(*args):
                attempts.append(args)
                if len(attempts)==1: raise subprocess.CalledProcessError(1,args)
            with patch.object(agent,'ROUTES',routes),patch.object(agent,'run',run):
                with self.assertRaises(subprocess.CalledProcessError): agent.set_route(eid,'new')
            self.assertEqual(file.read_text(),'old')

    def test_existing_data_cannot_be_silently_upgraded(self):
        eid=agent.identifier('dev')
        registry={eid:{'status':'stopped','revision':'a'*40}}
        with self.assertRaisesRegex(ValueError,'different commit'):
            agent.deploy({'branch':'dev','profile':'legacy','revision':'b'*40},registry,{})

    def test_running_environment_is_not_overwritten(self):
        eid=agent.identifier('dev')
        with self.assertRaisesRegex(ValueError,'running'):
            agent.deploy({'branch':'dev','profile':'legacy','revision':'a'*40},{eid:{'status':'running'}},{})

    def test_v2_fails_before_mutations(self):
        with self.assertRaisesRegex(ValueError,'v2 is not deployable'):
            agent.deploy({'branch':'dev','profile':'v2','revision':'a'*40},{},{})


if __name__=='__main__': unittest.main()
