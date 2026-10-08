"""Pure injected-driver algorithm controls. No SDK, process, fd or session is created."""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import sys
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('owned_command_controls_subject', Path(__file__).with_name('owned_command.py'))
subject = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = subject
spec.loader.exec_module(subject)


class Handle:
    def __init__(self, name): self.name, self.closed = name, False


class Selector(Handle):
    def __init__(self): super().__init__('selector'); self.keys = {}
    def get_map(self): return self.keys


class Driver:
    # Every birth, descriptor and kernel observation here is explicitly modeled.
    def __init__(self, fault=None):
        self.fault = fault
        self.now, self.ticks, self.starts = 0., 0, 0
        self.selector_calls = self.signal_calls = 0
        self.close_attempts = []
        self.alive = True
        self.once = set()
        self.process = SimpleNamespace(pid=101, returncode=None, stdout=Handle('stdout'), stderr=Handle('stderr'))
        self.payload = {'stdout': bytearray(b'private-opaque-value'), 'stderr': bytearray()}
    def clock(self):
        self.ticks += 1; assert self.ticks < 10000
        self.now += .01; return self.now
    def parent(self): return 99
    def admit(self): pass
    def start(self, arguments, cwd, env):
        self.starts += 1
        self.arguments, self.cwd, self.env = arguments, cwd, env
        if self.fault == 'opaque-constructor': raise RuntimeError('private-opaque-value')
        return self.process
    def generation(self, pid):
        assert pid == 101
        return (17 if self.fault != 'generation-mismatch' or self.selector_calls == 0 else 18,
                99, 101, 101, 'S' if self.alive else 'Z')
    def members(self, session, deadline):
        if self.process.returncode is not None: return {}
        return {101: self.generation(101)}
    def pidfd(self, process):
        if self.fault == 'pidfd-failure': raise OSError('private-opaque-value')
        return Handle('pidfd')
    def selector(self):
        self.selector_calls += 1
        if self.fault == 'selector-failure' and self.selector_calls == 1: raise RuntimeError('private-opaque-value')
        return Selector()
    def register(self, selector, stream, name):
        if self.fault == 'register-failure' and name == 'stderr' and 'register' not in self.once:
            self.once.add('register'); raise RuntimeError('private-opaque-value')
        selector.keys[name] = SimpleNamespace(data=name, fileobj=stream)
    def events(self, selector, delay):
        self.now += delay
        if (self.fault in ('timeout', 'signal-failure') and self.alive): return []
        return list(selector.keys.values())
    def read(self, key, maximum):
        if not self.alive: return b''
        if self.fault == 'overflow' and key.data == 'stdout': return b'x' * maximum
        pending = self.payload[key.data]
        piece = bytes(pending[:maximum]); del pending[:maximum]
        return piece
    def remove(self, selector, key): del selector.keys[key.data]
    def has_readers(self, selector): return bool(selector.keys)
    def exit_observation(self, process): return 0
    def pause(self): self.now += .1
    def kill_group(self, process):
        self.signal_calls += 1
        if self.fault == 'signal-failure' and self.signal_calls == 1: raise OSError('private-opaque-value')
        self.alive = False
    def wait(self, process, remaining, anchor):
        assert anchor is not None
        if self.alive: raise TimeoutError('private-opaque-value')
        process.returncode = 0
    def reap(self, pid, identity): raise AssertionError('No modeled descendant in these pure cases')
    def close(self, handle):
        self.close_attempts.append(handle.name)
        if self.fault == 'close-failure' and handle.name == 'stdout' and 'close' not in self.once:
            self.once.add('close'); raise OSError('private-opaque-value')
        handle.closed = True
        if isinstance(handle, Selector): handle.keys.clear()


def refusal(supervisor, driver, *, timeout=1):
    try: supervisor.capture(['synthetic-command'], timeout=timeout)
    except subject.CommandRefused as error:
        assert 'private' not in str(error)
        return error
    raise AssertionError('Expected algorithm refusal')


