"""Actual Ubuntu control source. Import alone launches no actor/kernel observer."""
import hashlib
import json
import os
from pathlib import Path
import selectors
import stat
import subprocess
import sys
import tempfile
import threading
import time
import types


CASES = ('unsettled', 'nonzero', 'live-preack', 'substitution', 'blocked-ack', 'preack-nonzero')
OWNERS = []
FLAG_NAMES = ('UnsettledObserved', 'NonreapingZombieObserved', 'NonzeroObserved',
              'CollectorEofBeforeAck', 'ProducerStderrLiveAndServiced', 'GuardedAckObserved',
              'SubstitutionRefused', 'BlockedWriteObserved', 'UnsolicitedOutputRefused', 'NonzeroPreackRefused')
CLEANUP_NAMES = ('ActualOriginalChildrenReaped', 'OriginalGenerationsAbsent', 'RecordedPipesAbsent',
                 'HeldInputsClosed', 'HeldOutputEofAndClosed', 'HeldPidfdsClosed')
FALSE_NAMES = ('KernelResourceCapsObserved', 'CoordinatorReceiptAdmissionAccepted', 'FinancialBusinessGraphAccepted')


def blocked_guard_evidence(blocked, forbidden_ready, deadline_live, bindings_live, write_attempts):
    require(all(type(value) is bool and value for value in (blocked, forbidden_ready, deadline_live, bindings_live)))
    require(type(write_attempts) is int and write_attempts == 0)


def negative_gate_evidence(predicate_observed, deadline_live, bindings_live, admissions=None):
    require(all(type(value) is bool and value for value in (predicate_observed, deadline_live, bindings_live)))
    if admissions is not None:
        require(type(admissions) is int and admissions == 0)


def validate_public_case(value):
    require(type(value) is dict and set(value) == {'Case', 'Passed', *FLAG_NAMES, *CLEANUP_NAMES, *FALSE_NAMES})
    require(type(value['Case']) is str and value['Case'] in CASES)
    require(all(type(value[key]) is bool for key in value if key != 'Case') and value['Passed'])
    expected = {
        'unsettled': {'UnsettledObserved', 'NonreapingZombieObserved'},
        'nonzero': {'UnsettledObserved', 'NonreapingZombieObserved', 'NonzeroObserved'},
        'live-preack': {'CollectorEofBeforeAck', 'ProducerStderrLiveAndServiced', 'GuardedAckObserved'},
        'substitution': {'SubstitutionRefused'},
        'blocked-ack': {'CollectorEofBeforeAck', 'ProducerStderrLiveAndServiced', 'BlockedWriteObserved', 'UnsolicitedOutputRefused'},
        'preack-nonzero': {'NonzeroPreackRefused'},
    }[value['Case']]
    require({key for key in FLAG_NAMES if value[key]} == expected)
    require(all(value[key] for key in CLEANUP_NAMES) and not any(value[key] for key in FALSE_NAMES))


