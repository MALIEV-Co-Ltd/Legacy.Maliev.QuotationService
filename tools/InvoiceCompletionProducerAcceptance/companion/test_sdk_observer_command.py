"""Actor-free causal SDK observer adapter controls; no Linux lifecycle proof."""
import importlib.util
import os
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).parent
SCANNER_SOURCE = HERE/'scanner_docker_command.py'
if not SCANNER_SOURCE.exists():
    SCANNER_SOURCE = HERE.parent/'preimages/scanner_docker_command.py'


def load():
    scanner_spec = importlib.util.spec_from_file_location('scanner_docker_command', SCANNER_SOURCE)
    scanner = importlib.util.module_from_spec(scanner_spec)
    scanner_spec.loader.exec_module(scanner)
    files = ModuleType('regular_owned_files')
    files.regular_hash = lambda path: 'a'*64
    spec = importlib.util.spec_from_file_location('sdk_observer_model', HERE/'sdk_observer_command.py')
    module = importlib.util.module_from_spec(spec)
    with patch.dict(sys.modules, scanner_docker_command=scanner, regular_owned_files=files):
        spec.loader.exec_module(module)
    return module


class Controls(unittest.TestCase):
    def setUp(self):
        self.m = load()

    def test_cap_checked_before_buffer_extension(self):
        lease = self.m.ObserverLease(1)
        stream = object()
        lease.output = {stream: bytearray(b'a'*16384)}
        lease.bindings = {stream: (123, 1, 2)}
        with patch.object(lease, 'require_pipe'), patch.object(self.m.os, 'read', return_value=b'b'):
            with self.assertRaises(self.m.ObserverLifecycleError): lease.read(stream, True)
        self.assertEqual(len(lease.output[stream]), 16384)

    def test_exact_eof_from_delegated_empty_read(self):
        lease = self.m.ObserverLease(1)
        stream = object(); lease.bindings = {stream: (123, 1, 2)}
        with patch.object(lease, 'require_pipe'), patch.object(self.m.os, 'read', return_value=b''):
            self.assertFalse(lease.read(stream, True))
        self.assertIn(stream, lease.eof)

    def owner(self):
        owner = self.m.RequestOwner(Path('/private'), 1)
        owner.descriptor = 123; owner.identity = (1, 2, 10)
        return owner

    def test_unsettled_helper_never_unlinks_or_closes_request(self):
        owner = self.owner()
        with patch.object(self.m.os, 'unlink') as unlink, patch.object(self.m.os, 'close') as close:
            with self.assertRaises(self.m.ObserverLifecycleError): owner.release_request()
        unlink.assert_not_called(); close.assert_not_called()

    def test_foreign_request_identity_refuses_release(self):
        owner = self.owner(); owner.lease.receipt['cleanupVerified'] = True
        with patch.object(self.m, 'request_identity', return_value=(1, 3, 10)), patch.object(self.m.os, 'unlink') as unlink:
            with self.assertRaises(self.m.ObserverLifecycleError): owner.release_request()
        unlink.assert_not_called()

    def test_request_uncertain_close_never_retried(self):
        owner = self.owner(); owner.lease.receipt['cleanupVerified'] = True
        metadata = SimpleNamespace(st_mode=0o100600, st_nlink=1, st_dev=1, st_ino=2, st_size=10)
        with patch.object(self.m, 'request_identity', return_value=(1, 2, 10)), patch.object(self.m.os, 'lstat', return_value=metadata), patch.object(self.m.os, 'unlink'), patch.object(self.m.os, 'close', side_effect=OSError) as close:
            with self.assertRaises(OSError): owner.release_request()
            with self.assertRaises(self.m.ObserverLifecycleError): owner.release_request()
        self.assertEqual(close.call_count, 1)
        self.assertTrue(owner.unlinked); self.assertFalse(owner.closed)

    def test_no_public_private_identity_fields(self):
        owner = self.owner(); self.m._OWNERS.append(owner)
        row = owner.snapshot()
        self.assertTrue(all(type(value) is bool for value in row.values()))
        self.assertNotIn('path', row); self.assertNotIn('pid', row)
        self.assertTrue(self.m.has_retained_owner())

    def test_fixed_timeout_admission_before_birth(self):
        with patch.object(self.m, 'exact_file') as files:
            for timeout in (0, -1, 8, True, float('nan')):
                with self.assertRaises(self.m.ObserverLifecycleError):
                    self.m.observe({}, '/private', '/dotnet', 'a'*64, '/observer.dll', 'b'*64, {}, timeout)
        files.assert_not_called()

    def test_expired_request_budget_refuses_before_open(self):
        owner = self.owner()
        with patch.object(self.m.time, "monotonic", return_value=owner.lease.end + 1), patch.object(self.m.os, "open") as opened:
            with self.assertRaises(self.m.ObserverLifecycleError): owner.create({"Owner": "Front"})
        opened.assert_not_called()
        self.assertFalse(owner.create_attempted)

    def test_hash_setup_expiration_refuses_before_original_birth(self):
        clock = [0.0]
        calls = [0]
        def qualified(value, digest):
            calls[0] += 1
            if calls[0] == 3: clock[0] = 8.0
            return Path(value)
        with patch.object(self.m.time, "monotonic", side_effect=lambda: clock[0]), \
             patch.object(self.m, "exact_file", side_effect=qualified), \
             patch.object(Path, "is_absolute", return_value=True), patch.object(Path, "resolve", lambda value: value), \
             patch.object(Path, "is_dir", return_value=True), patch.object(Path, "is_symlink", return_value=False), \
             patch.object(self.m.RequestOwner, "create"), patch.object(self.m.RequestOwner, "release_request"), \
             patch.object(self.m.ObserverLease, "cleanup"), patch.object(self.m.subprocess, "Popen") as birth:
            with self.assertRaises(self.m.ObserverLifecycleError):
                self.m.observe({}, "/private", "/dotnet", "a"*64, "/observer.dll", "b"*64, {}, 7)
        birth.assert_not_called()
        self.assertEqual(len(self.m._OWNERS), 1)
        self.assertFalse(self.m._OWNERS[0].lease.birth_attempted)
        self.assertIsNone(self.m._OWNERS[0].lease.process)

    def test_source_keeps_original_docker_git_apis(self):
        raw = (SCANNER_SOURCE).read_bytes()
        self.assertIn(b'def run_docker(', raw)
        self.assertIn(b'def run_git_bytes(', raw)
        self.assertNotIn(b'def run_dotnet', raw)


if __name__ == '__main__': unittest.main()
