"""Pure integration controls over actual proposed methods; no native dependencies."""
from pathlib import Path
import sys
import types
import unittest

ROOT = Path(__file__).parent


class EndOfPrefix(Exception):
    pass


class Controls(unittest.TestCase):
    def setUp(self):
        self.previous = {name: sys.modules.get(name) for name in
                         ('held_host_lifetime', 'retained_file_front_consumers')}
        fixture = types.ModuleType('pure_adapter_fixture')
        fixture_path = ROOT.parent / 'test_retained_file_front_consumers.py'
        source = fixture_path.read_bytes()
        fixture.__file__ = str(fixture_path)
        exec(compile(source, fixture.__file__, 'exec'), fixture.__dict__)
        self.base = fixture.Controls('test_prebirth_then_exact_live_and_global_release')
        self.base.setUp()
        self.cap = self.base.adapter()
        self.context = self.base.context
        self.context.validate = lambda *args, **kwargs: None
        sys.modules['held_host_lifetime'] = self.base.module.original
        sys.modules['retained_file_front_consumers'] = self.base.module
        module = types.ModuleType('proposed_bridge')
        exec(compile((ROOT / 'borrowed_scanner_bridge.py').read_bytes(), 'proposed_bridge', 'exec'), module.__dict__)
        self.module = module
        self.bridge = object.__new__(module.BorrowedScannerBridge)
        self.bridge.context = self.context
        self.bridge._consumers = {}
        self.bridge._failure = self.bridge._released = False
        self.bridge._borrow = types.SimpleNamespace(container_id='pure-backend')
        self.bridge.scanner_container_id = 'pure-scanner'
        self.bridge.network_id = 'pure-network'
        self.bridge._runtime_id = 'pure-image'
        self.bridge._generation = {'pure': True}
        self.bridge.scanner = types.SimpleNamespace(network_id='pure-network', container_id='pure-scanner',
            image_id='pure-image', receipt={'containerGeneration': {'pure': True}})
        self.bridge._relay = lambda: (_ for _ in ()).throw(EndOfPrefix())

    def tearDown(self):
        for name, value in self.previous.items():
            if value is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = value

    def retain(self):
        self.bridge.retain_file_front_consumers(self.cap)

    def live(self):
        self.retain()
        self.base.births()
        self.cap.admit_live()

    def test_atomic_retains_both_exact_references(self):
        self.retain()
        self.assertEqual(set(self.bridge._consumers), {'File', 'Front'})
        self.assertIs(self.bridge._consumers['File'], self.cap)
        self.assertIs(self.bridge._consumers['Front'], self.cap)

    def test_arbitrary_process_or_dictionary_refused(self):
        for value in (object(), {}):
            with self.assertRaises(self.module.BridgeRefused):
                self.bridge.retain_file_front_consumers(value)
        self.assertEqual(self.bridge._consumers, {})

    def test_duplicate_retention_sticky_preserves_original(self):
        self.retain()
        with self.assertRaises(self.module.BridgeRefused):
            self.retain()
        self.assertIs(self.bridge._consumers['File'], self.cap)
        self.assertTrue(self.bridge._failure)

    def test_context_substitution_refused_after_retention(self):
        self.base.front.context = object()
        with self.assertRaises(self.base.module.ConsumerHandoffRefused):
            self.retain()
        self.assertIs(self.bridge._consumers['Front'], self.cap)
        self.assertTrue(self.bridge._failure)

    def test_driver_substitution_refused_with_retained_objects(self):
        self.base.life.driver = type(self.base.life.driver)()
        with self.assertRaises(self.base.module.ConsumerHandoffRefused):
            self.retain()
        self.assertIs(self.bridge._consumers['File'], self.cap)

    def test_legacy_retention_still_refuses(self):
        with self.assertRaises(self.module.BridgeRefused):
            self.bridge.retain_consumer('File', object())

    def test_prebirth_preparation_does_not_invent_admission(self):
        self.retain()
        with self.assertRaises(EndOfPrefix):
            self.bridge._refresh_members(())
        self.assertEqual(self.base.life.observed, [])
        self.assertFalse(self.cap.admitted)

    def test_actual_prepared_front_phase_observer(self):
        self.retain()
        self.base.front_birth()
        with self.assertRaises(EndOfPrefix):
            self.bridge._refresh_members(())
        self.assertEqual(self.base.life.observed, [self.base.front, self.base.front])
        self.assertIs(self.cap.front_row, self.base.life.children[0])
        self.assertFalse(self.cap.admitted)

    def test_unadmitted_file_birth_refresh_refuses(self):
        self.retain()
        self.base.births()
        with self.assertRaises(self.base.module.ConsumerHandoffRefused):
            self.bridge.refresh()
        self.assertTrue(self.bridge._failure)
        self.assertEqual(self.base.life.observed, [])

    def test_admitted_refresh_calls_actual_capability_observer(self):
        self.live()
        before = len(self.base.life.observed)
        with self.assertRaises(EndOfPrefix):
            self.bridge._refresh_members(())
        self.assertEqual(self.base.life.observed[before:], [self.base.normal, self.base.front])

    def test_failed_capability_never_refreshes(self):
        self.live()
        self.cap.failed = True
        with self.assertRaises(self.module.BridgeRefused):
            self.bridge.refresh()
        self.assertTrue(self.bridge._failure)

    def test_quiescence_requires_original_global_fence(self):
        self.live()
        self.bridge._quiescent()
        self.assertTrue(self.cap.released)
        self.assertEqual(self.base.life.release_calls, 1)
        self.assertIs(self.bridge._consumers['File'], self.cap)

    def test_repeat_quiescence_rechecks_original_not_boolean(self):
        self.live()
        self.bridge._quiescent()
        self.bridge._quiescent()
        self.assertEqual(self.base.life.release_calls, 2)
        self.assertIs(self.bridge._consumers['Front'], self.cap)

    def test_release_failure_keeps_capability_and_owners(self):
        self.live()
        self.base.life.fail_release = True
        with self.assertRaises(self.base.module.ConsumerHandoffRefused):
            self.bridge.remove_backend_after_quiescence()
        self.assertTrue(self.bridge._failure)
        self.assertIs(self.bridge._consumers['File'], self.cap)
        self.assertFalse(self.bridge._released)

    def test_failed_prebirth_cleanup_stays_unaccepted(self):
        self.retain()
        self.cap.failed = True
        with self.assertRaises(self.base.module.ConsumerHandoffRefused):
            self.bridge.remove_backend_after_quiescence()
        self.assertEqual(self.base.front.close_calls, 1)
        self.assertIs(self.bridge._consumers['Front'], self.cap)
        self.assertFalse(self.bridge._released)

    def test_standalone_empty_inventory_unchanged(self):
        self.bridge._quiescent()
        self.assertEqual(self.base.life.release_calls, 0)


if __name__ == '__main__':
    unittest.main()
