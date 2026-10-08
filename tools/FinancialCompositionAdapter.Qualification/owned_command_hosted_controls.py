"""Actual Linux controls. No subprocess is launched at import or by source checks."""
import hashlib
import importlib.util
import json
import os
import pathlib
import sys
import tempfile
import time


EXPECTED_HELPER = '427f68ef6a78c9b6428bda39b93be782188ed398fd748ad69dc562f02868f993'
EXPECTED_FIXTURE = '64531503592f63020e6d33ad49b66caa83f80b0591f427ce7d0d8db5792c438e'
CASES = ('natural', 'overflow', 'timeout', 'selector', 'inherited', 'recovery')
OWNERS = []  # At most one entry per fixed case, retained before birth until exit.


def require(condition):
    if not condition:
        raise RuntimeError('Hosted command control refused')


def load_helper():
    with pathlib.Path(__file__).with_name('owned_command_fixture.py').open('rb') as stream:
        fixture = stream.read(1677)
    require(len(fixture) == 1676 and hashlib.sha256(fixture).hexdigest() == EXPECTED_FIXTURE)
    file = pathlib.Path(__file__).with_name('owned_command.py')
    with file.open('rb') as stream:
        raw = stream.read(16675)
    require(len(raw) == 16674 and hashlib.sha256(raw).hexdigest() == EXPECTED_HELPER)
    spec = importlib.util.spec_from_file_location('qualified_owned_command', file)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def read_births(root):
    values = []
    for role in ('leader', 'writer'):
        file = root / (role + '.json')
        if not file.exists():
            continue
        with file.open('rb') as stream:
            raw = stream.read(2049)
        require(0 < len(raw) <= 2048)
        value = json.loads(raw)
        require(set(value) == {'pid', 'ppid', 'ticks', 'session', 'pipes'})
        require(all(type(value[key]) is int and value[key] > 0 for key in ('pid', 'ppid', 'ticks', 'session')))
        require(type(value['pipes']) is list and len(value['pipes']) == 2)
        require(all(type(pair) is list and len(pair) == 2 and all(type(v) is int and v >= 0 for v in pair) for pair in value['pipes']))
        values.append((role, value))
    return values


def original_absent(driver, value):
    try:
        current = driver.generation(value['pid'])
    except FileNotFoundError:
        return True
    return current[0] != value['ticks']


def pipes_absent(values):
    expected = {tuple(pair) for _, value in values for pair in value['pipes']}
    require(bool(expected))
    with os.scandir('/proc/self/fd') as entries:
        count = 0
        for entry in entries:
            count += 1
            require(count <= 32768)
            try:
                stat = os.stat(entry.path)
            except FileNotFoundError:
                continue
            if (stat.st_dev, stat.st_ino) in expected:
                return False
    return True


