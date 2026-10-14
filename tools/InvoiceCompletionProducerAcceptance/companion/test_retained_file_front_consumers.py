"""Pure API algorithm controls: no original imports, descriptors or child processes."""
from pathlib import Path
import sys
import types
import unittest


class LinuxLifetime:
    def verify(self, handle, identity):
        if self.fail_verify:
            raise ValueError()

    def bootstrap(self, pipe):
        return self.binding

    def bootstrap_child(self, process, binding):
        if self.fail_bootstrap or binding is not self.binding:
            raise ValueError()


class HeldPopen:
    def __init__(self, lifetime):
        self.lifetime = lifetime
        self.stdin = self.stdout = self.stderr = None


class HostLifetime:
    def __init__(self):
        self.driver = LinuxLifetime()
        self.driver.fail_verify = self.driver.fail_bootstrap = False
        self.bootstrap_binding = self.driver.binding = object()
        self.parent_pidfd, self.parent = object(), object()
        self.parent_admitted = True
        self.failed = self.closed = self.birth_uncertain = False
        self.bootstrap_closed = False
        self.bootstrap_close_uncertain = False
        self.children = []
        self.observed = []
        self.release_calls = 0
        self.fail_observation = self.fail_release = False

    def observe_original(self, owner):
        self.observed.append(owner)
        if self.fail_observation:
            raise ValueError()

    def assert_backend_release(self):
        self.release_calls += 1
        if self.fail_release or not self.closed or self.failed:
            raise ValueError()


class LifetimeNormalHosts:
    pass


class LifetimeFrontHost:
    def observe_listener(self):
        self.listener_observations += 1
        if self.fail_listener:
            raise ValueError()
        for row in self.lifetime.children:
            if row.owner is self and row.role == 'Front' and row.listener is None:
                row.listener = 'pure-source-observed-listener'

    def close(self):
        self.close_calls += 1
        self.lifetime.closed = True
        self.lifetime.bootstrap_closed = True
        self.pipe.closed = True
        self.pipe.read_fd = self.pipe.write_fd = None


def load():
    original = types.ModuleType('held_host_lifetime')
    for cls in (HostLifetime, LinuxLifetime, LifetimeNormalHosts, LifetimeFrontHost, HeldPopen):
        setattr(original, cls.__name__, cls)
    previous = sys.modules.get('held_host_lifetime')
    module = types.ModuleType('pure_retained_adapter')
    try:
        sys.modules['held_host_lifetime'] = original
        source = (Path(__file__).parent / 'retained_file_front_consumers.py').read_bytes()
        exec(compile(source, 'pure_retained_adapter', 'exec'), module.__dict__)
    finally:
        if previous is None:
            del sys.modules['held_host_lifetime']
        else:
            sys.modules['held_host_lifetime'] = previous
    return module


