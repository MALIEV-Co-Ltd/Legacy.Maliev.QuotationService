import stat
import sys
import types
import unittest
from unittest.mock import patch
import pinned_image_oracle as module
from borrowed_scanner_bridge import BridgeRefused


class OracleSourceControls(unittest.TestCase):
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
