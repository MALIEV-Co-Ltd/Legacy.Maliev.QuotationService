"""Pure mocked source-reader/schema controls; no production actor acquisition."""
from contextlib import ExitStack
from dataclasses import dataclass
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import stat
from types import SimpleNamespace
from unittest.mock import patch


spec = importlib.util.spec_from_file_location('phase_control_subject', Path(__file__).with_name('hosted_controls.py'))
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


def controls():
    passed = 0
    raw = b'source-known-before-import'
    digest = hashlib.sha256(raw).hexdigest()
    info = SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_size=len(raw), st_dev=1, st_ino=2, st_mtime_ns=3)
    with ExitStack() as stack:
        stack.enter_context(patch.object(subject.os, 'O_NOFOLLOW', 131072, create=True))
        opened = stack.enter_context(patch.object(subject.os, 'open', return_value=17))
        stack.enter_context(patch.object(subject.os, 'fstat', return_value=info))
        stack.enter_context(patch.object(subject.os, 'fdopen', side_effect=lambda *args, **kwargs: io.BytesIO(raw)))
        closed = stack.enter_context(patch.object(subject.os, 'close'))
        assert subject.held_bytes('synthetic-source', len(raw), digest) == raw
        assert opened.call_args.args[1] & subject.os.O_NOFOLLOW and closed.call_count == 1
        passed += 1
        for wrong_size, wrong_digest in ((len(raw) - 1, digest), (len(raw), '0' * 64)):
            try:
                subject.held_bytes('synthetic-source', wrong_size, wrong_digest)
            except RuntimeError:
                pass
            else:
                raise AssertionError('Source substitution must refuse before import')
            passed += 1
    expected = {
        'unsettled': {'UnsettledObserved', 'NonreapingZombieObserved'},
        'nonzero': {'UnsettledObserved', 'NonreapingZombieObserved', 'NonzeroObserved'},
        'live-preack': {'CollectorEofBeforeAck', 'ProducerStderrLiveAndServiced', 'GuardedAckObserved'},
        'substitution': {'SubstitutionRefused'},
        'blocked-ack': {'CollectorEofBeforeAck', 'ProducerStderrLiveAndServiced', 'BlockedWriteObserved', 'UnsolicitedOutputRefused'},
        'preack-nonzero': {'NonzeroPreackRefused'},
    }
    for case in subject.CASES:
        value = {'Case': case, 'Passed': True,
                 **{name: name in expected[case] for name in subject.FLAG_NAMES},
                 **{name: True for name in subject.CLEANUP_NAMES},
                 **{name: False for name in subject.FALSE_NAMES}}
        subject.validate_public_case(value); passed += 1
        for mutation in ({**value, 'privatePid': 123}, {**value, 'Passed': 1},
                         {**value, 'HeldPidfdsClosed': False}, {**value, 'FinancialBusinessGraphAccepted': True}):
            try:
                subject.validate_public_case(mutation)
            except RuntimeError:
                pass
            else:
                raise AssertionError('Public schema must reject substitution')
            passed += 1
    class ExpectedRefusal(Exception):
        pass
    subject.reject(lambda: (_ for _ in ()).throw(ExpectedRefusal()), ExpectedRefusal); passed += 1
    try:
        subject.reject(lambda: (_ for _ in ()).throw(TypeError()), ExpectedRefusal)
    except TypeError:
        passed += 1
    else:
        raise AssertionError('Unrelated failures must never pass a causal refusal control')
    try:
        subject.unique_pairs([('private', 1), ('private', 2)])
    except RuntimeError:
        passed += 1
    else:
        raise AssertionError('Duplicate keys must refuse')
    subject.blocked_guard_evidence(True, True, True, True, 0); passed += 1
    for evidence in ((True, False, True, True, 0), (True, True, False, True, 0),
                     (True, True, True, False, 0), (True, True, True, True, 1),
                     (True, True, True, True, False)):
        try:
            subject.blocked_guard_evidence(*evidence)
        except RuntimeError:
            passed += 1
        else:
            raise AssertionError('Timeout or absent forbidden readiness cannot prove stdout refusal')
    subject.negative_gate_evidence(True, True, True, 0); passed += 1
    subject.negative_gate_evidence(True, True, True); passed += 1
    for evidence in ((False, True, True, 0), (True, False, True, 0),
                     (True, True, False, 0), (True, True, True, 1),
                     (False, True, True), (True, False, True)):
        try:
            subject.negative_gate_evidence(*evidence)
        except RuntimeError:
            passed += 1
        else:
            raise AssertionError('Unrelated timeout cannot prove a nonzero or substitution predicate')
    @dataclass
    class ModeledObservation:
        settled: bool
        exit_code: object
        reaped: bool = False
        disposed: bool = False
    actor = object.__new__(subject.OwnedActor)
    actor.role, actor.mode, actor.stdin_closed = 'producer', 'normal', True
    actor.observer = SimpleNamespace(ExitObservation=ModeledObservation)
    with patch.object(subject.OwnedActor, 'settled', return_value=ModeledObservation(True, 0)), \
         patch.object(subject.time, 'monotonic', return_value=0), patch.object(subject.time, 'sleep') as slept:
        actor.settle_closed_normal_input()
        assert slept.call_count == 0
        passed += 1
    with patch.object(subject.OwnedActor, 'settled', side_effect=[ModeledObservation(False, None), ModeledObservation(True, 9)]), \
         patch.object(subject.time, 'monotonic', return_value=0), patch.object(subject.time, 'sleep') as slept:
        actor.settle_closed_normal_input()
        assert slept.call_count == 1
        passed += 1
    with patch.object(subject.OwnedActor, 'settled', return_value=ModeledObservation(False, None)), \
         patch.object(subject.time, 'monotonic', side_effect=[0, 2]):
        try:
            actor.settle_closed_normal_input()
        except RuntimeError:
            passed += 1
        else:
            raise AssertionError('Unsettled normal producer must refuse the finite deadline')
    with patch.object(subject.OwnedActor, 'settled', return_value=ModeledObservation(True, 0)), \
         patch.object(subject.time, 'monotonic', side_effect=[0, 0, 2]):
        try:
            actor.settle_closed_normal_input()
        except RuntimeError:
            passed += 1
        else:
            raise AssertionError('Late settled observation must not satisfy the1s deadline')
    cleanup_calls = []
    stream = SimpleNamespace(closed=False)
    stream.close = lambda: setattr(stream, 'closed', True)
    actor.lease = SimpleNamespace(process=SimpleNamespace(stdin=stream), conditions_satisfied=lambda: True, cleanup_failed=False)
    actor.supervisor = SimpleNamespace(_cleanup=lambda lease: cleanup_calls.append(lease) or {})
    with patch.object(subject.OwnedActor, 'settle_closed_normal_input', side_effect=RuntimeError('modeled deadline')):
        assert not actor.dispose() and cleanup_calls == [actor.lease]
        passed += 1
    return {'PureMockedControlsPassed': passed, 'ActualHostedCasesExecuted': 0,
            'ActualChildrenStarted': False, 'ActualDescriptorsObserved': False,
            'CollectorPreackAccepted': False, 'FinancialEightAccepted': False}


if __name__ == '__main__':
    print(json.dumps(controls(), sort_keys=True))
