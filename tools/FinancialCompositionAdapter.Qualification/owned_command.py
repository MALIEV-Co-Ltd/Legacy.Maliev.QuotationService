"""Bounded private command capture; ownership scope is the retained child's session.

No process is started at import. Escaped sessions and a Popen constructor which
fails after an opaque birth are NOT covered by a successful cleanup receipt.
"""
import ctypes
from dataclasses import dataclass, field
import os
import pathlib
import re
import selectors
import signal
import subprocess
import threading
import time


class CommandRefused(RuntimeError):
    def __init__(self, kind, receipt=None):
        # Never include arguments, cwd, environment, output or underlying errors.
        super().__init__('Owned command refused')
        self.kind = kind
        self.receipt = receipt


def require(condition):
    if not condition:
        raise CommandRefused('ownership-or-budget')


def parse_generation(raw, pid):
    require(type(raw) is bytes and 0 < len(raw) <= 8192)
    end = raw.rfind(b')')
    require(end > 0 and int(raw[:raw.index(b'(')].strip()) == pid)
    fields = raw[end + 2:].split()
    require(len(fields) >= 20)
    return (int(fields[19]), int(fields[1]), int(fields[2]), int(fields[3]), fields[0].decode('ascii'))


class LinuxDriver:
    """Real backend. Pure controls supply a different driver, never this one."""
    def clock(self): return time.monotonic()
    def parent(self): return os.getpid()

    def admit(self):
        require(os.name == 'posix' and pathlib.Path('/proc/self/stat').is_file())
        libc = ctypes.CDLL(None, use_errno=True)
        require(libc.prctl(36, 1, 0, 0, 0) == 0)  # PR_SET_CHILD_SUBREAPER
        enabled = ctypes.c_int()
        require(libc.prctl(37, ctypes.byref(enabled), 0, 0, 0) == 0 and enabled.value == 1)
        require(hasattr(os, 'pidfd_open') and hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'))

    def start(self, arguments, cwd, env):
        return subprocess.Popen(arguments, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)

    def generation(self, pid):
        with open('/proc/' + str(pid) + '/stat', 'rb') as stream:
            return parse_generation(stream.read(8193), pid)

    def members(self, session, deadline):
        observed = {}
        # Unlike list(iterdir), this refuses BEFORE unbounded enumeration allocation.
        with os.scandir('/proc') as entries:
            count = 0
            for entry in entries:
                count += 1
                require(count <= 32768 and self.clock() < deadline)
                if re.fullmatch(r'[1-9][0-9]*', entry.name) is None: continue
                try: identity = self.generation(int(entry.name))
                except FileNotFoundError: continue
                if identity[3] == session: observed[int(entry.name)] = identity
        return observed

    def pidfd(self, process):
        handle = PidfdHandle(-1)  # Allocate the retaining wrapper before opening the fd.
        handle.fd = os.pidfd_open(process.pid, 0)
        return handle
    def selector(self): return selectors.DefaultSelector()
    def register(self, selector, stream, name):
        os.set_blocking(stream.fileno(), False)
        selector.register(stream, selectors.EVENT_READ, name)
    def events(self, selector, delay): return [key for key, _ in selector.select(delay)]
    def read(self, key, maximum): return os.read(key.fileobj.fileno(), maximum)
    def remove(self, selector, key): selector.unregister(key.fileobj)
    def has_readers(self, selector): return bool(selector.get_map())
    def exit_observation(self, process):
        status = os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        if status is None: return None
        return status.si_status if status.si_code == os.CLD_EXITED else -status.si_status
    def pause(self): time.sleep(.01)
    def kill_group(self, process): os.killpg(process.pid, signal.SIGKILL)
    def wait(self, process, remaining, anchor):
        # Do not use Popen.wait's ECHILD=>0 fallback as evidence of original reaping.
        require(anchor is not None)
        end = self.clock() + remaining
        while True:
            require(self.generation(process.pid)[:4] == anchor[:4])
            status = os.waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
            if status is not None:
                waited, actual_status = os.waitpid(process.pid, os.WNOHANG)
                require(waited == process.pid)
                process.returncode = os.waitstatus_to_exitcode(actual_status)
                return
            require(self.clock() < end)
            self.pause()
    def reap(self, pid, identity):
        require(self.generation(pid) == identity)
        reaped, _ = os.waitpid(pid, os.WNOHANG)
        require(reaped in (0, pid))
    def close(self, handle):
        if type(handle) is PidfdHandle:
            require(not handle.close_uncertain)
            if not handle.closed:
                # Never blindly retry a raw fd close whose outcome is uncertain.
                try: os.close(handle.fd); handle.closed = True
                except BaseException: handle.close_uncertain = True; raise
        else:
            handle.close()
            require(handle.closed if hasattr(handle, 'closed') else not handle.get_map())


@dataclass
class PidfdHandle:
    fd: int
    closed: bool = False
    close_uncertain: bool = False


@dataclass
class Lease:
    process: object = None
    selector: object = None
    pidfd: object = None
    anchor: object = None
    attempted: bool = False
    associated: bool = False
    bound: bool = False
    reaped: bool = False
    session_empty: bool = False
    eof: dict = field(default_factory=lambda: {'stdout': False, 'stderr': False})
    closed: dict = field(default_factory=lambda: {'stdout': False, 'stderr': False, 'selector': False, 'pidfd': False})
    cleanup_failed: bool = False
    failed_birth_association: bool = False
    recovery_attempted: bool = False
    actual_backend: bool = False
    output: dict = field(default_factory=lambda: {'stdout': bytearray(), 'stderr': bytearray()})
    streams: dict = field(default_factory=lambda: {'stdout': None, 'stderr': None})

    def conditions_satisfied(self):
        return self.associated and self.bound and self.reaped and self.session_empty and all(self.eof.values()) and all(self.closed.values())

    def receipt(self):
        conditions = self.conditions_satisfied()
        return {'BirthAttempted': self.attempted, 'ProcessAssociated': self.associated,
            'RetainedPidfdBound': self.bound, 'OriginalLeaderReaped': self.reaped,
            'OwnedSessionAbsent': self.session_empty, 'StdoutEof': self.eof['stdout'], 'StderrEof': self.eof['stderr'],
            'StdoutClosed': self.closed['stdout'], 'StderrClosed': self.closed['stderr'],
            'SelectorClosed': self.closed['selector'], 'PidfdClosed': self.closed['pidfd'],
            'CleanupOriginallyFailed': self.cleanup_failed, 'HeldResourceConditionsSatisfied': conditions,
            'ActualLinuxBackendUsed': self.actual_backend, 'SameSessionCleanupVerified': self.actual_backend and conditions,
            'BirthAssociationUnproved': self.failed_birth_association, 'EscapedSessionDescendantsAccepted': False,
            'KernelResourceCapsObserved': False, 'IndependentFdCensusAccepted': False}


@dataclass(frozen=True)
class CommandResult:
    stdout: bytes
    stderr: bytes
    returncode: int
    cleanup: dict


class CommandSupervisor:
    def __init__(self, driver=None, *, stream_limit=8 * 1024 * 1024, cleanup_seconds=5, command_limit=256):
        require(type(stream_limit) is int and 0 < stream_limit <= 8 * 1024 * 1024)
        require(type(command_limit) is int and 0 < command_limit <= 256
                and type(cleanup_seconds) in (int, float) and 0 < cleanup_seconds <= 5)
        self.driver = driver if driver is not None else LinuxDriver()
        self.stream_limit, self.cleanup_seconds, self.command_limit = stream_limit, cleanup_seconds, command_limit
        self.commands = 0
        self.quarantine = None
        self.admission_refused = False
        self.last_cleanup = None
        self._gate = threading.Lock()

    def _anchor(self, lease):
        driver, process = self.driver, lease.process
        current = driver.generation(process.pid)
        require(process.returncode is None and current[1:4] == (driver.parent(), process.pid, process.pid))
        if lease.anchor is not None: require(current[:4] == lease.anchor[:4])
        lease.anchor = current
        return current

    def _drain(self, lease, end, retain):
        driver = self.driver
        while driver.has_readers(lease.selector):
            require(driver.clock() < end)
            for key in driver.events(lease.selector, min(.1, max(0, end - driver.clock()))):
                name = key.data
                require(name in lease.output)
                remaining = self.stream_limit - len(lease.output[name])
                try: piece = driver.read(key, min(4096, remaining + 1) if retain else 4096)
                except BlockingIOError: continue  # Spurious readiness is not invented EOF.
                if not piece:
                    lease.eof[name] = True
                    driver.remove(lease.selector, key)
                elif retain:
                    # Refuse BEFORE extending a retained buffer past its byte budget.
                    require(len(piece) <= remaining)
                    lease.output[name].extend(piece)

    def _cleanup(self, lease):
        driver = self.driver
        end = driver.clock() + self.cleanup_seconds
        failures = []
        if lease.process is None:
            lease.failed_birth_association = lease.attempted
            lease.cleanup_failed = True
            return lease.receipt()
        # Metadata/setup uncertainty does not skip independent signal, EOF, reap or close attempts.
        if not lease.reaped:
            try:
                anchor = self._anchor(lease)
                members = driver.members(lease.process.pid, end)
                require(members.get(lease.process.pid, ())[:4] == anchor[:4])
                self._anchor(lease)  # Reserved un-reaped leader checked immediately before signal.
                try: driver.kill_group(lease.process)
                except ProcessLookupError:
                    require(anchor[4] == 'Z' and all(item[4] == 'Z' for item in members.values()))
            except BaseException: failures.append('session-signal')
        for name in ('stdout', 'stderr'):
            if lease.streams[name] is None:
                try: lease.streams[name] = getattr(lease.process, name)
                except BaseException: failures.append('missing-' + name)
        try:
            if lease.selector is None:
                lease.selector = driver.selector()
            existing = {key.data for key in getattr(lease.selector, 'get_map', lambda: {})().values()}
            for name in ('stdout', 'stderr'):
                if name not in existing and not lease.eof[name] and not lease.closed[name]:
                    require(lease.streams[name] is not None)
                    driver.register(lease.selector, lease.streams[name], name)
            self._drain(lease, end, retain=False)
        except BaseException: failures.append('reader-drain')
        if not lease.reaped:
            try:
                driver.wait(lease.process, max(.001, end - driver.clock()), lease.anchor)
                lease.reaped = lease.process.returncode is not None
                require(lease.reaped)
            except BaseException: failures.append('leader-reap')
        try:
            while True:
                members = driver.members(lease.process.pid, end)
                if not members: lease.session_empty = True; break
                for pid, identity in members.items():
                    if pid != lease.process.pid and identity[1] == driver.parent() and identity[4] == 'Z':
                        driver.reap(pid, identity)
                require(driver.clock() < end)
                driver.pause()
        except BaseException: failures.append('session-census')
        for name, handle in (('selector', lease.selector), ('stdout', lease.streams['stdout']), ('stderr', lease.streams['stderr']), ('pidfd', lease.pidfd)):
            if lease.closed[name]: continue
            if handle is None: failures.append('missing-' + name); continue
            # Retain incomplete original readers for retry, rather than manufacture EOF by closing.
            if name in lease.eof and not lease.eof[name]: failures.append('retained-' + name); continue
            if name == 'selector' and not all(lease.eof.values()): failures.append('retained-selector'); continue
            if name == 'pidfd' and not lease.reaped: failures.append('retained-pidfd'); continue
            try: driver.close(handle); lease.closed[name] = True
            except BaseException: failures.append('close-' + name)
        lease.cleanup_failed = lease.cleanup_failed or bool(failures)
        return lease.receipt()

    def capture(self, arguments, *, cwd=None, timeout=180, env=None):
        require(self._gate.acquire(blocking=False))
        try: return self._capture(arguments, cwd=cwd, timeout=timeout, env=env)
        finally: self._gate.release()

    def _capture(self, arguments, *, cwd=None, timeout=180, env=None):
        require(not self.admission_refused and self.commands < self.command_limit)
        require(type(arguments) in (list, tuple) and 0 < len(arguments) <= 128
                and all(type(item) is str and 0 < len(item) <= 32768 and '\0' not in item for item in arguments))
        require(type(timeout) in (int, float) and 0 < timeout <= 600)
        self.driver.admit()  # Before birth; import/pure controls have no process side effects.
        self.commands += 1
        lease = Lease()
        lease.actual_backend = type(self.driver) is LinuxDriver
        primary = None
        result = None
        end = self.driver.clock() + timeout
        try:
            lease.attempted = True
            lease.process = self.driver.start(arguments, cwd, env)
            lease.associated = True
            self._anchor(lease)
            lease.pidfd = self.driver.pidfd(lease.process)
            self._anchor(lease)
            lease.bound = True
            lease.selector = self.driver.selector()
            for name in ('stdout', 'stderr'):
                lease.streams[name] = getattr(lease.process, name)
                self.driver.register(lease.selector, lease.streams[name], name)
            self._drain(lease, end, retain=True)
            while True:
                require(self.driver.clock() < end)
                code = self.driver.exit_observation(lease.process)
                if code is not None: break
                self.driver.pause()
            result = (bytes(lease.output['stdout']), bytes(lease.output['stderr']), code)
        except BaseException:
            primary = CommandRefused('command-or-setup')
        finally:
            try: receipt = self._cleanup(lease)
            except BaseException:
                lease.cleanup_failed = True
                receipt = lease.receipt()
            self.last_cleanup = receipt
            if not lease.conditions_satisfied() or receipt['CleanupOriginallyFailed']:
                self.admission_refused = True
                self.quarantine = lease  # Keep original process/selector/streams/pidfd and buffers reachable.
                primary = CommandRefused('cleanup-quarantined', receipt)
        if primary is not None: raise primary
        return CommandResult(*result, receipt)

    def retry_quarantined_cleanup(self):
        require(self._gate.acquire(blocking=False))
        try: return self._retry_quarantined_cleanup()
        finally: self._gate.release()

    def _retry_quarantined_cleanup(self):
        require(self.quarantine is not None and self.admission_refused)
        require(not self.quarantine.recovery_attempted)
        self.quarantine.recovery_attempted = True
        receipt = self._cleanup(self.quarantine)
        self.last_cleanup = receipt
        # Physical recovery never clears the original failure or allows another birth.
        return receipt


_supervisor = CommandSupervisor()


def capture(arguments, *, cwd=None, timeout=180, env=None):
    return _supervisor.capture(arguments, cwd=cwd, timeout=timeout, env=env)


def run(arguments, *, cwd=None, timeout=180, env=None):
    result = capture(arguments, cwd=cwd, timeout=timeout, env=env)
    if result.returncode != 0: raise CommandRefused('command-exit', result.cleanup)
    return result.stdout
