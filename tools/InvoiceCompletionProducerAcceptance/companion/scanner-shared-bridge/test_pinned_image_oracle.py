import stat
import sys
import types
import unittest
from unittest.mock import patch
import pinned_image_oracle as module
from borrowed_scanner_bridge import BridgeRefused


HELD_ORACLE_SOURCE = None
ORACLE_CONTROL_SOURCE_SHA = 'da82d89cf595e9e2350f9a15c5e08b61f87c300bc1521d9ed4542400b21f3c5f'


class OracleSourceControls(unittest.TestCase):
    def stage_model(self):
        owner = self.model()
        owner.fd = 900001  # Mock-only, never allocated.
        owner.file_identity = (1, 101)
        owner.directory_identity = (1, 100)
        owner.stage_path = types.SimpleNamespace(lstat=lambda: self.snapshot(202, 4))
        owner.path = types.SimpleNamespace(lstat=lambda: self.snapshot(101, 0))
        owner.directory = types.SimpleNamespace(lstat=lambda: self.snapshot(100, 0, stat.S_IFDIR | 0o700))
        owner.stage_write_attempted = owner.stage_writer_settled = True
        return owner

    def snapshot(self, inode, size, mode=stat.S_IFREG | 0o600, links=1):
        return types.SimpleNamespace(st_dev=1, st_ino=inode, st_size=size, st_mode=mode,
                                     st_nlink=links, st_mtime_ns=123)

    def stage_patches(self, owner, read=None, write=None, stat_fn=None):
        from contextlib import ExitStack
        stack = ExitStack()
        runner = types.SimpleNamespace(command_receipts=lambda: ({'cleanupVerified': True, 'quarantined': False},))
        stack.enter_context(patch.dict(sys.modules, {'scanner_docker_command': runner}))
        for flag, value in [('O_NOFOLLOW', 65536), ('O_CLOEXEC', 524288), ('O_NONBLOCK', 2048)]:
            stack.enter_context(patch.object(module.os, flag, getattr(module.os, flag, value), create=True))
        def acquire(path, flags):
            self.assertIs(path, owner.stage_path)
            self.assertTrue(flags & module.os.O_NOFOLLOW and flags & module.os.O_NONBLOCK)
            return 900002
        stack.enter_context(patch.object(module.os, 'open', side_effect=acquire))
        stack.enter_context(patch.object(module.os, 'lseek'))
        stack.enter_context(patch.object(module.os, 'fstat', side_effect=stat_fn or (lambda fd: self.snapshot(202, 4) if fd==900002 else self.snapshot(101, 0))))
        stack.enter_context(patch.object(module.os, 'read', side_effect=read or [b'data', b'']))
        stack.enter_context(patch.object(module.os, 'write', side_effect=write or [2, 2]))
        return stack

    def test_actual_original_guard_rejects_archive_replacement_but_preserves_same_inode(self):
        import ast
        source = HELD_ORACLE_SOURCE
        if source is None:
            import hashlib
            from pathlib import Path
            source = (Path(__file__).parent / 'pinned_image_oracle.py').read_bytes()
            self.assertLessEqual(len(source), 65536)
            self.assertEqual(hashlib.sha256(source).hexdigest(), ORACLE_CONTROL_SOURCE_SHA)
        tree = ast.parse(source)
        guard = next(n for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Name)
                     and n.func.id == 'require' and len(n.args)==2 and isinstance(n.args[1], ast.Constant)
                     and n.args[1].value == 'Owned finite image file differs')
        expression = compile(ast.Expression(body=guard.args[0]), '<held-original-identity-guard>', 'eval')
        owner = self.stage_model()
        env={'stat':stat, 'self':owner, 'LIMIT':module.LIMIT,
             'current':self.snapshot(101,0),'linked':self.snapshot(202,4)}
        self.assertFalse(eval(expression, env))
        env.update(current=self.snapshot(101,4), linked=self.snapshot(101,4))
        self.assertTrue(eval(expression, env))

    def test_staged_transfer_partial_writes_keep_original_fd_and_inode(self):
        owner = self.stage_model()
        try:
            with self.stage_patches(owner): owner.transfer_staging_file()
            self.assertEqual(owner.fd,900001)
            self.assertEqual(owner.file_identity,(1,101))
            self.assertEqual(owner.stage_fd,900002)
            self.assertEqual(owner.stage_identity,(1,202))
            self.assertFalse(owner.failure)
        finally: module.ORACLE=None

    def test_unsettled_writer_refuses_before_staging_acquisition(self):
        owner = self.stage_model()
        owner.stage_writer_settled=False
        try:
            with (patch.object(module.os,'open',side_effect=AssertionError('new acquisition')),
                    self.assertRaises(BridgeRefused)): owner.transfer_staging_file()
            self.assertIs(module.ORACLE,owner)
            self.assertTrue(owner.failure)
        finally: module.ORACLE=None

    def test_stage_type_link_cap_original_identity_and_parent_faults_are_sticky(self):
        for fault in ('type','links','cap','destination','parent'):
            owner = self.stage_model()
            if fault=='type': owner.stage_path.lstat=lambda:self.snapshot(202,4,stat.S_IFLNK)
            if fault=='links': owner.stage_path.lstat=lambda:self.snapshot(202,4,links=2)
            if fault=='cap': owner.stage_path.lstat=lambda:self.snapshot(202,module.LIMIT+1)
            if fault=='destination': owner.path.lstat=lambda:self.snapshot(303,0)
            if fault=='parent': owner.directory.lstat=lambda:self.snapshot(999,0,stat.S_IFDIR|0o700)
            try:
                with self.stage_patches(owner), self.assertRaises(BridgeRefused): owner.transfer_staging_file()
                self.assertTrue(owner.failure)
                self.assertIs(module.ORACLE,owner)
            finally: module.ORACLE=None

    def test_short_read_zero_write_growth_and_changed_stage_refuse(self):
        for fault in ('short','zero-write','growth','changed'):
            owner=self.stage_model()
            read=[b'data',b''];write=[2,2];count=[0]
            def observe(fd):
                if fd==900001:return self.snapshot(101,0)
                count[0]+=1
                return self.snapshot(202,5 if fault=='changed' and count[0]>1 else 4)
            if fault=='short':read=[b'dat',b''];write=[3]
            if fault=='zero-write':write=[0]
            if fault=='growth':read=[b'extra',b'']
            try:
                with self.stage_patches(owner,read,write,observe),self.assertRaises(BridgeRefused):owner.transfer_staging_file()
                self.assertIs(module.ORACLE,owner)
                self.assertTrue(owner.failure)
                self.assertEqual(owner.file_identity,(1,101))
            finally:module.ORACLE=None

    def test_stage_close_fault_does_not_skip_independent_original_and_paths(self):
        owner=self.stage_model();owner.stage_fd=900002;owner.stage_identity=(1,202)
        calls=[]
        owner.stage_path.unlink=lambda:calls.append('stage-unlink')
        owner.path.unlink=lambda:calls.append('original-unlink')
        owner.directory.rmdir=lambda:calls.append('directory-rmdir')
        def close(fd):
            calls.append('close-'+str(fd))
            if fd==900002:raise OSError('model only')
        try:
            with self.stage_patches(owner),patch.object(module.os,'close',side_effect=close),self.assertRaises(BridgeRefused):owner.close()
            self.assertEqual(calls,['close-900002','close-900001','stage-unlink','original-unlink','directory-rmdir'])
            self.assertIs(module.ORACLE,owner)
            self.assertTrue(owner.stage_close_attempted)
            self.assertTrue(owner.failure)
        finally:module.ORACLE=None

    def test_unknown_stage_identity_never_adopts_or_deletes_path(self):
        owner=self.stage_model();calls=[]
        owner.fd=None;owner.path=None
        owner.stage_path.unlink=lambda:calls.append('unsafe-unlink')
        owner.directory.rmdir=lambda:calls.append('independent-rmdir-attempt')
        runner=types.SimpleNamespace(command_receipts=lambda:())
        try:
            with patch.dict(sys.modules,{'scanner_docker_command':runner}),self.assertRaises(BridgeRefused):owner.close()
            self.assertEqual(calls,['independent-rmdir-attempt'])
            self.assertIs(module.ORACLE,owner)
        finally:module.ORACLE=None

    def setUp(self):
        # Pure failures never become the real worker's first diagnostic or owner.
        self.original_oracle = module.ORACLE
        self.addCleanup(setattr, module, 'ORACLE', self.original_oracle)
        diagnostic = types.SimpleNamespace(guard=lambda message: None,
            stage=lambda value: None, failure=lambda error: None)
        self.model_diagnostic = patch.object(module, 'diagnostic', diagnostic)
        self.model_diagnostic.start()
        self.addCleanup(self.model_diagnostic.stop)

    def test_immutable_image_layers_and_volume_rejection(self):
        image = {'Id': 'config', 'RepoDigests': ['reference'], 'RootFS': {'Layers': ['sha256:' + 'a' * 64]},
                 'Config': {}}
        self.assertEqual(module.checked_image(image, 'reference', 'config'), ('sha256:' + 'a' * 64,))
        for bad in [dict(image, Id='different'), dict(image, RepoDigests=[]),
                    dict(image, RootFS={'Layers': []}), dict(image, Config={'Volumes': {'/data': {}}})]:
            with self.subTest(bad=bad), self.assertRaises(BridgeRefused):
                module.checked_image(bad, 'reference', 'config')

    def model(self):
        owner = object.__new__(module.PinnedImageOracle)
        owner.finished = False
        owner.failure = False
        owner.cap_restore_required = False
        owner.create_attempted = False
        owner.container_id = None
        owner.fd = None
        owner.path = None
        owner.directory = None
        owner.fd_close_attempted = False
        owner.stage_path = None
        owner.stage_fd = None
        owner.stage_identity = None
        owner.stage_close_attempted = False
        owner.stage_write_attempted = False
        owner.stage_writer_settled = False
        module.ORACLE = owner
        return owner

    def test_original_fd_close_failure_does_not_skip_owned_path_cleanup(self):
        owner = self.model()
        owner.fd = 100001
        owner.file_identity = (1, 2)
        owner.directory_identity = (1, 3)
        calls = []
        owner.path = types.SimpleNamespace(lstat=lambda: types.SimpleNamespace(st_dev=1, st_ino=2,
            st_mode=stat.S_IFREG), unlink=lambda: calls.append('file-unlink'))
        owner.directory = types.SimpleNamespace(lstat=lambda: types.SimpleNamespace(st_dev=1, st_ino=3,
            st_mode=stat.S_IFDIR), rmdir=lambda: calls.append('directory-rmdir'))
        runner = types.SimpleNamespace(command_receipts=lambda: ())
        try:
            with patch.dict(sys.modules, {'scanner_docker_command': runner}), \
                 patch.object(module.os, 'fstat', return_value=types.SimpleNamespace(st_dev=1, st_ino=2)), \
                 patch.object(module.os, 'close', side_effect=OSError('causal close fault')), \
                 self.assertRaises(BridgeRefused):
                owner.close()
            self.assertEqual(calls, ['file-unlink', 'directory-rmdir'])
            self.assertIs(module.ORACLE, owner)
            self.assertEqual(owner.fd, 100001)
            self.assertTrue(owner.fd_close_attempted)
            self.assertTrue(owner.failure)
        finally:
            module.ORACLE = None

    def test_quarantined_writer_prevents_file_delete_or_new_command(self):
        owner = self.model()
        calls = []
        owner.docker = lambda *a, **k: calls.append('unexpected-command')
        owner.path = types.SimpleNamespace(unlink=lambda: calls.append('unexpected-unlink'))
        runner = types.SimpleNamespace(command_receipts=lambda: ({'cleanupVerified': False, 'quarantined': True},))
        try:
            with patch.dict(sys.modules, {'scanner_docker_command': runner}), self.assertRaises(BridgeRefused):
                owner.close()
            self.assertEqual(calls, [])
            self.assertIs(module.ORACLE, owner)
            self.assertFalse(owner.finished)
        finally:
            module.ORACLE = None

    def test_failed_soft_cap_restore_is_retried_independently(self):
        owner = self.model()
        owner.cap_restore_required = True
        owner.rlimit_prior = (123, 456)
        calls = []
        resource = types.SimpleNamespace(RLIMIT_FSIZE=9,
            setrlimit=lambda key, value: calls.append((key, value)),
            getrlimit=lambda key: (123, 456))
        runner = types.SimpleNamespace(command_receipts=lambda: ())
        try:
            with patch.dict(sys.modules, {'resource': resource, 'scanner_docker_command': runner}):
                owner.close()
            self.assertEqual(calls, [(9, (123, 456))])
            self.assertFalse(owner.cap_restore_required)
            self.assertTrue(owner.finished)
            self.assertIsNone(module.ORACLE)
        finally:
            module.ORACLE = None


if __name__ == '__main__':
    unittest.main()
