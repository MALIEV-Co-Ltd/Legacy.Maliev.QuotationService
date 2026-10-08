"""Pure causal controls: no subprocess, descriptor, signal or /proc acquisition."""
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
sys.path[:0] = [str(HERE), str(ROOT / 'business-fixture-proposal/held-lifetime-readbacks'),
               str(ROOT / 'quotation/tools/InvoiceCompletionProducerAcceptance/companion')]
import held_host_lifetime as adapter


class Driver:
    actual = False
    def __init__(self):
        self.events = []
        self.identities = {adapter.os.getpid(): (10, 1, 20, 30, b'R')}
        self.stopped = set()
        self.extra = {}
        self.next_pid = 200001
        self.t = 0
        self.fail = None
        self.null = ('/dev/null', 1, 2, 3, adapter.stat.S_IFCHR)
        self.inherited = ('pipe:[100]', 1, 100, 0, adapter.stat.S_IFIFO)
    def now(self): self.t += .001; return self.t
    def generation(self, pid):
        identity = self.identities[pid]
        return (*identity[:4], b'Z' if pid in self.stopped else b'R')
    def pidfd(self, pid):
        if self.fail == 'pidfd': raise RuntimeError()
        self.events.append(('pidfd', pid))
        return adapter.Pidfd(pid)
    def verify(self, handle, identity):
        adapter.require(not handle.closed and not handle.close_uncertain)
        adapter.require(self.generation(identity.pid)[:4] == (identity.start_ticks, identity.parent_pid, identity.process_group, identity.session))
    def mask(self): self.events.append(('mask',)); return 'original-mask'
    def restore(self, original):
        adapter.require(original == 'original-mask')
        self.events.append(('restore',))
    def census(self, parent, end):
        return {**{pid: self.generation(pid) for pid in self.identities if pid != parent}, **self.extra}
    def spawn(self, arguments, options):
        if self.fail == 'spawn': raise RuntimeError()
        self.next_pid += 1
        pid = self.next_pid
        self.identities[pid] = (50, adapter.os.getpid(), 20, 30, b'R')
        self.events.append(('spawn', pid, options['start_new_session']))
        return types.SimpleNamespace(pid=pid, returncode=None, output_null=options.get('stdout') == adapter.subprocess.DEVNULL)
    def descriptors(self, pid):
        if self.fail == 'descriptors' and pid != adapter.os.getpid(): raise RuntimeError()
        if pid == adapter.os.getpid(): return (self.null, self.inherited, self.inherited)
        null = next(row.process.output_null for row in self.scope.children if row.process.pid == pid)
        return (self.null, self.null if null else self.inherited, self.null if null else self.inherited)
    def null_descriptor(self): return self.null[3:]
    def observe(self, row):
        self.verify(row.pidfd, row.identity)
        adapter.require(row.process.returncode is None)
        return adapter.original_exit.ExitObservation(row.process.pid in self.stopped, 0 if row.process.pid in self.stopped else None)
    def bootstrap(self, pipe):
        adapter.require(not pipe.closed)
        return (pipe.read_fd, 'pipe:[42]', 1, 42)
    def bootstrap_child(self, process, binding): self.events.append(('bootstrap-child', process.pid))
    def terminate(self, row, force):
        self.verify(row.pidfd, row.identity)
        self.events.append(('terminate', row.process.pid, force))
        self.stopped.add(row.process.pid)
    def reap(self, row):
        adapter.require(self.observe(row).settled)
        self.events.append(('reap', row.process.pid))
        row.process.returncode = 0
        row.reaped = True
        del self.identities[row.process.pid]
    def listener_absent(self, inode):
        self.events.append(('listener-absent', inode))
        if self.fail == 'listener': raise RuntimeError()
    def close(self, handle):
        adapter.require(not handle.close_uncertain)
        self.events.append(('close-pidfd', handle.fd))
        handle.closed = True
    def pause(self): self.t += .1


