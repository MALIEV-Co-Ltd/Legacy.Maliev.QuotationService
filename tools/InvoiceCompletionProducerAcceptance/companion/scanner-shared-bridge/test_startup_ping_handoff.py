"""Pure causal ownership models only; no socket, thread or fixture is born."""
import errno
import types
import unittest
from unittest.mock import patch

HELD_RELAY_SOURCE = None
HELD_SCANNER_SOURCE = None


class StartupTests(unittest.TestCase):
    def model(self, outcome='refused', fault=None):
        module=types.ModuleType('held_startup_relay_model')
        exec(compile(HELD_RELAY_SOURCE,'<held-startup-relay>','exec'),module.__dict__)
        relay=object.__new__(module.LoopbackRelay)
        relay.lock=__import__('threading').Lock()
        relay.stop=types.SimpleNamespace(is_set=lambda:False)
        relay.sockets=set();relay.workers=set();relay.failures=[]
        relay._startup_tracking=True;relay._startup_attempts=[];relay._startup_peers={};relay._startup_bad=False;relay._startup_sealed=False;relay._startup_unhandled=[];relay._startup_overflow=None;relay._startup_admission=None
        relay.endpoint=('127.0.0.1',7);relay.seconds=10;relay.maximum_bytes=1024
        relay._model_outcome=outcome;relay._model_budgets=[];relay._model_worker_clock=None
        operations=[];clock=[0.0];created=[];threads=[]
        class Sock:
            def __init__(self,kind):
                self.kind=kind;self.closed=False;self.data=b'';self.original_error=OSError('PRIVATE');self.getter_failed=False;self.peer=('127.0.0.1',100+len(created))
            def fileno(self):
                if fault=='accepted-fileno' and self.kind=='accepted' and not self.getter_failed:
                    self.getter_failed=True;raise self.original_error
                return -1 if self.closed else 8
            def bind(self,value):operations.append('bind')
            def getsockname(self):return self.peer
            def getpeername(self):return relay.endpoint
            def settimeout(self,value):
                operations.append(self.kind+'-timeout')
                if self.kind=='backend':relay._model_budgets.append(value)
                if fault=='backend-timeout' and self.kind=='backend':raise ConnectionRefusedError(errno.ECONNREFUSED,'PRIVATE')
            def connect(self,value):
                operations.append(self.kind+'-connect')
                if self.kind=='caller':relay._accept()
                elif fault=='backend-base':raise KeyboardInterrupt('PRIVATE')
                elif relay._model_outcome=='refused':raise ConnectionRefusedError(errno.ECONNREFUSED,'PRIVATE')
                elif relay._model_outcome=='wrong-errno':raise ConnectionRefusedError(errno.EHOSTUNREACH,'PRIVATE')
            def sendall(self,value):
                operations.append(self.kind+'-send')
                if self.kind=='caller':
                    if value!=b'zPING\0':raise AssertionError('Source did not send exact PING')
                    if threads:threads[-1].run()
                elif self.kind=='accepted':relay._model_client.data+=value
            def recv(self,count):
                if self.kind=='accepted':
                    if not self.data:self.data=b'zVERSION\0' if fault=='foreign-frame' else b'zPING\0'
                    value=self.data[:count];self.data=self.data[count:];return value
                if self.kind=='backend':return b'PONG\0'
                value=self.data[:count];self.data=self.data[count:];return value
            def close(self):
                operations.append(self.kind+'-close')
                if fault==self.kind+'-close':raise self.original_error
                if fault=='capacity-close' and self.kind=='accepted':raise OSError('PRIVATE')
                if fault==self.kind+'-close-base':raise KeyboardInterrupt('PRIVATE')
                self.closed=True
        accepted=Sock('accepted');seen=set()
        def accept():
            peer=relay._model_client.peer
            if peer in seen:raise OSError('model stop accept loop')
            seen.add(peer);return Sock('accepted'),('127.0.0.1',999) if fault=='foreign-peer' else peer
        relay.listener=types.SimpleNamespace(accept=accept)
        class Worker:
            def __init__(self,target,args,**kwargs):
                operations.append('worker-ctor')
                if fault=='worker-ctor':raise RuntimeError('PRIVATE')
                self.target=target;self.args=args;self.live=False;threads.append(self)
            def start(self):
                operations.append('worker-start')
                if fault=='worker-start':self.live=True;raise RuntimeError('PRIVATE')
            def run(self):
                if relay._model_worker_clock is not None:clock[0]=relay._model_worker_clock
                self.live=True
                try:self.target(*self.args)
                finally:self.live=False
            def join(self,timeout):
                operations.append('worker-join')
                if fault=='worker-join':raise RuntimeError('PRIVATE')
                if fault=='late-join':clock[0]=11.0
            def is_alive(self):return self.live
        capacity=[];relay.capacity=types.SimpleNamespace(acquire=lambda **k:fault!='capacity-close',release=lambda:capacity.append(True))
        def allocate(*args):
            operations.append('socket-ctor')
            caller=relay._startup_attempts[-1].client is None
            if (fault=='caller-ctor' and caller) or (fault=='backend-ctor' and not caller):raise ConnectionRefusedError(errno.ECONNREFUSED,'PRIVATE')
            value=Sock('caller' if caller else 'backend');created.append(value)
            if caller:relay._model_client=value
            return value
        def validate():
            if fault=='validation':raise ConnectionRefusedError(errno.ECONNREFUSED,'PRIVATE')
            if fault=='validation-base':raise KeyboardInterrupt('PRIVATE')
            if fault=='validation-exit':raise SystemExit('PRIVATE')
            return ('172.31.0.2',3310)
        relay.validate_backend=validate
        def current():return threads[-1] if threads else None
        contexts=(patch.object(module.socket,'socket',side_effect=allocate),
                  patch.object(module.threading,'Thread',Worker),patch.object(module.threading,'current_thread',side_effect=current),
                  patch.object(module.time,'monotonic',side_effect=lambda:clock[0]))
        return module,relay,operations,created,threads,accepted,contexts

    def invoke(self,model):
        from contextlib import ExitStack
        module,relay,operations,created,threads,accepted,contexts=model
        with ExitStack() as stack:
            for context in contexts:stack.enter_context(context)
            return relay.startup_ping()

    def test_actual_source_model_pong_settles_and_seals_without_history(self):
        m=self.model('pong');self.assertEqual(self.invoke(m),'PONG')
        relay=m[1];self.assertTrue(relay._startup_attempts[0].settled)
        proof=relay.seal_startup_readiness();self.assertEqual(proof['originalSettledConnectRefusals'],0)
        self.assertTrue(relay.ready_history_valid());self.assertEqual(relay.failures,[])

    def test_connect_refusal_retains_exact_exception_and_original_history(self):
        m=self.model();module,relay=m[:2]
        with self.assertRaises(module.StartupPingRefused):self.invoke(m)
        row=relay._startup_attempts[0]
        self.assertTrue(row.accounted and row.settled);self.assertIs(type(row.original_failure),ConnectionRefusedError)
        self.assertEqual(row.failure_index,0);self.assertEqual(relay.failures,['ConnectionRefusedError'])
        self.assertIn('worker-join',m[2]);self.assertFalse(relay.ready_history_valid())

    def test_refused_then_actual_model_pong_seals_full_unchanged_rows(self):
        m=self.model();module,relay=m[:2]
        with self.assertRaises(module.StartupPingRefused):self.invoke(m)
        original_error=relay._startup_attempts[0].original_failure
        original_rows=tuple(relay.failures)
        relay._model_outcome='pong';self.assertEqual(self.invoke(m),'PONG')
        proof=relay.seal_startup_readiness()
        self.assertEqual(proof['originalStartupAttempts'],2)
        self.assertEqual(proof['originalSettledConnectRefusals'],1)
        self.assertEqual(tuple(relay.failures),original_rows)
        self.assertIs(relay._startup_attempts[0].original_failure,original_error)
        self.assertTrue(relay.ready_history_valid())

    def test_foreign_frame_location_errno_and_constructor_never_account(self):
        for outcome,fault in [('wrong-errno',None),('refused','foreign-frame'),('refused','validation'),
                              ('refused','backend-timeout'),('refused','backend-ctor'),('refused','foreign-peer')]:
            m=self.model(outcome,fault)
            with self.assertRaises(Exception):self.invoke(m)
            self.assertTrue(m[1]._startup_bad);self.assertFalse(m[1]._startup_attempts[0].accounted)

    def test_caller_close_fault_still_attempts_original_worker_join_once(self):
        m=self.model(fault='caller-close')
        with self.assertRaises(ValueError):self.invoke(m)
        self.assertEqual(m[2].count('caller-close'),1);self.assertIn('worker-join',m[2])
        self.assertTrue(m[1]._startup_bad);self.assertFalse(m[1]._startup_attempts[0].settled)

    def test_original_worker_join_error_or_late_completion_refuses_seal(self):
        for fault in ('worker-join','late-join'):
            m=self.model(fault=fault)
            with self.assertRaises(ValueError):self.invoke(m)
            self.assertIn('caller-close',m[2]);self.assertTrue(m[1]._startup_bad)
            with self.assertRaises(ValueError):m[1].seal_startup_readiness()

    def test_partial_birth_retains_owner_and_blocks_next_socket_birth(self):
        for fault in ('caller-ctor','worker-ctor','worker-start'):
            m=self.model(fault=fault)
            with self.assertRaises(Exception):self.invoke(m)
            before=m[2].count('socket-ctor')
            with self.assertRaises(ValueError):self.invoke(m)
            self.assertEqual(before,m[2].count('socket-ctor'));self.assertEqual(len(m[1]._startup_attempts),1)
            self.assertTrue(m[1]._startup_bad)

    def test_worker_close_fault_attempts_backend_close_and_retains_failure(self):
        m=self.model('pong','accepted-close')
        with self.assertRaises(ValueError):self.invoke(m)
        self.assertIn('backend-close',m[2]);self.assertTrue(m[1]._startup_bad)
        self.assertFalse(m[1]._startup_attempts[0].settled)

    def test_capacity_history_or_postseal_changes_do_not_admit(self):
        m=self.model('pong');self.invoke(m);relay=m[1];relay.seal_startup_readiness()
        relay.failures.append('Unknown');self.assertFalse(relay.ready_history_valid())
        with self.assertRaises(ValueError):self.invoke(m)
        self.assertEqual(len(relay._startup_attempts),1)
        n=self.model();n[1]._startup_attempts=[None]*16
        with self.assertRaises(ValueError):self.invoke(n)
        self.assertEqual(n[2],[]);self.assertTrue(n[1]._startup_bad)

    def test_original_row_or_peer_substitution_refuses_history(self):
        m=self.model()
        with self.assertRaises(m[0].StartupPingRefused):self.invoke(m)
        relay=m[1];row=relay._startup_attempts[0]
        row.succeeded=True;self.assertFalse(relay._startup_history_valid());row.succeeded=False
        row.failure_index=1;self.assertFalse(relay._startup_history_valid());row.failure_index=0
        relay._startup_peers[row.peer]=object();self.assertFalse(relay._startup_history_valid())

    def test_unknown_failure_history_cannot_be_baselined_at_seal(self):
        m=self.model('pong');self.invoke(m);relay=m[1]
        relay.failures.append('ConnectionRefusedError')
        with self.assertRaises(ValueError):relay.seal_startup_readiness()
        self.assertTrue(relay._startup_bad);self.assertFalse(relay.ready_history_valid())

    def test_unsettled_prior_attempt_blocks_next_birth_and_stays_retained(self):
        m=self.model();module,relay=m[:2]
        original=module._StartupAttempt();relay._startup_attempts.append(original)
        with self.assertRaises(ValueError):self.invoke(m)
        self.assertEqual(m[2],[]);self.assertIs(relay._startup_attempts[0],original)
        self.assertTrue(relay._startup_bad)

    def test_worker_baseexception_is_retained_unhandled_and_blocks_seal(self):
        for fault,kind in (('validation-base','KeyboardInterrupt'),('validation-exit','SystemExit')):
            m=self.model(fault=fault)
            with self.assertRaises(ValueError):self.invoke(m)
            self.assertEqual(m[1].failures,[kind])
            self.assertTrue(m[1]._startup_bad)
            self.assertFalse(m[1]._startup_attempts[0].accounted)
            self.assertIn('accepted-close',m[2])
            with self.assertRaises(ValueError):m[1].seal_startup_readiness()

    def test_interrupted_connect_and_final_close_keep_original_unhandled_rows(self):
        for fault in ('backend-base','accepted-close-base','caller-close-base'):
            m=self.model('pong',fault)
            with self.assertRaises(ValueError):self.invoke(m)
            relay=m[1]
            self.assertIn('KeyboardInterrupt',relay.failures)
            self.assertTrue(any(type(error) is KeyboardInterrupt for index,error in relay._startup_unhandled))
            self.assertTrue(relay._startup_bad)
            self.assertIn('worker-join',m[2])
            if fault=='accepted-close-base':self.assertIn('backend-close',m[2])
            with self.assertRaises(ValueError):relay.seal_startup_readiness()
            before=m[2].count('socket-ctor')
            with self.assertRaises(ValueError):self.invoke(m)
            self.assertEqual(before,m[2].count('socket-ctor'))

    def test_capacity_rejection_close_fault_retains_original_accepted_socket(self):
        m=self.model(fault='capacity-close')
        with self.assertRaises(ValueError):self.invoke(m)
        relay=m[1]
        accepted=[row for row in relay.sockets if row.kind=='accepted']
        self.assertEqual(len(accepted),1)
        self.assertEqual(accepted[0].fileno(),8)
        self.assertTrue(relay._startup_bad)
        self.assertIn('OSError',relay.failures)
        self.assertFalse(relay._startup_attempts[0].settled)
        with self.assertRaises(ValueError):relay.seal_startup_readiness()

    def test_each_close_or_getter_exception_has_exactly_one_original_row(self):
        for fault in ('accepted-close','accepted-fileno'):
            m=self.model('pong',fault)
            with self.assertRaises(ValueError):self.invoke(m)
            relay=m[1];original=relay._startup_attempts[0].accepted.original_error
            self.assertEqual(relay.failures,['OSError'])
            self.assertEqual(len(relay._startup_unhandled),1)
            self.assertEqual(relay._startup_unhandled[0][0],0)
            self.assertIs(relay._startup_unhandled[0][1],original)
            self.assertIn('accepted-close',m[2]);self.assertIn('backend-close',m[2])
            self.assertTrue(relay._startup_bad)

    def test_original_startup_worker_uses_the_same_retained_deadline(self):
        m=self.model('pong');relay=m[1];relay._model_worker_clock=2.0
        self.assertEqual(self.invoke(m),'PONG')
        self.assertEqual(relay._startup_attempts[0].deadline,3.0)
        self.assertTrue(relay._model_budgets)
        self.assertTrue(all(value==1.0 for value in relay._model_budgets))
        self.assertTrue(relay._startup_attempts[0].settled)

    def test_public_history_refusal_and_shape_error_are_sticky(self):
        for malformed in (False,True):
            m=self.model('pong');relay=m[1];self.invoke(m);relay.seal_startup_readiness()
            original=relay._startup_attempts[0]
            if malformed:relay._startup_attempts[0]=None
            else:relay.failures.append('Unknown')
            self.assertFalse(relay.ready_history_valid());self.assertTrue(relay._startup_bad)
            if malformed:
                self.assertEqual(len(relay._startup_unhandled),1)
                self.assertIs(type(relay._startup_unhandled[0][1]),AttributeError)
                relay._startup_attempts[0]=original
            else:relay.failures.clear()
            self.assertFalse(relay.ready_history_valid())

    def test_postseal_peer_reuse_is_ordinary_but_ordinary_failure_is_sticky(self):
        from contextlib import ExitStack
        m=self.model('pong');module,relay=m[:2];self.invoke(m);relay.seal_startup_readiness()
        original=relay._startup_attempts[0];oldaccepted=original.accepted
        client=type(original.client)('caller');client.peer=original.peer;relay._model_client=client
        seen=[False]
        def accept():
            if seen[0]:raise OSError('model stop')
            seen[0]=True;return type(client)('accepted'),original.peer
        relay.listener=types.SimpleNamespace(accept=accept)
        with ExitStack() as stack:
            for context in m[-1]:stack.enter_context(context)
            relay._accept()
            worker=m[4][-1]
            self.assertIsNone(worker.args[1]);self.assertFalse(relay._startup_bad)
            self.assertIs(original.accepted,oldaccepted)
            relay._model_outcome='refused';worker.run()
        self.assertTrue(relay._startup_bad);self.assertFalse(relay.ready_history_valid())
        self.assertEqual(relay.failures,['ConnectionRefusedError'])

    def test_closed_foreign_and_capacity_rejections_remain_sticky(self):
        for foreign in (False,True):
            m=self.model('pong');relay=m[1];module=m[0]
            client=types.SimpleNamespace(fileno=lambda:-1,close=lambda:None)
            seen=[False]
            def accept():
                if seen[0]:raise OSError('model stop')
                seen[0]=True;return client,('192.0.2.1' if foreign else '127.0.0.1',7)
            relay.listener=types.SimpleNamespace(accept=accept)
            relay.capacity=types.SimpleNamespace(acquire=lambda **k:False)
            # SimpleNamespace is unhashable; preserve actual rooted socket semantics.
            class Accepted:
                def fileno(self):return -1
                def close(self):pass
            client=Accepted()
            relay._accept()
            self.assertTrue(relay._startup_bad);self.assertEqual(relay.sockets,set())

    def test_unmatched_preseal_successful_forward_cannot_seal_startup(self):
        from contextlib import ExitStack
        m=self.model('pong');module,relay=m[:2];self.invoke(m)
        original=relay._startup_attempts[0]
        client=type(original.client)('caller');client.peer=('127.0.0.1',900)
        relay._model_client=client;seen=[False]
        def accept():
            if seen[0]:raise OSError('model stop')
            seen[0]=True;return type(client)('accepted'),client.peer
        relay.listener=types.SimpleNamespace(accept=accept)
        with ExitStack() as stack:
            for context in m[-1]:stack.enter_context(context)
            count=len(m[4]);relay._accept()
            self.assertEqual(len(m[4]),count)
        self.assertEqual(relay.failures,[])
        self.assertEqual(relay.sockets,set())
        self.assertTrue(relay._startup_bad)
        with self.assertRaises(ValueError):relay.seal_startup_readiness()
        self.assertFalse(relay.ready_history_valid())

    def test_unmatched_preseal_close_and_capacity_faults_retain_originals(self):
        for close_fault,capacity_fault in ((True,False),(False,True),(True,True)):
            m=self.model('pong');relay=m[1];self.invoke(m)
            close_error=KeyboardInterrupt('PRIVATE');release_error=SystemExit('PRIVATE')
            calls=[];seen=[False]
            class Accepted:
                closed=False
                def close(self):
                    calls.append('close')
                    if close_fault:raise close_error
                    self.closed=True
                def fileno(self):return -1 if self.closed else 8
            client=Accepted()
            def accept():
                if seen[0]:raise OSError('model stop')
                seen[0]=True;return client,('127.0.0.1',999)
            def release():
                calls.append('release')
                if capacity_fault:raise release_error
            relay.listener=types.SimpleNamespace(accept=accept)
            relay.capacity=types.SimpleNamespace(acquire=lambda **k:True,release=release)
            count=len(m[4]);relay._accept()
            self.assertEqual(len(m[4]),count);self.assertEqual(calls,['close','release'])
            self.assertEqual(client in relay.sockets,close_fault)
            errors=[item[1] for item in relay._startup_unhandled]
            self.assertEqual(len(errors),int(close_fault)+int(capacity_fault))
            if close_fault:self.assertIs(errors[0],close_error)
            if capacity_fault:self.assertIs(errors[-1],release_error)
            self.assertTrue(relay._startup_bad)
            with self.assertRaises(ValueError):relay.seal_startup_readiness()

    def test_unmatched_cleanup_uncertainty_forbids_next_accept(self):
        for fault in ('close','getter','capacity','open'):
            m=self.model('pong');relay=m[1];self.invoke(m)
            original_error=KeyboardInterrupt('PRIVATE');calls=[]
            class Accepted:
                closed=False
                def close(self):
                    calls.append('close')
                    if fault=='close':raise original_error
                    if fault!='open':self.closed=True
                def fileno(self):
                    calls.append('getter')
                    if fault=='getter':raise original_error
                    return -1 if self.closed else 8
            client=Accepted();accept_count=[0]
            def accept():
                accept_count[0]+=1
                if accept_count[0]>1:raise OSError('model stop')
                return client,('127.0.0.1',999)
            def release():
                calls.append('release')
                if fault=='capacity':raise original_error
            relay.listener=types.SimpleNamespace(accept=accept)
            relay.capacity=types.SimpleNamespace(acquire=lambda **k:True,release=release)
            count=len(m[4]);relay._accept()
            self.assertEqual(accept_count[0],1);self.assertEqual(len(m[4]),count)
            self.assertEqual(calls,['close','getter','release'])
            self.assertTrue(relay._startup_bad)
            self.assertEqual(client in relay.sockets,fault in ('close','getter','open'))
            if fault!='open':
                self.assertEqual(len(relay._startup_unhandled),1)
                self.assertIs(relay._startup_unhandled[0][1],original_error)
            with self.assertRaises(ValueError):relay.seal_startup_readiness()

    def test_default_ordinary_mode_connects_before_frame_and_idle_cleanup(self):
        from contextlib import ExitStack
        for idle in (False,True):
            m=self.model('pong');module,relay=m[:2];self.invoke(m)
            relay._startup_tracking=False;original=relay._startup_attempts[0]
            client=type(original.client)('caller');client.peer=('127.0.0.1',901)
            relay._model_client=client;seen=[False]
            def accept():
                if seen[0]:raise OSError('model stop')
                seen[0]=True;return type(client)('accepted'),client.peer
            relay.listener=types.SimpleNamespace(accept=accept)
            original_recv=type(client).recv
            def recv(actual,count):
                if idle and actual.kind=='accepted':raise TimeoutError('PRIVATE')
                return original_recv(actual,count)
            m[2].clear()
            with ExitStack() as stack:
                for context in m[-1]:stack.enter_context(context)
                stack.enter_context(patch.object(type(client),'recv',recv))
                relay._accept();worker=m[4][-1]
                self.assertIsNone(worker.args[1]);self.assertFalse(relay._startup_bad)
                worker.run()
            self.assertLess(m[2].index('backend-connect'),m[2].index('accepted-timeout'))
            self.assertIn('accepted-close',m[2]);self.assertIn('backend-close',m[2])
            self.assertFalse(worker.is_alive());self.assertEqual(relay.sockets,set())
            self.assertEqual(relay.failures,['TimeoutError'] if idle else [])

    def test_default_mode_cannot_lazily_enable_startup_admission(self):
        m=self.model('pong');relay=m[1];relay._startup_tracking=False
        with self.assertRaises(ValueError):self.invoke(m)
        self.assertEqual(m[3],[]);self.assertEqual(relay._startup_attempts,[])

    def test_admission_cap_snapshot_preserves_original_counts_before_bad(self):
        m=self.model();module,relay=m[:2]
        for index in range(16):
            with self.assertRaises(module.StartupPingRefused):self.invoke(m)
        with self.assertRaisesRegex(ValueError,'Startup PING owner quarantined or exhausted'):self.invoke(m)
        value=relay.startup_admission_diagnostic()
        self.assertEqual([value[n] for n in ('attempts','history','accounted','succeeded','settled')],[16,16,16,0,16])
        self.assertFalse(value['badBefore']);self.assertTrue(relay._startup_bad)
        self.assertTrue(value['capPredicate']);self.assertEqual(value['priorInvalidPredicate'],'UNKNOWN')
        self.assertFalse(value['stopPredicate']);self.assertFalse(value['badPredicate']);self.assertFalse(value['sealedPredicate'])
        self.assertEqual(len(relay._startup_attempts),16);self.assertEqual(len(relay.failures),16)

    def test_admission_short_circuit_and_first_snapshot_are_preserved(self):
        for first in ('stop','bad','sealed'):
            m=self.model();relay=m[1]
            relay.stop=types.SimpleNamespace(is_set=lambda:first=='stop')
            relay._startup_bad=first=='bad';relay._startup_sealed=first=='sealed'
            with self.assertRaises(ValueError):self.invoke(m)
            value=relay.startup_admission_diagnostic()
            order=('stopPredicate','badPredicate','sealedPredicate','capPredicate','priorInvalidPredicate')
            index=('stop','bad','sealed').index(first)
            self.assertTrue(value[order[index]])
            self.assertTrue(all(value[n]=='UNKNOWN' for n in order[index+1:]))
            relay._startup_bad=False
            with self.assertRaises(ValueError):
                relay._startup_bad=True;self.invoke(m)
            self.assertEqual(relay.startup_admission_diagnostic(),value)
            self.assertEqual(m[3],[])

    def test_unknown_owned_shape_never_invokes_foreign_getters(self):
        m=self.model();relay=m[1]
        class Foreign:
            def __getattribute__(self,name):raise AssertionError('PRIVATE getter executed')
        relay._startup_attempts=[Foreign()];relay._startup_bad=True
        with self.assertRaises(ValueError):self.invoke(m)
        value=relay.startup_admission_diagnostic()
        self.assertEqual(value['attempts'],1)
        for name in ('accounted','succeeded','settled'):self.assertEqual(value[name],'UNKNOWN')
        self.assertEqual(value['capPredicate'],'UNKNOWN');self.assertEqual(value['priorInvalidPredicate'],'UNKNOWN')

    def test_original_malformed_prior_guard_error_is_not_rewritten(self):
        m=self.model();module,relay=m[:2]
        row=module._StartupAttempt();del row.settled;relay._startup_attempts=[row]
        with self.assertRaises(AttributeError):self.invoke(m)
        value=relay.startup_admission_diagnostic()
        self.assertEqual(value['settled'],'UNKNOWN');self.assertEqual(value['priorInvalidPredicate'],'UNKNOWN')
        self.assertFalse(value['badBefore']);self.assertFalse(relay._startup_bad)

    def test_diagnostic_projection_is_finite_private_free_and_detached(self):
        m=self.model();relay=m[1];relay._startup_bad=True
        with self.assertRaises(ValueError):self.invoke(m)
        value=relay.startup_admission_diagnostic();encoded=__import__('json').dumps(value)
        self.assertNotIn('PRIVATE',encoded);self.assertNotIn('peer',encoded);self.assertNotIn('error',encoded)
        value['history']=16;self.assertEqual(relay.startup_admission_diagnostic()['history'],0)
        for replacement in (16.0,True,17,-1,{},'PRIVATE'):
            original=relay._startup_admission['history'];relay._startup_admission['history']=replacement
            self.assertIsNone(relay.startup_admission_diagnostic())
            relay._startup_admission['history']=original

    def test_original_scanner_projection_failure_cannot_replace_refusal(self):
        import ast
        tree=ast.parse(HELD_SCANNER_SOURCE)
        cls=next(node for node in tree.body if isinstance(node,ast.ClassDef) and node.name=='Scanner')
        start=next(node for node in cls.body if isinstance(node,ast.FunctionDef) and node.name=='start')
        loop=next(node for node in start.body if isinstance(node,ast.While))
        function=ast.FunctionDef(name='run',args=ast.arguments(posonlyargs=[],args=[ast.arg(arg='self')],kwonlyargs=[],kw_defaults=[],defaults=[]),body=[loop],decorator_list=[])
        module=ast.fix_missing_locations(ast.Module(body=[function],type_ignores=[]))
        for projection_fault in (False,True):
            original=ValueError('PRIVATE original');projected={'schemaVersion':1}
            def ping(**kwargs):raise original
            def project():
                if projection_fault:raise KeyboardInterrupt('PRIVATE projection')
                return projected
            ns={'time':types.SimpleNamespace(monotonic=lambda:0),'deadline':120,'StartupPingRefused':type('StartupPingRefused',(OSError,),{})}
            exec(compile(module,'<held-scanner-failure-loop>','exec'),ns)
            owner=types.SimpleNamespace(relay=types.SimpleNamespace(startup_ping=ping,startup_admission_diagnostic=project),receipt={})
            with self.assertRaises(ValueError) as caught:ns['run'](owner)
            self.assertIs(caught.exception,original)
            self.assertEqual('startupAdmissionDiagnostic' in owner.receipt,not projection_fault)