def unique_pairs(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def require(condition):
    if not condition:
        raise RuntimeError('Held phase hosted control refused')


def held_bytes(path, size, digest):
    require(type(size) is int and 0 < size <= 1048576)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    try:
        info = os.fstat(fd)
        require(stat.S_ISREG(info.st_mode) and info.st_size == size)
        with os.fdopen(fd, 'rb', closefd=False) as stream:
            raw = stream.read(size + 1)
        require(len(raw) == size and hashlib.sha256(raw).hexdigest() == digest)
        after = os.fstat(fd)
        require((after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_mode) ==
                (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_mode))
        return raw
    finally:
        os.close(fd)  # Only this transient source-reader descriptor is owned here.


def load_sources(repository, manifest, snapshot):
    entries = manifest['Dependencies']
    names = ('owned_command', 'held_parent_io', 'held_linux_exit', 'coordinator', 'collector_preack')
    require(tuple(entry['module'] for entry in entries) == names)
    modules = {}
    for entry in entries:
        require(set(entry) == {'module', 'path', 'bytes', 'sha256'})
        relative = Path(entry['path'])
        require(not relative.is_absolute() and '..' not in relative.parts)
        path = repository / relative
        require(path.resolve().is_relative_to(repository.resolve()))
        raw = held_bytes(path, entry['bytes'], entry['sha256'])
        copy = snapshot / (entry['module'] + '.py')
        copy.write_bytes(raw)
        module = types.ModuleType(entry['module'])
        module.__file__ = str(copy)
        sys.modules[entry['module']] = module
        exec(compile(raw, str(copy), 'exec'), module.__dict__)
        modules[entry['module']] = module
    return modules


def until(predicate, seconds=3):
    deadline = time.monotonic() + seconds
    while not predicate():
        require(time.monotonic() < deadline)
        time.sleep(.01)


class OwnedActor:
    """Test acquisition fence: original lease registered before attempted birth."""
    def __init__(self, helper, observer, fixture, role, mode, root):
        require(len(OWNERS) < 12)
        self.helper, self.observer, self.root, self.role = helper, observer, root, role
        self.mode = mode
        self.supervisor = helper.CommandSupervisor()
        self.lease = helper.Lease()
        self.lease.actual_backend = True
        self.stdin_closed = False
        self.initial_identity = None
        self.journal = None
        self.cleanup = None
        OWNERS.append(self)
        self.supervisor.driver.admit()
        self.lease.attempted = True
        self.lease.process = subprocess.Popen([sys.executable, str(fixture), role, mode, str(root)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            start_new_session=True, close_fds=True,
            env={'PATH': os.environ.get('PATH', ''), 'LANG': 'C.UTF-8', 'PYTHONNOUSERSITE': '1'})
        self.lease.associated = True
        anchor = self.supervisor._anchor(self.lease)
        self.initial_identity = observer.OriginalIdentity(self.process.pid, anchor[0], anchor[1], anchor[2], anchor[3])
        self.lease.pidfd = self.supervisor.driver.pidfd(self.process)
        self.supervisor._anchor(self.lease)
        self.lease.bound = True
        self.lease.selector = self.supervisor.driver.selector()
        for name in ('stdout', 'stderr'):
            self.lease.streams[name] = getattr(self.process, name)
            self.supervisor.driver.register(self.lease.selector, self.lease.streams[name], name)
        # Actual original-generation/pidfd observation precedes any exit release.
        require(not observer.observe_original(self.process, self.lease.pidfd, self.initial_identity).settled)
        until(lambda: (root / (role + '-birth.json')).exists())
        with (root / (role + '-birth.json')).open('rb') as stream:
            raw = stream.read(2049)
        require(0 < len(raw) <= 2048)
        self.journal = json.loads(raw)
        require(set(self.journal) == {'pid', 'ticks', 'ppid', 'pgrp', 'sid', 'pipes'})
        require(type(self.journal['pipes']) is list and len(self.journal['pipes']) == 3
                and all(type(pair) is list and len(pair) == 2 and all(type(v) is int and v >= 0 for v in pair)
                        for pair in self.journal['pipes']))
        require((self.journal['pid'], self.journal['ticks'], self.journal['ppid'], self.journal['pgrp'], self.journal['sid']) ==
                (self.process.pid, anchor[0], anchor[1], anchor[2], anchor[3]))
        for stream, pair in zip((self.process.stdin, self.process.stdout, self.process.stderr), self.journal['pipes']):
            require(list((os.fstat(stream.fileno()).st_dev, os.fstat(stream.fileno()).st_ino)) == pair)

    @property
    def process(self):
        return self.lease.process

    def settled(self):
        return self.observer.observe_original(self.process, self.lease.pidfd, self.initial_identity)

    def settle_closed_normal_input(self):
        if self.role != 'producer' or self.mode != 'normal':
            return
        require(self.stdin_closed)
        deadline = time.monotonic() + 1
        while True:
            require(time.monotonic() < deadline)
            observed = self.settled()
            require(time.monotonic() < deadline)
            require(type(observed) is self.observer.ExitObservation and type(observed.settled) is bool
                    and observed.reaped is False and observed.disposed is False)
            if observed.settled:
                require(type(observed.exit_code) is int)
                return  # Already-acknowledged exit0 is legal; do not guess exit9.
            require(observed.exit_code is None and time.monotonic() < deadline)
            time.sleep(.01)

    def dispose(self):
        # Independent input-close and source-fenced signal/drain/reap/close
        # attempts. Unknown birth/failed close remains retained in OWNERS.
        errors = []
        try:
            require(self.process is not None)
            self.process.stdin.close()
            self.stdin_closed = self.process.stdin.closed
        except BaseException:
            errors.append('stdin')
        try:
            self.settle_closed_normal_input()
        except BaseException:
            errors.append('normal-producer-settle')
        try:
            self.cleanup = self.supervisor._cleanup(self.lease)
        except BaseException:
            errors.append('source-cleanup')
        return not errors and self.stdin_closed and self.cleanup is not None and self.lease.conditions_satisfied() and not self.lease.cleanup_failed


def make_channels(transport, collector, producer):
    cancelled = threading.Event()
    expires = time.monotonic() + 10
    ce = transport.OpaqueDrain(collector.process.stderr, cancelled, expires)
    pe = transport.OpaqueDrain(producer.process.stderr, cancelled, expires)
    drains = (ce, pe)
    co = transport.HeldPipe(collector.process.stdout, cancelled, expires, drains)
    po = transport.HeldPipe(producer.process.stdout, cancelled, expires, drains)
    pi = transport.HeldPipe(producer.process.stdin, cancelled, expires, drains)
    return co, ce, po, pe, pi


def release_collector(root):
    (root / 'collector-release').write_bytes(b'1')


def reject(operation, expected):
    try:
        operation()
    except expected:
        return
    require(False)


def actual_pipe_absence(actors):
    expected = {tuple(pair) for actor in actors for pair in actor.journal['pipes']}
    with os.scandir('/proc/self/fd') as entries:
        count = 0
        for entry in entries:
            count += 1
            require(count <= 32768)
            try:
                info = os.stat(entry.path)
            except FileNotFoundError:
                continue
            require((info.st_dev, info.st_ino) not in expected)


def run_case(modules, fixture, case, root):
    helper, observer, transport, preack = (modules[name] for name in ('owned_command', 'held_linux_exit', 'held_parent_io', 'collector_preack'))
    actors = []
    flags = {'UnsettledObserved': False, 'NonreapingZombieObserved': False,
             'NonzeroObserved': False, 'CollectorEofBeforeAck': False,
             'ProducerStderrLiveAndServiced': False, 'GuardedAckObserved': False,
             'SubstitutionRefused': False, 'BlockedWriteObserved': False,
             'UnsolicitedOutputRefused': False, 'NonzeroPreackRefused': False}
    failure = False
    try:
        collector = OwnedActor(helper, observer, fixture, 'collector', case, root)
        actors.append(collector)
        if case in ('unsettled', 'nonzero'):
            require(not collector.settled().settled)
            flags['UnsettledObserved'] = True
            release_collector(root)
            until(lambda: collector.settled().settled)
            observed = collector.settled()
            require(observed.exit_code == (7 if case == 'nonzero' else 0) and collector.process.returncode is None)
            flags['NonreapingZombieObserved'] = True
            flags['NonzeroObserved'] = case == 'nonzero'
        else:
            producer = OwnedActor(helper, observer, fixture, 'producer', 'blocked' if case == 'blocked-ack' else 'normal', root)
            actors.append(producer)
            until(lambda: (root / 'producer-ready.json').exists())
            co, ce, po, pe, pi = make_channels(transport, collector, producer)
            preack.verify_dependencies()
            preack.validate_channels(collector.process, producer.process, co, ce, po, pe)
            if case == 'substitution':
                substituted = transport.HeldPipe(producer.process.stdout, co.cancelled, co.expires, (ce, pe))
                original_validation = preack.validate_channels
                original_observer = observer.observe_original
                original_factory = preack.selectors.DefaultSelector
                original_read = preack.os.read
                original_wait = preack.os.waitid
                validation_refused = False
                admissions = 0
                gate_deadline = time.monotonic() + 3

                def observed_validation(*args):
                    nonlocal validation_refused
                    try:
                        return original_validation(*args)
                    except preack.PreackRefused:
                        require(args[2] is substituted and substituted.stream is not collector.process.stdout)
                        validation_refused = True
                        raise

                def count_admission(operation):
                    def delegated(*args, **kwargs):
                        nonlocal admissions
                        admissions += 1
                        return operation(*args, **kwargs)
                    return delegated
                preack.validate_channels = observed_validation
                observer.observe_original = count_admission(original_observer)
                preack.selectors.DefaultSelector = count_admission(original_factory)
                preack.os.read = count_admission(original_read)
                preack.os.waitid = count_admission(original_wait)
                try:
                    reject(lambda: preack.drain_collector_preack(collector.process, producer.process, collector.lease.pidfd,
                        collector.initial_identity, substituted, ce, po, pe, gate_deadline), preack.PreackRefused)
                finally:
                    preack.validate_channels = original_validation
                    observer.observe_original = original_observer
                    preack.selectors.DefaultSelector = original_factory
                    preack.os.read = original_read
                    preack.os.waitid = original_wait
                negative_gate_evidence(validation_refused, time.monotonic() < gate_deadline and not co.cancelled.is_set(),
                                       substituted.stream is producer.process.stdout and substituted.stream is not collector.process.stdout,
                                       admissions)
                flags['SubstitutionRefused'] = True
            else:
                release_collector(root)
                operation = lambda: preack.drain_collector_preack(collector.process, producer.process, collector.lease.pidfd,
                    collector.initial_identity, co, ce, po, pe, time.monotonic() + 3)
                if case == 'preack-nonzero':
                    real_observer = observer.observe_original
                    observed_nonzero = False
                    gate_deadline = time.monotonic() + 3
                    retained = tuple((c, c.fd, c.stream) for c in (co, ce, po, pe))
                    links = preack.pipe_links((co, ce, po, pe))

                    def observed_nonzero_exit(*args):
                        nonlocal observed_nonzero
                        observed = real_observer(*args)
                        if observed.settled and observed.exit_code == 7:
                            require(args[0] is collector.process and args[1] is collector.lease.pidfd and args[2] is collector.initial_identity)
                            require(time.monotonic() < gate_deadline and not co.cancelled.is_set())
                            observed_nonzero = True
                        return observed
                    observer.observe_original = observed_nonzero_exit
                    try:
                        reject(lambda: preack.drain_collector_preack(collector.process, producer.process, collector.lease.pidfd,
                            collector.initial_identity, co, ce, po, pe, gate_deadline), preack.PreackRefused)
                    finally:
                        observer.observe_original = real_observer
                    bindings_live = all(c.fd == fd and c.stream is stream and not stream.closed and stream.fileno() == fd
                                        for c, fd, stream in retained) and preack.pipe_links((co, ce, po, pe)) == links
                    negative_gate_evidence(observed_nonzero, time.monotonic() < gate_deadline and not co.cancelled.is_set(), bindings_live)
                    flags['NonzeroPreackRefused'] = True
                else:
                    operation()
                    require(co.buffer.eof and ce.eof and not pe.eof and pe.count > 0 and not producer.settled().settled)
                    flags['CollectorEofBeforeAck'] = True
                    flags['ProducerStderrLiveAndServiced'] = True
                    if case == 'live-preack':
                        preack._write_stop_ack_owned(b'stopped\n', pi, po, (co, ce, po, pe, pi), time.monotonic() + 3)
                        until(lambda: (root / 'ack.json').exists())
                        require(json.loads((root / 'ack.json').read_bytes()) == {'acknowledged': True})
                        until(lambda: producer.settled().settled)
                        require(producer.settled().exit_code == 0 and producer.process.returncode is None)
                        flags['GuardedAckObserved'] = True
                    else:
                        # Fill actual retained stdin until real nonblocking EAGAIN.
                        total = 0
                        while True:
                            try:
                                count = os.write(pi.fd, b'x' * 4096)
                                require(count > 0 and total + count <= 1048576)
                                total += count
                            except BlockingIOError:
                                break
                        require(total > 0)
                        real_factory = preack.selectors.DefaultSelector
                        real_write = preack.os.write
                        forbidden_ready = False
                        write_attempts = 0
                        ack_deadline = min(time.monotonic() + 3, pi.expires)
                        retained = tuple((c, c.fd, c.stream) for c in (co, ce, po, pe, pi))
                        links = preack.pipe_links((co, ce, po, pe, pi))

                        def observed_write(fd, raw):
                            nonlocal write_attempts
                            if fd == pi.fd:
                                write_attempts += 1
                            return real_write(fd, raw)

                        class ObservedSelector:
                            def __init__(self):
                                self.actual = real_factory()
                            def __enter__(self): return self
                            def __exit__(self, *args): self.actual.close()
                            def register(self, *args): return self.actual.register(*args)
                            def unregister(self, *args): return self.actual.unregister(*args)
                            def select(self, timeout=None):
                                nonlocal forbidden_ready
                                ready = self.actual.select(timeout)
                                if any(key.fd == po.fd for key, _ in ready):
                                    forbidden_ready = True
                                if not flags['BlockedWriteObserved'] and any(key.fd == pi.fd for key in self.actual.get_map().values()):
                                    require(not any(key.fd == pi.fd for key, _ in ready))
                                    flags['BlockedWriteObserved'] = True
                                    (root / 'emit-unsolicited').write_bytes(b'1')
                                return ready  # Never invent or filter kernel readiness.
                        preack.selectors.DefaultSelector = ObservedSelector
                        preack.os.write = observed_write
                        try:
                            reject(lambda: preack._write_stop_ack_owned(b'stopped\n', pi, po, (co, ce, po, pe, pi), ack_deadline), preack.PreackRefused)
                        finally:
                            preack.selectors.DefaultSelector = real_factory
                            preack.os.write = real_write
                        until(lambda: (root / 'unsolicited.json').exists())
                        bindings_live = all(c.fd == fd and c.stream is stream and not stream.closed
                                            and stream.fileno() == fd and not c.failed
                                            and c.cancelled is pi.cancelled and c.expires == pi.expires for c, fd, stream in retained)
                        bindings_live = bindings_live and preack.pipe_links((co, ce, po, pe, pi)) == links
                        bindings_live = bindings_live and not po.buffer.pending and not po.buffer.eof and not producer.settled().settled
                        blocked_guard_evidence(flags['BlockedWriteObserved'], forbidden_ready,
                                               not pi.cancelled.is_set() and time.monotonic() < ack_deadline,
                                               bindings_live, write_attempts)
                        require(not (root / 'ack.json').exists())
                        flags['UnsolicitedOutputRefused'] = True
    except BaseException:
        failure = True
    finally:
        # Include partial acquisitions already retained globally, not just the
        # successfully returned local actors. Attempt both independently.
        actors = [actor for actor in OWNERS if actor.root == root]
        for actor in actors:
            try:
                if not actor.dispose(): failure = True
            except BaseException:
                failure = True
    require(not failure and actors)
    for actor in actors:
        try:
            current = actor.supervisor.driver.generation(actor.process.pid)
        except FileNotFoundError:
            continue
        require(current[0] != actor.initial_identity.start_ticks)
    actual_pipe_absence(actors)
    return {'Case': case, 'Passed': True, **flags, 'ActualOriginalChildrenReaped': True,
            'OriginalGenerationsAbsent': True, 'RecordedPipesAbsent': True,
            'HeldInputsClosed': True, 'HeldOutputEofAndClosed': True,
            'HeldPidfdsClosed': True, 'KernelResourceCapsObserved': False,
            'CoordinatorReceiptAdmissionAccepted': False, 'FinancialBusinessGraphAccepted': False}


def main():
    repository, output = (Path(argument).resolve() for argument in sys.argv[1:])
    require(not output.exists())
    here = Path(__file__).resolve().parent
    with (here / 'manifest.json').open('rb') as stream:
        manifest_raw = stream.read(32769)
    require(0 < len(manifest_raw) <= 32768)
    manifest = json.loads(manifest_raw, object_pairs_hook=unique_pairs)
    require(set(manifest) == {'Dependencies', 'Fixture'})
    private = Path(tempfile.mkdtemp(prefix='held-phase-controls-'))
    snapshot = private / 'source'
    snapshot.mkdir()
    fixture_raw = held_bytes(here / 'fixture.py', manifest['Fixture']['bytes'], manifest['Fixture']['sha256'])
    fixture = snapshot / 'fixture.py'
    fixture.write_bytes(fixture_raw)
    modules = load_sources(repository, manifest, snapshot)
    metadata = {'ManifestSha256': hashlib.sha256(manifest_raw).hexdigest(),
                'SourceSha256': {entry['module']: entry['sha256'] for entry in manifest['Dependencies']},
                'FixtureSha256': manifest['Fixture']['sha256'],
                'ObservedPythonVersion': list(sys.version_info[:3])}
    receipts = []
    for case in CASES:
        root = private / case
        root.mkdir()
        try:
            receipt = run_case(modules, fixture, case, root)
            validate_public_case(receipt)
        except BaseException:
            receipts.append({'Case': case, 'Passed': False})
            output.write_text(json.dumps({'Schema': 1, 'Completed': False, 'Metadata': metadata, 'Cases': receipts}), encoding='ascii')
            return 1
        receipts.append(receipt)
        output.write_text(json.dumps({'Schema': 1, 'Completed': len(receipts) == len(CASES), 'Metadata': metadata, 'Cases': receipts}), encoding='ascii')
    return 0


if __name__ == '__main__':
    try:
        code = main()
    except BaseException:
        code = 1
    print(json.dumps({'HeldPhaseHostedControlsCompleted': code == 0}))
    raise SystemExit(code)