def run_case(subject, case, root):
    driver = subject.LinuxDriver()
    supervisor = subject.CommandSupervisor(driver, stream_limit=4096)
    require(len(OWNERS) < len(CASES))
    OWNERS.append((supervisor, root))
    injected = False
    initial_refusal = False
    initial_quarantine = False
    recovery_verified = False
    observed_exit = None
    observed_owner = None
    stdout_bytes_observed = 0
    overflow_boundary_observed = False
    retained_deadline_refusal_observed = False
    result = None
    expected_failure = case != 'natural'
    real_wait = driver.wait
    real_read = driver.read
    real_drain = supervisor._drain

    def observe_drain(lease, end, retain):
        nonlocal overflow_boundary_observed, retained_deadline_refusal_observed
        before = stdout_bytes_observed
        try:
            return real_drain(lease, end, retain)
        except subject.CommandRefused:
            if retain and driver.clock() >= end and driver.has_readers(lease.selector) and not all(lease.eof.values()):
                retained_deadline_refusal_observed = True
            raise
        finally:
            if retain and case == 'overflow':
                overflow_boundary_observed = stdout_bytes_observed - before > 4096 and len(lease.output['stdout']) <= 4096
    supervisor._drain = observe_drain

    def observe_read(key, maximum):
        nonlocal stdout_bytes_observed
        piece = real_read(key, maximum)
        if key.data == 'stdout':
            stdout_bytes_observed += len(piece)
        return piece
    driver.read = observe_read

    def observe_wait(process, remaining, anchor):
        nonlocal observed_exit, observed_owner
        real_wait(process, remaining, anchor)
        observed_exit = process.returncode
        observed_owner = (process.pid, anchor[:4])
    driver.wait = observe_wait
    # Exactly the production Linux driver still owns all real resources. Only
    # one named causal operation is replaced for these two fault controls.
    if case == 'selector':
        real = driver.selector

        def selector_fault():
            nonlocal injected
            if not injected:
                injected = True
                end = time.monotonic() + 2
                while not (root / 'leader.json').exists():
                    require(time.monotonic() < end)
                    time.sleep(.01)
                raise OSError('Synthetic selector fault')
            return real()
        driver.selector = selector_fault
    if case == 'recovery':
        real = driver.kill_group

        def signal_fault(process):
            nonlocal injected
            if not injected:
                injected = True
                raise OSError('Synthetic signal fault')
            return real(process)
        driver.kill_group = signal_fault
    try:
        try:
            result = supervisor.capture([sys.executable, str(pathlib.Path(__file__).with_name('owned_command_fixture.py')),
                                         case, str(root)], timeout=2 if case in ('natural', 'selector', 'inherited', 'overflow') else .5)
        except subject.CommandRefused:
            initial_refusal = True
        require(initial_refusal == expected_failure)
        initial_quarantine = supervisor.quarantine is not None
        if case == 'recovery':
            require(injected and initial_quarantine and supervisor.admission_refused)
            held = supervisor.quarantine
            require(held.process is not None and held.pidfd is not None and held.bound)
            require(not held.reaped and not held.closed['pidfd'] and not all(held.eof.values()))
            receipt = supervisor.retry_quarantined_cleanup()
            recovery_verified = receipt['SameSessionCleanupVerified'] and receipt['CleanupOriginallyFailed']
            require(recovery_verified and supervisor.quarantine is held and supervisor.admission_refused)
            # A new command must be refused before another actual birth.
            commands = supervisor.commands
            try:
                supervisor.capture(['never-start-this-synthetic-command'])
            except subject.CommandRefused:
                pass
            else:
                require(False)
            require(supervisor.commands == commands)
        else:
            require(not initial_quarantine)
        if case == 'natural':
            require(result.returncode == 0 and result.stdout == b'owned-synthetic-output')
        if case == 'selector':
            require(injected)
        receipt = supervisor.last_cleanup
        require(receipt['ActualLinuxBackendUsed'] and receipt['SameSessionCleanupVerified'])
        require(receipt['CleanupOriginallyFailed'] == (case == 'recovery'))
        births = read_births(root)
        require([role for role, _ in births] == (['leader', 'writer'] if case == 'inherited' else ['leader']))
        leader = births[0][1]
        require(observed_owner is not None and leader['pid'] == observed_owner[0])
        require((leader['ticks'], leader['ppid'], leader['pid'], leader['session']) == observed_owner[1])
        require(leader['ppid'] == os.getpid() and leader['session'] == leader['pid'])
        if case == 'inherited':
            require(births[1][1]['ppid'] == leader['pid'] and births[1][1]['session'] == leader['pid'])
            require(observed_exit == 0)
        if case == 'overflow':
            require((root / 'write-intent').read_bytes() == b'1')
            require(overflow_boundary_observed)
        if case in ('timeout', 'inherited', 'recovery'):
            require(retained_deadline_refusal_observed)
        require(all(original_absent(driver, value) for _, value in births))
        require(pipes_absent(births))
        return {'Case': case, 'Passed': True, 'ActualLinuxBackendUsed': True,
                'OriginalRecordedGenerationsAbsent': True, 'RecordedPipeDescriptorsAbsent': True,
                'SameSessionCleanupVerified': True, 'ExpectedRefusalObserved': expected_failure,
                'ControlledFaultInjected': injected, 'InitialQuarantineObserved': initial_quarantine,
                'RecoveryVerified': recovery_verified, 'KernelResourceCapsObserved': False,
                'NaturalLeaderExitObserved': case in ('natural', 'inherited') and observed_exit == 0,
                'RetainedPhaseDeadlineRefusalObserved': retained_deadline_refusal_observed,
                'RetainedByteBoundaryObserved': overflow_boundary_observed,
                'EscapedSessionDescendantsAccepted': False}
    finally:
        # Preserve sticky ownership even when an independent control assertion
        # fails. An unproved recovery is never converted into a passing record.
        if supervisor.quarantine is not None and not supervisor.quarantine.recovery_attempted:
            try:
                supervisor.retry_quarantined_cleanup()
            except BaseException:
                pass


def main():
    output = pathlib.Path(sys.argv[1])
    require(not output.exists())
    subject = load_helper()
    receipts = []
    for case in CASES:
        # Keep failed private journals reachable for quarantine; never upload
        # temporary journal directories or underlying exceptions.
        root = pathlib.Path(tempfile.mkdtemp(prefix='owned-command-control-'))
        try:
            receipt = run_case(subject, case, root)
        except BaseException:
            receipts.append({'Case': case, 'Passed': False})
            output.write_text(json.dumps({'Schema': 1, 'Completed': False, 'Cases': receipts}), encoding='ascii')
            return 1
        for entry in root.iterdir():
            entry.unlink()
        root.rmdir()
        receipts.append(receipt)
        output.write_text(json.dumps({'Schema': 1, 'Completed': len(receipts) == len(CASES), 'Cases': receipts}), encoding='ascii')
    return 0


if __name__ == '__main__':
    try:
        code = main()
    except BaseException:
        # No interpreter traceback, PID, argv, paths, stdout or stderr in logs.
        code = 1
    print(json.dumps({'OwnedCommandHostedControlsCompleted': code == 0}))
    raise SystemExit(code)
