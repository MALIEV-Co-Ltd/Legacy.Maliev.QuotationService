"""Pure harness regressions; every OS acquisition function is injected/refused."""
import hashlib
from contextlib import nullcontext
import stat
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import hosted_controls as harness


class Controls(unittest.TestCase):
    def info(self,size,mode=stat.S_IFREG):
        return types.SimpleNamespace(st_mode=mode,st_nlink=1,st_size=size,st_dev=1,st_ino=2,st_mtime_ns=3)
    def test_held_source_read_executes_captured_bytes_without_reopening_path(self):
        registry=harness.CallerRegistry(); raw=b'VALUE=42\n'; info=self.info(len(raw))
        with patch.object(harness.os,'open',return_value=7),patch.object(harness.os,'fstat',return_value=info),\
             patch.object(harness.os,'read',side_effect=[raw,b'']),\
             patch.object(harness.os,'O_NOFOLLOW',1,create=True),patch.object(harness.os,'O_NONBLOCK',2,create=True):
            held=registry.read('/injected/source',65536,hashlib.sha256(raw).hexdigest())
        name='held_source_causal_test'
        with patch.object(harness,'REGISTRY',registry),\
             patch.object(registry,'snapshot',return_value=Path('/injected/private/snapshot.py')),\
             patch.object(Path,'read_bytes',side_effect=AssertionError('Path reopen forbidden')):
            try:
                actual=harness.execute_held_module(name,held,Path('/injected/private'))
                self.assertEqual(42,actual.VALUE)
                self.assertIs(registry.modules[0],actual)
            finally: sys.modules.pop(name,None)
    def test_nonregular_input_retains_fd_and_never_reads(self):
        r=harness.CallerRegistry()
        with patch.object(harness.os,'open',return_value=7),patch.object(harness.os,'fstat',return_value=self.info(1,stat.S_IFIFO)),\
             patch.object(harness.os,'read',side_effect=AssertionError('FIFO read forbidden')),\
             patch.object(harness.os,'O_NOFOLLOW',1,create=True),patch.object(harness.os,'O_NONBLOCK',2,create=True):
            with self.assertRaises(RuntimeError): r.read('/injected/source',65536)
        self.assertEqual(7,r.inputs[0].fd)
    def test_over_cap_input_never_reads(self):
        r=harness.CallerRegistry()
        with patch.object(harness.os,'open',return_value=7),patch.object(harness.os,'fstat',return_value=self.info(65537)),\
             patch.object(harness.os,'read',side_effect=AssertionError('Over-cap read forbidden')),\
             patch.object(harness.os,'O_NOFOLLOW',1,create=True),patch.object(harness.os,'O_NONBLOCK',2,create=True):
            with self.assertRaises(RuntimeError): r.read('/injected/source',65536)
    def test_source_pin_mismatch_never_qualifies_bytes(self):
        r=harness.CallerRegistry(); raw=b'unqualified'; info=self.info(len(raw))
        with patch.object(harness.os,'open',return_value=7),patch.object(harness.os,'fstat',return_value=info),\
             patch.object(harness.os,'read',side_effect=[raw,b'']),\
             patch.object(harness.os,'O_NOFOLLOW',1,create=True),patch.object(harness.os,'O_NONBLOCK',2,create=True):
            with self.assertRaises(RuntimeError): r.read('/injected/source',65536,'0'*64)
        self.assertEqual(7,r.inputs[0].fd)
    def test_scope_record_exists_before_constructor_and_survives_failure(self):
        r=harness.CallerRegistry()
        def fail(**kwargs):
            self.assertEqual(1,len(r.scopes))
            raise RuntimeError('Injected constructor failure')
        with self.assertRaises(RuntimeError): r.new_scope(types.SimpleNamespace(HostLifetime=fail))
        self.assertEqual(1,len(r.scopes))
    def test_pipe_object_is_registered_before_original_constructor(self):
        r=harness.CallerRegistry(); original=object()
        class Pipe:
            def __init__(self):
                self_case.assertIs(r.pipes[0].pipe,self)
                raise RuntimeError('Injected constructor failure')
        self_case=self
        with self.assertRaises(RuntimeError): r.new_pipe(original,types.SimpleNamespace(PublicBootstrapPipe=Pipe))
        self.assertIs(r.pipes[0].scope,original)
        self.assertIsNotNone(r.pipes[0].pipe)
        self.assertFalse(r.pipes[0].initialized)
    def test_preexisting_module_cannot_replace_held_module(self):
        name='held_source_causal_test'; actual=types.ModuleType(name)
        with patch.dict(sys.modules,{name:actual}):
            with self.assertRaises(RuntimeError): harness.execute_held_module(name,b'VALUE=99',Path('/injected'))
        self.assertFalse(hasattr(actual,'VALUE'))
    def test_uncertain_source_fd_is_not_reclosed(self):
        r=harness.CallerRegistry(); r.inputs.append(harness.HeldInput(fd=7,uncertain=True))
        with patch.object(harness.os,'close',side_effect=AssertionError('Uncertain close retry')),\
             patch.object(harness.signal,'SIGALRM',14,create=True),\
             patch.object(harness.signal,'ITIMER_REAL',0,create=True),\
             patch.object(harness.signal,'getsignal',return_value=harness.signal.SIG_DFL),\
             patch.object(harness.signal,'getitimer',return_value=(0.,0.),create=True):
            self.assertFalse(r.cleanup())
        self.assertTrue(r.cleanup_failed)
    def test_bootstrap_writer_close_uncertainty_refuses_raw_retry(self):
        r=harness.CallerRegistry()
        scope=types.SimpleNamespace(fence=lambda:nullcontext(),bootstrap_close_uncertain=False)
        pipe=types.SimpleNamespace(write_fd=7)
        r.pipes.append(harness.PipeCustody(scope,pipe,True))
        with patch.object(harness,'REGISTRY',r),patch.object(harness.os,'close',side_effect=OSError()) as close:
            with self.assertRaises(RuntimeError): harness.close_bootstrap_writer(scope,pipe)
            with self.assertRaises(RuntimeError): harness.close_bootstrap_writer(scope,pipe)
            self.assertEqual(1,close.call_count)
        self.assertTrue(r.pipes[0].uncertain)
        self.assertTrue(scope.bootstrap_close_uncertain)
    def test_timer_restoration_is_independent_after_normal_close_failure(self):
        r=harness.CallerRegistry(); events=[]
        class Normal:
            timer_owned=True
            def expire(self,*args): pass
            def close(self): events.append('normal-close'); raise RuntimeError()
            def release_timer(self): events.append('timer-release'); self.timer_owned=False
        normal=Normal()
        scope=types.SimpleNamespace(normal=normal,birth_uncertain=False,children=[],parent_pidfd=None,
                                    close=lambda:events.append('scope-close'),fence=lambda:nullcontext())
        r.scopes.append(harness.ScopeCustody(scope,normal))
        with patch.object(harness.signal,'getsignal',side_effect=[normal.expire,harness.signal.SIG_DFL]),\
             patch.object(harness.signal,'getitimer',return_value=(0.,0.),create=True),\
             patch.object(harness.signal,'SIGALRM',14,create=True),\
             patch.object(harness.signal,'ITIMER_REAL',0,create=True):
            self.assertTrue(r.cleanup())
        self.assertEqual(['normal-close','scope-close','timer-release'],events)
        self.assertFalse(normal.timer_owned)
    def test_stage_category_refuses_arbitrary_private_text(self):
        with self.assertRaises(RuntimeError): harness.stage('/injected/private/path')


if __name__=='__main__': unittest.main()
