import sys
import types
import unittest
from unittest.mock import patch

from borrowed_scanner_bridge import BridgeRefused
from held_pair_launcher import select_backend_ip
from storage_owner_command import storage_command


class LauncherSourceControls(unittest.TestCase):
    def test_address_comes_from_actual_subnet_and_avoids_members(self):
        network = {'IPAM': {'Config': [{'Subnet': '172.20.0.0/24', 'Gateway': '172.20.0.1'}]},
                   'Containers': {'scanner': {'IPv4Address': '172.20.0.2/24'}}}
        self.assertEqual(select_backend_ip(network), '172.20.0.3')
        with self.assertRaises(BridgeRefused):
            select_backend_ip({'IPAM': {'Config': []}})
        with self.assertRaises(BridgeRefused):
            select_backend_ip({'IPAM': {'Config': [{'Subnet': '8.8.8.0/24'}]}})

    def test_storage_reads_use_explicit_owner_runner(self):
        calls = []
        def runner(args, timeout=30):
            calls.append((args, timeout))
            return 'synthetic modeled stdout'
        module = types.SimpleNamespace(run_docker=runner)
        with patch.dict(sys.modules, {'scanner_docker_command': module}):
            output = storage_command(['docker', 'exec', 'a' * 64, 'cat', '/proc/1/stat'])
        self.assertEqual(output, b'synthetic modeled stdout')
        self.assertEqual(calls, [(['exec', 'a' * 64, 'cat', '/proc/1/stat'], 10)])

    def test_unreviewed_mutation_and_private_path_refuse_before_runner(self):
        calls = []
        module = types.SimpleNamespace(run_docker=lambda *a, **k: calls.append(a))
        bad = [['docker', 'rm', 'a' * 64], ['docker', 'exec', 'a' * 64, 'cat', '/etc/passwd'],
               ['docker', 'exec', 'a' * 64, 'sh', '-c', 'cat /proc/1/stat'],
               ['docker', 'container', 'inspect', 'short']]
        with patch.dict(sys.modules, {'scanner_docker_command': module}):
            for args in bad:
                with self.subTest(args=args), self.assertRaises(BridgeRefused):
                    storage_command(args)
        self.assertEqual(calls, [])

    def test_explicit_per_read_output_cap_refuses(self):
        module = types.SimpleNamespace(run_docker=lambda *a, **k: 'x' * 65)
        with patch.dict(sys.modules, {'scanner_docker_command': module}), self.assertRaises(BridgeRefused):
            storage_command(['docker', 'exec', 'a' * 64, 'cat', '/proc/1/stat'], maximum=64)

    def test_normal_release_uses_original_bridge_release_and_close(self):
        import held_pair_launcher as module
        from borrowed_scanner_bridge import BorrowedScannerBridge
        from types import SimpleNamespace
        events = []
        scanner = SimpleNamespace(container_id='scanner', receipt={'cleanupVerified': False})
        responses = iter(['backend', ''])
        scanner.docker = lambda *a, **k: next(responses)
        def close_original():
            events.append('scanner-close')
            scanner.receipt['cleanupVerified'] = True
        scanner.close = close_original
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._borrow = SimpleNamespace(container_id='backend')
        bridge._consumers = {}
        bridge._released = False
        bridge._failure = False
        bridge.scanner_container_id = 'scanner'
        bridge.scanner = scanner
        def observe_one(members, cleanup=False):
            self.assertFalse(bridge._released)
            self.assertEqual(members, ('scanner',))
            self.assertTrue(cleanup)
        bridge._refresh_members = observe_one
        # Keep ORIGINAL absence/release/close semantics; model only stop/remove.
        bridge.remove_backend_after_quiescence = bridge.confirm_backend_absent_and_release
        owner = object.__new__(module.HeldPairLauncher)
        owner.bridge = bridge
        owner.scanner = scanner
        owner.backend_id = 'backend'
        owner.backend_create_attempted = True
        owner.failure = False
        owner.finished = False
        module.OWNER = owner
        try:
            owner.close()
            self.assertTrue(bridge._released)
            self.assertTrue(owner.finished)
            self.assertIsNone(module.OWNER)
            self.assertEqual(events, ['scanner-close'])
        finally:
            module.OWNER = None

    def test_uncertain_removed_backend_recovery_does_not_inspect_absent_id(self):
        import held_pair_launcher as module
        from types import SimpleNamespace
        calls = []
        scanner = SimpleNamespace(receipt={'cleanupVerified': False})
        scanner.docker = lambda *a, **k: calls.append(a) or ''
        scanner.close = lambda: scanner.receipt.update(cleanupVerified=True)
        owner = object.__new__(module.HeldPairLauncher)
        owner.scanner = scanner
        owner.bridge = None
        owner.backend_id = 'a' * 64
        owner.backend_create_attempted = True
        owner.failure = False
        owner.finished = False
        def forbidden_inspect(*args):
            raise AssertionError('Already removed full-ID must use absence observation')
        owner.inspect = forbidden_inspect
        module.OWNER = owner
        try:
            owner.close()
            self.assertIsNone(owner.backend_id)
            self.assertTrue(owner.finished)
            self.assertIsNone(module.OWNER)
            self.assertEqual(calls[0][0:2], ('ps', '-a'))
        finally:
            module.OWNER = None

    def test_failure_before_borrow_retains_original_owner_on_cleanup_fault(self):
        import held_pair_launcher as module
        from types import SimpleNamespace
        scanner = SimpleNamespace(receipt={'cleanupVerified': False})
        def cleanup_fault():
            raise RuntimeError('causal original Scanner cleanup fault')
        scanner.close = cleanup_fault
        owner = object.__new__(module.HeldPairLauncher)
        owner.scanner = scanner
        owner.bridge = None
        owner.backend_id = None
        owner.backend_create_attempted = False
        owner.failure = True
        owner.finished = False
        module.OWNER = owner
        try:
            with self.assertRaises(RuntimeError):
                owner.close()
            self.assertIs(module.OWNER, owner)
            self.assertTrue(owner.failure)
            self.assertFalse(owner.finished)
        finally:
            module.OWNER = None

    def test_live_observer_import_failure_quarantines_both_owners(self):
        import builtins
        import held_pair_launcher as module
        owner = object.__new__(module.HeldPairLauncher)
        owner.failure = False
        owner.lease = object()
        owner.finished = False
        owner.bridge = types.SimpleNamespace(_failure=False)
        module.OWNER = owner
        original = builtins.__import__
        def importer(name, *args, **kwargs):
            if name == 'owned_storage_backend':
                raise ImportError('causal admitted observer dependency fault')
            return original(name, *args, **kwargs)
        with patch('builtins.__import__', importer), self.assertRaises(ImportError):
            owner.observe()
        self.assertTrue(owner.failure)
        self.assertTrue(owner.bridge._failure)
        with self.assertRaises(BridgeRefused):
            owner.observe()
        module.OWNER = None

    def test_actual_borrow_uncertain_rm_uses_original_absence_recovery(self):
        import held_pair_launcher as module
        from borrowed_scanner_bridge import BorrowedScannerBridge
        scanner = types.SimpleNamespace(container_id='scanner', receipt={'cleanupVerified': False})
        scanner.docker = lambda *a, **k: ''
        scanner.close = lambda: scanner.receipt.update(cleanupVerified=True)
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._borrow = types.SimpleNamespace(container_id='backend')
        bridge._consumers = {}
        bridge._released = False
        bridge._failure = False
        bridge.scanner_container_id = 'scanner'
        bridge.scanner = scanner
        bridge._refresh_members = lambda *a, **k: None
        def forbidden_repeat():
            raise AssertionError('Uncertain removed borrow must not repeat stop/rm')
        bridge.remove_backend_after_quiescence = forbidden_repeat
        owner = object.__new__(module.HeldPairLauncher)
        owner.scanner = scanner
        owner.bridge = bridge
        owner.backend_id = 'backend'
        owner.backend_create_attempted = True
        owner.failure = False
        owner.finished = False
        module.OWNER = owner
        try:
            owner.close()
            self.assertTrue(bridge._released)
            self.assertTrue(owner.finished)
            self.assertIsNone(module.OWNER)
        finally:
            module.OWNER = None

    def test_finished_owner_cannot_reopen_or_erase_new_owner(self):
        import held_pair_launcher as module
        owner = object.__new__(module.HeldPairLauncher)
        owner.finished = True
        owner.failure = False
        owner.bridge = None
        owner.backend_create_attempted = False
        owner.lease = object()
        newer = object()
        module.OWNER = newer
        try:
            with self.assertRaises(BridgeRefused):
                owner.start()
            self.assertIs(module.OWNER, newer)
            with self.assertRaises(BridgeRefused):
                owner.observe()
            with self.assertRaises(BridgeRefused):
                owner.close()
            self.assertIs(module.OWNER, newer)
        finally:
            module.OWNER = None


if __name__ == '__main__':
    unittest.main()