class Controls(unittest.TestCase):
    def setUp(self):
        self.module = load()
        self.life = HostLifetime()
        self.normal = LifetimeNormalHosts()
        self.normal.lifetime = self.life
        self.normal.owned = []
        self.context = types.SimpleNamespace(validate=lambda *args: None)
        self.normal.context = self.context
        self.normal.environment = {}
        self.front = LifetimeFrontHost()
        self.front.lifetime = self.life
        self.front.process = None
        self.front.close_calls = 0
        self.front.listener_observations = 0
        self.front.fail_listener = False
        self.front.context = self.context
        self.front.launcher_environment = {}
        self.front.pipe = types.SimpleNamespace(closed=False, sealed=False, read_fd=10, write_fd=11)
        self.life.normal, self.life.front, self.life.pipe = self.normal, self.front, self.front.pipe

    def adapter(self):
        return self.module.RetainedFileFrontConsumers(self.life, self.normal, self.front)

    def births(self):
        file_process = HeldPopen(self.life)
        self.front.process = HeldPopen(self.life)
        self.front.pipe.sealed = True
        self.front.pipe.write_fd = None
        for role, owner, process, scope in (
            ('File', self.normal, file_process, 'inherited-output'),
            ('Front', self.front, self.front.process, 'devnull-output'),
        ):
            self.life.children.append(types.SimpleNamespace(role=role, owner=owner, process=process,
                descriptor_scope=scope, birth_attempted=True, identity=object(),
                pidfd=types.SimpleNamespace(closed=False, close_uncertain=False),
                listener='synthetic-source-control-only', failed=False, reaped=False))
        self.normal.owned.append([types.SimpleNamespace(owner='File'), file_process])

    def test_prebirth_then_exact_live_and_global_release(self):
        adapter = self.adapter()
        self.births()
        adapter.admit_live()
        adapter.observe_before_use()
        self.assertEqual(self.life.observed, [self.normal, self.front, self.normal, self.front])
        adapter.close_original_and_assert_backend_release()
        self.assertTrue(adapter.released)
        self.assertEqual(self.front.close_calls, 1)

    def test_arbitrary_driver_refused(self):
        self.life.driver = object()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            self.adapter()

    def test_owner_subclass_refused(self):
        class Replacement(LifetimeNormalHosts):
            pass
        self.normal = Replacement()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            self.adapter()

    def test_late_retention_refused(self):
        self.births()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            self.adapter()

    def test_use_before_admission_refused_sticky(self):
        adapter = self.adapter()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_before_use()
        self.assertTrue(adapter.failed)

    def test_missing_birth_refused(self):
        adapter = self.adapter()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()

    def test_replaced_exact_process_refused(self):
        adapter = self.adapter()
        self.births()
        self.front.process = object()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()

    def test_invented_readers_mode_refused(self):
        adapter = self.adapter()
        self.births()
        self.life.children[0].descriptor_scope = 'pipe-readers'
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()

    def test_unsealed_bootstrap_refused(self):
        adapter = self.adapter()
        self.births()
        self.front.pipe.sealed = False
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()

    def test_caller_supplied_output_reader_refused(self):
        adapter = self.adapter()
        self.births()
        self.life.children[0].process.stdout = object()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()

    def test_original_observation_failure_sticky(self):
        adapter = self.adapter()
        self.births()
        self.life.fail_observation = True
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()
        self.assertTrue(adapter.failed)

    def test_original_release_failure_preserves_custody(self):
        adapter = self.adapter()
        self.births()
        adapter.admit_live()
        self.life.fail_release = True
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.close_original_and_assert_backend_release()
        self.assertIs(adapter.file_row, self.life.children[0])
        self.assertFalse(adapter.released)

    def test_prior_failure_does_not_skip_physical_close(self):
        adapter = self.adapter()
        adapter.failed = True
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.close_original_and_assert_backend_release()
        self.assertEqual(self.front.close_calls, 1)
        self.assertFalse(adapter.released)

    def test_rebound_owner_refused(self):
        adapter = self.adapter()
        self.births()
        adapter.admit_live()
        self.life.normal = object()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_before_use()

    def test_replaced_post_constructor_driver_refused(self):
        adapter = self.adapter()
        self.births()
        self.life.driver = LinuxLifetime()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()
        self.assertEqual(self.life.observed, [])
        self.assertTrue(adapter.failed)

    def front_birth(self):
        self.births()
        self.life.children = [row for row in self.life.children if row.role == 'Front']
        self.normal.owned.clear()
        self.front.pipe.sealed = False
        self.front.pipe.write_fd = 11

    def test_source_prebirth_observation(self):
        adapter = self.adapter()
        adapter.observe_prebirth()
        self.assertEqual(self.life.observed, [])

    def test_prebirth_parent_verify_failure(self):
        adapter = self.adapter()
        self.life.driver.fail_verify = True
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prebirth()

    def test_prepared_front_retains_and_observes_original(self):
        adapter = self.adapter()
        self.front_birth()
        adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, self.life.children[0])
        self.assertEqual(self.life.observed, [self.front, self.front])
        self.assertEqual(self.front.listener_observations, 1)
        self.assertFalse(adapter.admitted)

    def test_first_listener_admission_is_original_api(self):
        adapter = self.adapter()
        self.front_birth()
        row = self.life.children[0]
        row.listener = None
        adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, row)
        self.assertEqual(self.front.listener_observations, 1)
        self.assertEqual(row.listener, 'pure-source-observed-listener')

    def test_missing_original_listener_binding_refuses(self):
        adapter = self.adapter()
        self.front_birth()
        row = self.life.children[0]
        row.listener = None
        self.front.observe_listener = lambda: None
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, row)
        self.assertTrue(adapter.failed)

    def test_pending_file_attempt_refuses_preparation(self):
        adapter = self.adapter()
        self.births()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()

    def test_file_publication_without_child_refuses(self):
        adapter = self.adapter()
        self.front_birth()
        self.normal.owned.append([types.SimpleNamespace(owner='File'), object()])
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()

    def test_prepared_replaced_original_row_refuses(self):
        adapter = self.adapter()
        self.front_birth()
        adapter.observe_prepared_front()
        previous = self.life.children[0]
        self.life.children[0] = types.SimpleNamespace(**vars(previous))
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, previous)

    def test_prepared_bootstrap_substitution_refuses(self):
        adapter = self.adapter()
        self.front_birth()
        self.life.driver.binding = object()
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, self.life.children[0])

    def test_prepared_listener_failure_retains_original_row(self):
        adapter = self.adapter()
        self.front_birth()
        self.front.fail_listener = True
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.observe_prepared_front()
        self.assertIs(adapter.front_row, self.life.children[0])

    def test_prepared_then_live_preserves_same_front(self):
        adapter = self.adapter()
        self.front_birth()
        adapter.observe_prepared_front()
        prepared = adapter.front_row
        process = HeldPopen(self.life)
        self.life.children.append(types.SimpleNamespace(role='File', owner=self.normal,
            process=process, descriptor_scope='inherited-output', birth_attempted=True,
            identity=object(), pidfd=types.SimpleNamespace(closed=False, close_uncertain=False),
            listener='pure-listener', failed=False, reaped=False))
        self.normal.owned.append([types.SimpleNamespace(owner='File'), process])
        self.front.pipe.sealed = True
        self.front.pipe.write_fd = None
        adapter.admit_live()
        self.assertIs(adapter.front_row, prepared)

    def test_admit_replaced_prepared_front_refuses(self):
        adapter = self.adapter()
        self.front_birth()
        adapter.observe_prepared_front()
        prepared = adapter.front_row
        self.births()
        self.life.children = [self.life.children[-2], self.life.children[-1]]
        with self.assertRaises(self.module.ConsumerHandoffRefused):
            adapter.admit_live()
        self.assertIs(adapter.front_row, prepared)


if __name__ == '__main__':
    unittest.main()
