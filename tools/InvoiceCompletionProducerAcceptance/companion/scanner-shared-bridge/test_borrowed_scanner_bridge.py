import copy
import unittest
from borrowed_scanner_bridge import BridgeRefused, BorrowedScannerBridge, validate_image_chain, validate_network


class BridgeControls(unittest.TestCase):
    def test_actual_image_ancestry_predicate(self):
        base = {'Id': 'base', 'RepoDigests': ['pinned'], 'RootFS': {'Layers': ['a', 'b']}}
        derived = {'Id': 'runtime', 'RootFS': {'Layers': ['a', 'b', 'c']},
                   'Config': {'Labels': {'financial.acceptance.run': 'run'}}}
        build = {'owned': True, 'buildCompleted': True, 'runLabel': 'run',
                 'baseImageId': 'base', 'imageId': 'runtime'}
        validate_image_chain(base, derived, build, 'pinned', 'runtime', 'run')
        for key, value in [('imageId', 'base'), ('baseImageId', 'wrong'), ('owned', False),
                           ('buildCompleted', False), ('runLabel', 'foreign')]:
            bad = dict(build, **{key: value})
            with self.subTest(key=key), self.assertRaises(BridgeRefused):
                validate_image_chain(base, derived, bad, 'pinned', 'runtime', 'run')
        for layers in [[], ['x', 'b', 'c'], ['a', 'b'], ['a', 'b', 'c', 'd']]:
            bad = copy.deepcopy(derived)
            bad['RootFS']['Layers'] = layers
            with self.subTest(layers=layers), self.assertRaises(BridgeRefused):
                validate_image_chain(base, bad, build, 'pinned', 'runtime', 'run')

    def test_network_substitution_and_foreign_member(self):
        network = {'Id': 'network', 'Created': 'created', 'Internal': True, 'Driver': 'bridge',
                   'EnableIPv6': False, 'Scope': 'local', 'Labels': {'financial.acceptance.run': 'run'},
                   'Containers': {'scanner': {}, 'storage': {}}}
        validate_network(network, 'network', 'created', 'run', ('scanner', 'storage'))
        for key, value in [('Id', 'other'), ('Created', 'new'), ('Internal', False),
                           ('Driver', 'host'), ('EnableIPv6', True), ('Scope', 'swarm'),
                           ('Containers', {'scanner': {}}), ('Containers', {'scanner': {}, 'foreign': {}})]:
            with self.subTest(key=key), self.assertRaises(BridgeRefused):
                validate_network(dict(network, **{key: value}), 'network', 'created', 'run', ('scanner', 'storage'))

    def test_close_cannot_remove_live_borrow(self):
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._released = False
        bridge._borrow = object()
        with self.assertRaises(BridgeRefused):
            bridge.close_scanner()

    def test_failed_consumer_quiescence_keeps_borrow(self):
        class ChildModel:
            stdin = stdout = stderr = None
            def poll(self):
                return None
        bridge = object.__new__(BorrowedScannerBridge)
        borrow = object()
        bridge._borrow = borrow
        bridge._released = False
        bridge._consumers = {'File': ChildModel(), 'front': ChildModel()}
        with self.assertRaises(BridgeRefused):
            bridge.remove_backend_after_quiescence()
        self.assertIs(bridge._borrow, borrow)
        self.assertFalse(bridge._released)

    def test_partial_consumer_pair_refuses(self):
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._consumers = {'front': object()}
        with self.assertRaises(BridgeRefused):
            bridge._quiescent()

    def test_refresh_failure_is_sticky_and_retains_borrow(self):
        bridge = object.__new__(BorrowedScannerBridge)
        from types import SimpleNamespace
        borrow = SimpleNamespace(container_id='backend')
        bridge.scanner_container_id = 'scanner'
        bridge._borrow = borrow
        bridge._failure = False
        def refuse(*args, **kwargs):
            raise BridgeRefused('causal refresh refusal')
        bridge._refresh_members = refuse
        with self.assertRaises(BridgeRefused):
            bridge.refresh()
        self.assertTrue(bridge._failure)
        self.assertIs(bridge._borrow, borrow)

    def test_absence_failure_is_sticky_and_retains_borrow(self):
        from types import SimpleNamespace
        bridge = object.__new__(BorrowedScannerBridge)
        borrow = SimpleNamespace(container_id='retained-id')
        bridge._borrow = borrow
        bridge._released = False
        bridge._failure = False
        bridge._consumers = {}
        bridge.scanner = SimpleNamespace(docker=lambda *a, **k: 'retained-id')
        with self.assertRaises(BridgeRefused):
            bridge.confirm_backend_absent_and_release()
        self.assertTrue(bridge._failure)
        self.assertIs(bridge._borrow, borrow)

    def test_consumer_handoff_refuses_instead_of_faking_drain(self):
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._failure = False
        with self.assertRaises(BridgeRefused):
            bridge.retain_consumer('File', object())
        self.assertTrue(bridge._failure)


    def test_cleanup_ownership_does_not_use_dead_relay(self):
        from types import SimpleNamespace
        bridge = object.__new__(BorrowedScannerBridge)
        validations = []
        bridge.context = SimpleNamespace(validate=lambda env, now, cleanup=False: validations.append(cleanup))
        bridge._released = False
        bridge.network_id = 'network'
        bridge.scanner_container_id = 'scanner'
        bridge.created_utc = 'network-created'
        bridge._runtime_id = 'image'
        bridge._generation = {'createdUtc': 'created', 'startedUtc': 'started'}
        bridge.scanner = SimpleNamespace(network_id='network', container_id='scanner', image_id='image',
            run_id='run', receipt={'containerGeneration': dict(bridge._generation)})
        def no_live_relay():
            raise AssertionError('dead relay use admission must not gate cleanup')
        bridge._relay = no_live_relay
        container = {'Id': 'scanner', 'Image': 'image', 'Created': 'created',
            'State': {'StartedAt': 'started'}, 'Config': {'Labels': {'financial.acceptance.run': 'run'}}}
        network = {'Id': 'network', 'Created': 'network-created', 'Internal': True, 'Driver': 'bridge',
            'EnableIPv6': False, 'Scope': 'local', 'Labels': {'financial.acceptance.run': 'run'},
            'Containers': {'scanner': {}, 'backend': {}}}
        bridge._inspect = lambda kind, handle: container if kind == 'container' else network
        bridge._refresh_members(('scanner', 'backend'), cleanup=True)
        self.assertEqual(validations, [True])

    def test_pre_stop_failure_is_sticky_without_losing_borrow(self):
        bridge = object.__new__(BorrowedScannerBridge)
        from types import SimpleNamespace
        bridge._borrow = SimpleNamespace(container_id='backend')
        bridge.scanner_container_id = 'scanner'
        retained = bridge._borrow
        bridge._released = False
        bridge._failure = False
        bridge._consumers = {}
        def refuse(*args, **kwargs):
            raise BridgeRefused('pre-stop exact ownership refused')
        bridge._refresh_members = refuse
        with self.assertRaises(BridgeRefused):
            bridge.remove_backend_after_quiescence()
        self.assertTrue(bridge._failure)
        self.assertIs(bridge._borrow, retained)

    def test_admitted_dependency_import_fault_still_attempts_cleanup(self):
        import builtins
        from unittest.mock import patch
        from qualify_held_pair import qualify_held_pair
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._borrow = object()
        bridge._released = False
        bridge._failure = False
        attempts = []
        bridge.remove_backend_after_quiescence = lambda: attempts.append('backend-cleanup')
        original = builtins.__import__
        def controlled_import(name, *args, **kwargs):
            if name == 'owned_storage_backend':
                raise ImportError('causal dependency fault')
            return original(name, *args, **kwargs)
        with patch('builtins.__import__', controlled_import), self.assertRaises(BridgeRefused):
            qualify_held_pair(bridge)
        self.assertEqual(attempts, ['backend-cleanup'])
        self.assertTrue(bridge._failure)
        self.assertIsNotNone(bridge._borrow)

    def test_failed_then_valid_refresh_is_quarantined_before_observation(self):
        bridge = object.__new__(BorrowedScannerBridge)
        bridge._failure = True
        bridge._borrow = object()
        calls = []
        bridge._refresh_members = lambda *a, **k: calls.append('owner-observation')
        with self.assertRaises(BridgeRefused):
            bridge.refresh()
        with self.assertRaises(BridgeRefused):
            bridge.borrow(object())
        self.assertEqual(calls, [])
        self.assertTrue(bridge._failure)


if __name__ == '__main__':
    unittest.main()