class Pipe:
    def __init__(self, driver):
        self.driver = driver
        self.read_fd, self.write_fd = 42, None
        self.closed = False
    def close(self):
        self.driver.events.append(('bootstrap-close',))
        self.read_fd = self.write_fd = None
        self.closed = True


class Controls(unittest.TestCase):
    def fixture(self, front=False):
        driver = Driver()
        scope = adapter.HostLifetime(driver)
        driver.scope = scope
        scope.admit_parent()
        normal = adapter.LifetimeNormalHosts(scope, None, {}, (), '/synthetic/dotnet', '0'*64, 1)
        if front:
            pipe = Pipe(driver)
            owner = adapter.LifetimeFrontHost(scope, None, None, {}, pipe, 1)
        else: owner = normal
        return scope, driver, owner
    def acquire(self, scope, owner, front=False):
        options = {'stdin': adapter.subprocess.DEVNULL, 'start_new_session': False}
        if front: options.update(stdout=adapter.subprocess.DEVNULL, stderr=adapter.subprocess.DEVNULL)
        process = scope.acquire(owner, 'Front' if front else 'File', ['/synthetic/dotnet','/synthetic/owner.dll'], options,
                                'devnull-output' if front else 'inherited-output')
        scope.bind_listener(owner, process, '101')
        return process
    def test_live_original_is_nonreaped(self):
        s,d,o = self.fixture(); p = self.acquire(s,o)
        s.observe_original(o)
        self.assertIsNone(p.returncode)
        self.assertFalse(s.children[0].reaped)
    def test_original_objects_required(self):
        s,d,o = self.fixture(); self.acquire(s,o)
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(object())
        self.assertTrue(s.failed)
    def test_unowned_process_refused(self):
        s,d,o = self.fixture(); self.acquire(s,o)
        with self.assertRaises(adapter.LifetimeRefused): s.observe_process(object())
    def test_cached_exit_refused(self):
        s,d,o = self.fixture(); p = self.acquire(s,o); p.returncode = 0
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_generation_replacement_refused(self):
        s,d,o = self.fixture(); p = self.acquire(s,o)
        d.identities[p.pid] = (999, adapter.os.getpid(),20,30,b'R')
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_parent_generation_replacement_refused(self):
        s,d,o = self.fixture(); self.acquire(s,o)
        d.identities[adapter.os.getpid()] = (999,1,20,30,b'R')
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_closed_pidfd_refused(self):
        s,d,o = self.fixture(); self.acquire(s,o); s.children[0].pidfd.closed=True
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_uncertain_pidfd_refused(self):
        s,d,o = self.fixture(); self.acquire(s,o); s.children[0].pidfd.close_uncertain=True
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_duplicate_birth_refused(self):
        s,d,o = self.fixture(); self.acquire(s,o)
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertEqual(1,len(s.children))
    def test_post_birth_setup_failure_retains_original(self):
        s,d,o = self.fixture(); d.fail='descriptors'
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertEqual(1,len(s.children)); self.assertIsNotNone(s.children[0].process)
        self.assertTrue(s.failed)
    def test_unknown_birth_is_sticky(self):
        s,d,o = self.fixture(); d.fail='spawn'
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertTrue(s.birth_uncertain)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()
    def test_listener_before_pidfd_release(self):
        s,d,o = self.fixture(); p=self.acquire(s,o); s.close()
        self.assertLess(d.events.index(('reap',p.pid)),d.events.index(('listener-absent','101')))
        self.assertLess(d.events.index(('listener-absent','101')),d.events.index(('close-pidfd',p.pid)))
        self.assertFalse(s.release_observation()['ActualLinuxBackendUsed'])
        self.assertFalse(s.release_observation()['GenuineEightHostFinancialAccepted'])
    def test_listener_failure_blocks_release_after_actual_reap(self):
        s,d,o = self.fixture(); self.acquire(s,o); d.fail='listener'
        with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertTrue(s.children[0].reaped)
        self.assertTrue(s.children[0].pidfd.closed)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()
    def test_retry_never_erases_original_refusal(self):
        s,d,o = self.fixture(); self.acquire(s,o); d.fail='listener'
        with self.assertRaises(adapter.LifetimeRefused): s.close()
        d.fail=None; s.close()
        self.assertTrue(s.closed); self.assertTrue(s.failed)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()
    def test_bootstrap_follows_front_reap(self):
        s,d,o=self.fixture(True); p=self.acquire(s,o,True); s.close()
        self.assertLess(d.events.index(('reap',p.pid)),d.events.index(('bootstrap-close',)))
        self.assertTrue(o.pipe.closed)
    def test_bootstrap_identity_change_refused(self):
        s,d,o=self.fixture(True); o.pipe.read_fd=99
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o,True)
    def test_unknown_descendant_never_signalled(self):
        s,d,o=self.fixture(); p=self.acquire(s,o); d.extra[333333]=(1,p.pid,20,30,b'R')
        with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertTrue(s.failed)
        self.assertEqual([p.pid],[e[1] for e in d.events if e[0]=='terminate'])
    def test_listener_rebinding_refused(self):
        s,d,o=self.fixture(); p=self.acquire(s,o)
        with self.assertRaises(adapter.LifetimeRefused): s.bind_listener(o,p,'999')
    def test_exclusive_fence_refuses_reentry(self):
        s,d,o=self.fixture(); self.acquire(s,o)
        with s.fence():
            with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
    def test_signal_mask_restored_on_failed_birth(self):
        s,d,o=self.fixture(); d.fail='spawn'
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertEqual(('restore',),d.events[-1])
    def held_popen(self):
        s,d,o=self.fixture(); old=self.acquire(s,o)
        actual=adapter.HeldPopen.__new__(adapter.HeldPopen)
        actual._child_created=False
        actual.pid,actual.returncode,actual.lifetime=old.pid,None,s
        actual.output_null=False
        s.children[0].process=actual
        return s,d,actual
    def test_actual_poll_algorithm_does_not_reap(self):
        s,d,p=self.held_popen()
        self.assertIsNone(p.poll()); self.assertIsNone(p.returncode)
        self.assertFalse(s.children[0].reaped)
    def test_actual_poll_refuses_settled_and_is_sticky(self):
        s,d,p=self.held_popen(); d.stopped.add(p.pid)
        with self.assertRaises(adapter.LifetimeRefused): p.poll()
        self.assertTrue(s.failed); self.assertIsNone(p.returncode)
    def test_actual_poll_refuses_cached_exit(self):
        s,d,p=self.held_popen(); p.returncode=0
        with self.assertRaises(adapter.LifetimeRefused): p.poll()
        self.assertTrue(s.failed)
    def test_actual_wait_never_delegates_to_popen(self):
        s,d,p=self.held_popen()
        with self.assertRaises(adapter.LifetimeRefused): p.wait()
        self.assertFalse(s.children[0].reaped)
    def test_modeled_scope_cannot_authorize_backend_release(self):
        s,d,o=self.fixture(); self.acquire(s,o); s.close()
        with self.assertRaises(adapter.LifetimeRefused): s.assert_backend_release()
    def test_actual_raw_reap_echild_is_refusal(self):
        driver=adapter.LinuxLifetime()
        row=adapter.Child(object(),'File',types.SimpleNamespace(returncode=None),
                          adapter.original_exit.OriginalIdentity(123,1,2,3,4),adapter.Pidfd(9))
        with patch.object(driver,'verify'),patch.object(driver,'observe',return_value=adapter.original_exit.ExitObservation(True,0)),\
             patch.object(adapter.os,'waitpid',side_effect=ChildProcessError()),\
             patch.object(adapter.os,'WNOHANG',1,create=True):
            with self.assertRaises(ChildProcessError): driver.reap(row)
        self.assertFalse(row.reaped); self.assertIsNone(row.process.returncode)
    def test_actual_uncertain_close_never_retries_raw_fd(self):
        driver=adapter.LinuxLifetime(); handle=adapter.Pidfd(9)
        with patch.object(adapter.os,'close',side_effect=OSError()) as close:
            with self.assertRaises(OSError): driver.close(handle)
            with self.assertRaises(adapter.LifetimeRefused): driver.close(handle)
            self.assertEqual(1,close.call_count)
        self.assertTrue(handle.close_uncertain)
    def test_descriptor_change_sticks_refusal(self):
        s,d,o=self.fixture(); self.acquire(s,o)
        d.inherited=('pipe:[999]',1,999,0,adapter.stat.S_IFIFO)
        with self.assertRaises(adapter.LifetimeRefused): s.observe_original(o)
        self.assertTrue(s.failed)

    def test_parent_census_failure_releases_original_handle_without_new_baseline(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s
        with patch.object(d,'census',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()
        handle=s.parent_pidfd
        self.assertIsNotNone(handle); self.assertIsNone(s.baseline)
        self.assertFalse(s.parent_admitted); self.assertTrue(s.failed)
        with patch.object(d,'census',side_effect=AssertionError('No replacement census')):
            s.close(); s.close()
        self.assertIs(s.parent_pidfd,handle); self.assertTrue(handle.closed)
        self.assertTrue(s.closed); self.assertTrue(s.failed); self.assertIsNone(s.baseline)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()
        with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()

    def test_partial_parent_close_failure_can_retry_same_handle(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s
        with patch.object(d,'census',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()
        handle=s.parent_pidfd
        with patch.object(d,'close',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertFalse(s.closed); self.assertFalse(handle.closed)
        s.close()
        self.assertTrue(handle.closed); self.assertTrue(s.closed); self.assertTrue(s.failed)
        self.assertIs(s.parent_pidfd,handle)

    def test_failed_parent_pidfd_verification_still_closes_acquired_handle(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s
        with patch.object(d,'verify',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()
        handle=s.parent_pidfd; s.close()
        self.assertTrue(handle.closed); self.assertTrue(s.failed)

    def test_failed_parent_generation_has_no_synthetic_handle_to_close(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s
        with patch.object(d,'generation',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()
        self.assertIsNone(s.parent_pidfd); s.close()
        self.assertTrue(s.closed); self.assertTrue(s.failed)
        self.assertFalse(any(event[0]=='close-pidfd' for event in d.events))

    def test_metadata_only_normal_owner_binding_refused(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s; s.admit_parent()
        fake=types.SimpleNamespace(lifetime=s,owned=[])
        with self.assertRaises(adapter.LifetimeRefused): s.bind_normal(fake)
        self.assertIsNone(s.normal); self.assertTrue(s.failed)

    def test_metadata_only_front_owner_binding_refused_without_descriptor_access(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s; s.admit_parent()
        fake=types.SimpleNamespace(lifetime=s,process=None,pipe=object())
        with patch.object(d,'bootstrap',side_effect=AssertionError('Unbound metadata descriptor')):
            with self.assertRaises(adapter.LifetimeRefused): s.bind_front(fake)
        self.assertIsNone(s.front); self.assertIsNone(s.pipe); self.assertTrue(s.failed)

    def test_substituted_subclass_cannot_bind_as_original_normal_wrapper(self):
        class Substituted(adapter.LifetimeNormalHosts): pass
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s; s.admit_parent()
        with self.assertRaises(adapter.LifetimeRefused):
            Substituted(s,None,{},(),'/synthetic/dotnet','0'*64,1)
        self.assertIsNone(s.normal)

    def test_failed_parent_cannot_enroll_real_owner_after_physical_recovery(self):
        d=Driver(); s=adapter.HostLifetime(d); d.scope=s
        with patch.object(d,'census',side_effect=RuntimeError()):
            with self.assertRaises(adapter.LifetimeRefused): s.admit_parent()
        s.close()
        with self.assertRaises(adapter.LifetimeRefused):
            adapter.LifetimeNormalHosts(s,None,{},(),'/synthetic/dotnet','0'*64,1)
        self.assertIsNone(s.normal)

    def test_missing_listener_does_not_skip_parent_or_bootstrap_disposal(self):
        s,d,o=self.fixture(True); d.fail='descriptors'
        with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o,True)
        original=s.children[0].process; d.fail=None
        with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertIs(s.children[0].process,original)
        self.assertTrue(s.children[0].reaped); self.assertTrue(s.children[0].pidfd.closed)
        self.assertTrue(s.parent_pidfd.closed); self.assertTrue(o.pipe.closed)
        self.assertIsNone(s.children[0].listener)
        self.assertFalse(s.children[0].listener_absence_verified)
        self.assertTrue(s.failed)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()

    def test_bootstrap_close_failure_does_not_skip_parent_close_or_retry_uncertain_fd(self):
        s,d,o=self.fixture(True); self.acquire(s,o,True)
        with patch.object(o.pipe,'close',side_effect=OSError()) as closed:
            with self.assertRaises(adapter.LifetimeRefused): s.close()
            self.assertTrue(s.parent_pidfd.closed)
            self.assertIn('bootstrap-close',s.cleanup_failures)
            with self.assertRaises(adapter.LifetimeRefused): s.close()
            self.assertEqual(1,closed.call_count)
        self.assertTrue(s.bootstrap_close_uncertain)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()

    def test_independent_parent_and_bootstrap_failures_are_both_retained(self):
        s,d,o=self.fixture(True); self.acquire(s,o,True)
        original_close=d.close
        def close(handle):
            if handle is s.parent_pidfd: raise OSError()
            return original_close(handle)
        with patch.object(o.pipe,'close',side_effect=OSError()),patch.object(d,'close',side_effect=close):
            with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertIn('bootstrap-close',s.cleanup_failures)
        self.assertIn('parent-close',s.cleanup_failures)
        self.assertTrue(s.failed)

    def test_custody_allocation_failure_prevents_birth(self):
        s,d,o=self.fixture()
        with patch.object(adapter,'Child',side_effect=MemoryError()),patch.object(d,'spawn',wraps=d.spawn) as spawned:
            with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertEqual(0,spawned.call_count); self.assertFalse(s.birth_uncertain)
        s.close(); self.assertTrue(s.parent_pidfd.closed); self.assertTrue(s.failed)

    def test_custody_ledger_append_failure_prevents_birth(self):
        class RefusedList(list):
            def append(self, value): raise MemoryError()
        s,d,o=self.fixture(); s.children=RefusedList()
        with patch.object(d,'spawn',wraps=d.spawn) as spawned:
            with self.assertRaises(adapter.LifetimeRefused): self.acquire(s,o)
        self.assertEqual(0,spawned.call_count); self.assertFalse(s.birth_uncertain)
        s.close(); self.assertTrue(s.parent_pidfd.closed)

    def test_stop_failure_retains_parent_until_exact_child_recovery(self):
        s,d,o=self.fixture(); self.acquire(s,o)
        with patch.object(d,'terminate',side_effect=OSError()):
            with self.assertRaises(adapter.LifetimeRefused): s.close()
        self.assertFalse(s.children[0].reaped); self.assertFalse(s.parent_pidfd.closed)
        s.close()
        self.assertTrue(s.children[0].reaped); self.assertTrue(s.parent_pidfd.closed)
        self.assertTrue(s.failed)
        with self.assertRaises(adapter.LifetimeRefused): s.release_observation()


if __name__ == '__main__': unittest.main()