def controls():
    passed = 0
    driver = Driver(); supervisor = subject.CommandSupervisor(driver)
    result = supervisor.capture(['synthetic-command'])
    assert result.stdout == b'private-opaque-value' and result.stderr == b'' and result.returncode == 0
    assert all(result.cleanup[key] for key in ('StdoutEof', 'StderrEof', 'OriginalLeaderReaped', 'OwnedSessionAbsent', 'StdoutClosed', 'StderrClosed', 'SelectorClosed', 'PidfdClosed'))
    assert result.cleanup['EscapedSessionDescendantsAccepted'] is False and not supervisor.admission_refused
    assert result.cleanup['ActualLinuxBackendUsed'] is False and result.cleanup['SameSessionCleanupVerified'] is False
    passed += 1
    driver = Driver(); supervisor = subject.CommandSupervisor(driver)
    supplied_environment = {'SYNTHETIC_PRIVATE_VALUE': 'private-opaque-value'}
    supervisor.capture(['synthetic-command', 'argument'], cwd='private-owned-root', env=supplied_environment)
    assert driver.arguments == ['synthetic-command', 'argument'] and driver.cwd == 'private-owned-root' and driver.env is supplied_environment
    passed += 1
    driver = Driver('overflow'); supervisor = subject.CommandSupervisor(driver, stream_limit=12)
    created = []; original_lease = subject.Lease
    def held_lease():
        lease = original_lease(); created.append(lease); return lease
    with patch.object(subject, 'Lease', side_effect=held_lease): refusal(supervisor, driver)
    assert supervisor.last_cleanup['HeldResourceConditionsSatisfied'] and len(created[0].output['stdout']) <= 12
    # The retained lease buffer never exceeds its configured limit; no real pipe was read.
    assert not supervisor.admission_refused
    passed += 1
    driver = Driver('timeout'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver, timeout=.3)
    assert supervisor.last_cleanup['HeldResourceConditionsSatisfied'] and driver.signal_calls == 1
    passed += 1
    for fault in ('selector-failure', 'register-failure'):
        driver = Driver(fault); supervisor = subject.CommandSupervisor(driver)
        refusal(supervisor, driver)
        assert supervisor.last_cleanup['HeldResourceConditionsSatisfied'] and set(driver.close_attempts) == {'stdout', 'stderr', 'selector', 'pidfd'}
        passed += 1
    driver = Driver('opaque-constructor'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver)
    assert supervisor.quarantine is not None and supervisor.last_cleanup['BirthAssociationUnproved']
    assert not supervisor.last_cleanup['SameSessionCleanupVerified'] and driver.signal_calls == 0
    refusal(supervisor, driver); assert driver.starts == 1
    passed += 1
    driver = Driver('pidfd-failure'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver)
    assert supervisor.quarantine is not None and not supervisor.last_cleanup['RetainedPidfdBound']
    assert {'stdout', 'stderr', 'selector'}.issubset(driver.close_attempts)
    passed += 1
    driver = Driver('generation-mismatch'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver)
    assert supervisor.quarantine is not None and driver.signal_calls == 0
    assert supervisor.last_cleanup['CleanupOriginallyFailed']
    passed += 1
    driver = Driver('close-failure'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver)
    assert supervisor.quarantine is not None and supervisor.last_cleanup['StderrClosed'] and supervisor.last_cleanup['PidfdClosed']
    assert not supervisor.last_cleanup['StdoutClosed']
    recovered = supervisor.retry_quarantined_cleanup()
    assert recovered['HeldResourceConditionsSatisfied'] and not recovered['SameSessionCleanupVerified'] and recovered['CleanupOriginallyFailed'] and supervisor.admission_refused
    try: supervisor.retry_quarantined_cleanup()
    except subject.CommandRefused: pass
    else: raise AssertionError('Recovery attempts must remain finite')
    refusal(supervisor, driver); assert driver.starts == 1
    passed += 1
    driver = Driver('signal-failure'); supervisor = subject.CommandSupervisor(driver)
    refusal(supervisor, driver, timeout=.3)
    assert supervisor.quarantine is not None and not supervisor.last_cleanup['StdoutEof']
    assert not supervisor.last_cleanup['StdoutClosed'] and not supervisor.last_cleanup['PidfdClosed']
    recovered = supervisor.retry_quarantined_cleanup()
    assert recovered['HeldResourceConditionsSatisfied'] and not recovered['SameSessionCleanupVerified'] and recovered['CleanupOriginallyFailed'] and driver.signal_calls == 2
    refusal(supervisor, driver); assert driver.starts == 1
    passed += 1
    # A failed raw pidfd close is not retried against a possibly reused descriptor number.
    fd = subject.PidfdHandle(17)
    with patch.object(subject.os, 'close', side_effect=InterruptedError('private-opaque-value')) as mocked:
        try: subject.LinuxDriver().close(fd)
        except InterruptedError: pass
        else: raise AssertionError('Expected close uncertainty')
        try: subject.LinuxDriver().close(fd)
        except subject.CommandRefused: pass
        else: raise AssertionError('Uncertain raw fd close must remain refused')
        assert mocked.call_count == 1 and fd.close_uncertain
    passed += 1
    driver = Driver(); supervisor = subject.CommandSupervisor(driver, command_limit=1)
    supervisor.capture(['synthetic-command'])
    refusal(supervisor, driver); assert driver.starts == 1
    passed += 1
    driver = Driver(); supervisor = subject.CommandSupervisor(driver)
    assert supervisor._gate.acquire(blocking=False)
    try: refusal(supervisor, driver)
    finally: supervisor._gate.release()
    assert driver.starts == 0
    passed += 1
    for arguments in ([], [''], ['x\0y'], ['x'] * 129):
        driver = Driver(); supervisor = subject.CommandSupervisor(driver)
        try: supervisor.capture(arguments)
        except subject.CommandRefused: pass
        else: raise AssertionError('Invalid input must fail before modeled birth')
        assert driver.starts == 0
        passed += 1
    return {'PureAlgorithmControlsPassed': passed, 'InjectedDriverOnly': True,
        'ActualChildrenStarted': False, 'ActualDescriptorCensusPerformed': False,
        'ActualKernelResourceCapsObserved': False, 'HostedLifecycleAccepted': False,
        'GeneralSourceGitCleanupAccepted': False, 'GenuineEightHostFinancialAccepted': False}


if __name__ == '__main__':
    print(json.dumps(controls(), sort_keys=True))
