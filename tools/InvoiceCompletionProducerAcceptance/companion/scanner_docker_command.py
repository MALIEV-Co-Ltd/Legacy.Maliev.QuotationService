"""Exclusive, exact-leader Linux Docker CLI lease. No descendant or OS-cap proof.

Private retained objects are not artifact data. Every uncertain owner remains
reachable; callers must serialize Docker commands and retain this module alive.
"""
import os
import selectors
import signal
import stat
import subprocess
import time
import threading
from pathlib import Path
from types import MappingProxyType

_OWNERS = []
_HISTORY = []
_FENCE = threading.Lock()


def command_receipts():
    """Bounded read-only, private-safe snapshots; not independent FD census/caps."""
    return tuple(MappingProxyType(dict(receipt)) for receipt in _HISTORY)


class DockerLifecycleError(RuntimeError):
    pass


def _bounded(path):
    with open(path, "rb") as stream:
        value = stream.read(8193)
    if len(value) > 8192:
        raise DockerLifecycleError("Owner metadata exceeded bound")
    return value.decode("ascii", "strict")


def _birth(pid):
    text = _bounded(Path("/proc") / str(pid) / "stat")
    fields = text[text.rindex(")") + 2:].split()
    if len(fields) < 20 or int(text.split("(", 1)[0]) != pid:
        raise DockerLifecycleError("Original owner metadata refused")
    return (pid, int(fields[19]), int(fields[1]), int(fields[2]), int(fields[3]))


def _fd_pid(fd):
    fields = [line[4:].strip() for line in _bounded(Path("/proc/self/fdinfo") / str(fd)).splitlines() if line.startswith("Pid:")]
    if len(fields) != 1:
        raise DockerLifecycleError("Original descriptor binding refused")
    return int(fields[0])


def _pipe(stream):
    fd = stream.fileno()
    value = os.fstat(fd)
    if not stat.S_ISFIFO(value.st_mode):
        raise DockerLifecycleError("Original reader binding refused")
    return (fd, value.st_dev, value.st_ino)


def _descriptor(fd):
    value = os.fstat(fd)
    link = os.readlink(Path("/proc/self/fd") / str(fd))
    if link != "anon_inode:[pidfd]":
        raise DockerLifecycleError("Retained descriptor type refused")
    return (value.st_dev, value.st_ino, value.st_mode, link)


class _Lease:
    def __init__(self, timeout):
        self.end = time.monotonic() + timeout
        self.process = None
        self.birth_attempted = False
        self.birth = None
        self.pidfd = None
        self.pidfd_attempted = False
        self.pidfd_identity = None
        self.selector = None
        self.selector_attempted = False
        self.streams = []
        self.bindings = {}
        self.eof = set()
        self.output = {}
        self.original_failure = None
        self.cleanup_failures = []
        self.reaped = False
        self.selector_closed = False
        self.pidfd_closed = False
        self.pidfd_close_attempted = False
        self.selector_close_attempted = False
        self.stream_close_attempted = set()
        self.closed = set()
        self.attempts = []
        self.receipt = {"originalFailure": False, "returnedProcessObserved": False,
                        "generationBound": False, "originalReaped": False,
                        "bothReadersEof": False, "bothReadersClosed": False,
                        "selectorCloseCompleted": False, "pidfdCloseCompleted": False,
                        "cleanupVerified": False, "quarantined": True,
                        "descendantCleanupProved": False, "kernelCapsObserved": False,
                        "originalExitCode": None, "cleanupAttempts": (), "attemptLedgerCapped": False}

    def bind(self):
        process = self.process
        # Retain the returned original pidfd before fallible reader/metadata
        # setup. The lifetime fence forbids other reapers of this Popen child.
        self.pidfd_attempted = True
        self.pidfd = os.pidfd_open(process.pid, 0)
        self.bind_original()
        self.streams = [process.stdout, process.stderr]
        if any(stream is None for stream in self.streams) or self.streams[0] is self.streams[1]:
            raise DockerLifecycleError("Two retained original readers required")
        for stream in self.streams:
            self.output[stream] = bytearray()
            self.bindings[stream] = _pipe(stream)
    def bind_original(self):
        if self.pidfd is None or self.pidfd_close_attempted:
            raise DockerLifecycleError("Original descriptor acquisition unsettled")
        if self.pidfd_identity is None:
            self.pidfd_identity = _descriptor(self.pidfd)
        if _descriptor(self.pidfd) != self.pidfd_identity or _fd_pid(self.pidfd) != self.process.pid:
            raise DockerLifecycleError("Original descriptor association refused")
        observed = _birth(self.process.pid)
        if observed[2] != os.getpid() or observed[3:] != (self.process.pid, self.process.pid):
            raise DockerLifecycleError("Original child session refused")
        if self.birth is None:
            self.birth = observed
        self.require_original()
        self.receipt["generationBound"] = True

    def require_original(self):
        if self.pidfd is None or self.birth is None or self.pidfd_identity is None or _descriptor(self.pidfd) != self.pidfd_identity or _fd_pid(self.pidfd) != self.process.pid or _birth(self.process.pid) != self.birth:
            raise DockerLifecycleError("Retained original generation refused")

    def require_pipe(self, stream):
        if _pipe(stream) != self.bindings[stream]:
            raise DockerLifecycleError("Retained original pipe changed")

    def wait_original(self, end):
        # Popen.wait treats ECHILD as exit0; it is not original-reaping evidence.
        while True:
            if time.monotonic() >= end:
                raise TimeoutError("Original wait deadline expired")
            self.require_original()
            status = os.waitid(os.P_PIDFD, self.pidfd, os.WEXITED | os.WNOHANG | os.WNOWAIT)
            if time.monotonic() >= end:
                raise TimeoutError("Original wait observation exceeded deadline")
            if status is not None:
                if status.si_pid != self.process.pid or status.si_code not in (os.CLD_EXITED, os.CLD_KILLED, os.CLD_DUMPED):
                    raise DockerLifecycleError("Original wait observation refused")
                self.require_original()
                waited, raw_status = os.waitpid(self.process.pid, os.WNOHANG)
                if waited != self.process.pid:
                    raise DockerLifecycleError("Exact original reaping refused")
                self.process.returncode = os.waitstatus_to_exitcode(raw_status)
                self.reaped = True
                expected = status.si_status if status.si_code == os.CLD_EXITED else -status.si_status
                if self.process.returncode != expected:
                    raise DockerLifecycleError("Original exit status changed")
                if time.monotonic() >= end:
                    raise TimeoutError("Original reaping exceeded deadline")
                return
            time.sleep(min(0.01, max(0, end - time.monotonic())))

    def read(self, stream, collect):
        self.require_pipe(stream)
        chunk = os.read(self.bindings[stream][0], 4096)
        if not chunk:
            self.eof.add(stream)
            return False
        if collect:
            if sum(map(len, self.output.values())) + len(chunk) > 262144:
                raise ValueError("Docker output exceeded measurement bound")
            self.output[stream].extend(chunk)
        return True

    def drain(self):
        self.selector_attempted = True
        self.selector = selectors.DefaultSelector()
        for stream in self.streams:
            self.require_pipe(stream)
            os.set_blocking(self.bindings[stream][0], False)
            self.selector.register(stream, selectors.EVENT_READ)
        while self.selector.get_map():
            remaining = self.end - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Docker command deadline expired")
            for key, _ in self.selector.select(min(remaining, 0.1)):
                if key.fileobj not in self.streams:
                    raise DockerLifecycleError("Unexpected command reader")
                if not self.read(key.fileobj, True):
                    self.selector.unregister(key.fileobj)
        self.wait_original(self.end)

    def cleanup(self):
        """Fixed existing five-second cleanup budget; independent attempts, sticky faults."""
        cleanup_end = time.monotonic() + 5
        def attempt(label, action):
            outcome = "completed"
            try:
                action()
            except BaseException as error:
                outcome = "refused"
                if len(self.cleanup_failures) < 32:
                    self.cleanup_failures.append(error)
            if len(self.attempts) < 64:
                self.attempts.append((label, outcome))
            else:
                self.receipt["attemptLedgerCapped"] = True

        def stop():
            if not self.reaped:
                self.require_original()
                status = os.waitid(os.P_PIDFD, self.pidfd, os.WEXITED | os.WNOHANG | os.WNOWAIT)
                if status is None:
                    signal.pidfd_send_signal(self.pidfd, signal.SIGKILL, None, 0)
                elif status.si_pid != self.process.pid or status.si_code not in (os.CLD_EXITED, os.CLD_KILLED, os.CLD_DUMPED):
                    raise DockerLifecycleError("Original exit observation refused")

        def reap():
            if not self.reaped:
                self.wait_original(cleanup_end)

        if self.process is not None:
            if not self.reaped:
                attempt("recoverOriginalBinding", self.bind_original)
            attempt("stopOriginal", stop)
            attempt("reapOriginal", reap)
            # Setup may have failed before streams were registered. Recover only
            # the two returned original objects; never arbitrary descriptor IDs.
            for stream in (self.process.stdout, self.process.stderr):
                if stream is not None and stream not in self.streams:
                    self.streams.append(stream)
            for stream in self.streams:
                def drain_one(stream=stream):
                    if stream in self.closed:
                        if stream not in self.eof:
                            raise DockerLifecycleError("Closed reader lacks original EOF")
                        return
                    if stream in self.stream_close_attempted:
                        raise DockerLifecycleError("Reader close remains uncertain")
                    if stream not in self.bindings:
                        self.bindings[stream] = _pipe(stream)
                    self.require_pipe(stream)
                    os.set_blocking(self.bindings[stream][0], False)
                    while stream not in self.eof:
                        if time.monotonic() >= cleanup_end:
                            raise TimeoutError("Original reader cleanup deadline expired")
                        try:
                            self.read(stream, False)
                        except BlockingIOError:
                            time.sleep(min(0.01, max(0, cleanup_end - time.monotonic())))
                attempt("stdoutEof" if stream is self.process.stdout else "stderrEof", drain_one)

        if self.selector is not None:
            def close_selector():
                if self.selector_closed:
                    return
                if self.selector_close_attempted:
                    raise DockerLifecycleError("Selector close remains uncertain")
                self.selector_close_attempted = True
                self.selector.close()
                self.selector_closed = True
            attempt("selectorClose", close_selector)
        else:
            # A throwing constructor may have created an unreturned descriptor.
            # We cannot invent ownership/closure evidence for that partial birth.
            self.selector_closed = not self.selector_attempted
        for stream in self.streams:
            def close_stream(stream=stream):
                if stream in self.closed:
                    return
                if stream in self.stream_close_attempted:
                    raise DockerLifecycleError("Reader close remains uncertain")
                self.require_pipe(stream)
                self.stream_close_attempted.add(stream)
                stream.close()
                if not stream.closed:
                    raise DockerLifecycleError("Original reader close unsettled")
                self.closed.add(stream)
            attempt("stdoutClose" if stream is self.process.stdout else "stderrClose", close_stream)
        # An unsettled original retains its pidfd. Close independently only after
        # actual wait succeeded; a failed close keeps the descriptor owner alive.
        if self.pidfd is not None and self.reaped:
            def close_pidfd():
                if self.pidfd_closed:
                    return
                if self.pidfd_close_attempted:
                    raise DockerLifecycleError("Descriptor close remains uncertain")
                if self.pidfd_identity is None or _descriptor(self.pidfd) != self.pidfd_identity:
                    raise DockerLifecycleError("Original descriptor identity changed before close")
                self.pidfd_close_attempted = True
                os.close(self.pidfd)
                self.pidfd_closed = True
            attempt("pidfdClose", close_pidfd)
        self.receipt.update(originalFailure=self.original_failure is not None,
                            returnedProcessObserved=self.process is not None,
                            originalReaped=self.reaped,
                            bothReadersEof=len(self.streams) == 2 and len(self.eof) == 2,
                            bothReadersClosed=len(self.streams) == 2 and len(self.closed) == 2,
                            selectorCloseCompleted=self.selector_closed,
                            pidfdCloseCompleted=self.pidfd_closed,
                            originalExitCode=self.process.returncode if self.reaped else None,
                            cleanupAttempts=tuple(self.attempts))
        clean = (not self.cleanup_failures and self.receipt["generationBound"] and self.reaped
                 and self.receipt["bothReadersEof"] and self.receipt["bothReadersClosed"]
                 and self.selector_closed and self.pidfd_closed)
        self.receipt.update(cleanupVerified=clean, quarantined=not clean)
        if clean and self in _OWNERS:
            _OWNERS.remove(self)


def run_docker(args, timeout=30, capture_stderr=False):
    if type(timeout) not in (int, float) or not 0 < timeout <= 300:
        raise ValueError("Finite Docker command timeout required")
    if not _FENCE.acquire(blocking=False):
        raise DockerLifecycleError("Command lifetime fence already held")
    try:
        return _run_fenced(args, timeout, capture_stderr)
    finally:
        _FENCE.release()


def _run_raw(executable, args, timeout):
    if executable not in ('docker', 'git'):
        raise ValueError('Reviewed command executable required')
    if type(timeout) not in (int, float) or not 0 < timeout <= 300:
        raise ValueError('Finite owned command timeout required')
    if not _FENCE.acquire(blocking=False):
        raise DockerLifecycleError('Command lifetime fence already held')
    try:
        return _run_fenced(args, timeout, False, executable=executable, raw_stdout=True)
    finally:
        _FENCE.release()


def run_docker_bytes(args, timeout=30):
    """Untouched stdout bytes, same aggregate cap/lease/fence/cleanup as run_docker."""
    return _run_raw('docker', args, timeout)


def run_git_bytes(args, timeout=30):
    """Exact source/build Git query lane; no shell or unknown executable birth."""
    return _run_raw('git', args, timeout)


def _run_fenced(args, timeout, capture_stderr, executable="docker", raw_stdout=False):
    if _OWNERS or len(_HISTORY) >= 256:
        raise DockerLifecycleError("Prior quarantine or command ledger capacity refused")
    lease = _Lease(timeout)
    _OWNERS.append(lease)  # Retain before birth, including constructor ambiguity.
    answer = None
    try:
        lease.birth_attempted = True
        lease.process = subprocess.Popen([executable, *args], stdin=subprocess.DEVNULL,
                                         stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                         start_new_session=True)
        lease.bind()
        lease.drain()
        if lease.process.returncode:
            raise subprocess.CalledProcessError(lease.process.returncode, [executable, *args],
                                               stderr=lease.output[lease.process.stderr].decode("utf-8", "replace")[:4096])
        captured = lease.output[lease.process.stdout]
        if capture_stderr:
            captured = captured + lease.output[lease.process.stderr]
        answer = bytes(captured) if raw_stdout else captured.decode("utf-8", "strict").strip()
    except BaseException as error:
        lease.original_failure = error
    finally:
        try:
            lease.cleanup()
        except BaseException as error:
            # A secondary bookkeeping fault cannot erase the reachable owner or
            # turn initialized/previous snapshots into cleanup acceptance.
            if len(lease.cleanup_failures) < 32:
                lease.cleanup_failures.append(error)
            lease.receipt.update(cleanupVerified=False, quarantined=True,
                                 originalFailure=lease.original_failure is not None)
            if lease not in _OWNERS:
                _OWNERS.append(lease)
        finally:
            _HISTORY.append(dict(lease.receipt))
    if not lease.receipt["cleanupVerified"]:
        raise DockerLifecycleError("Original Docker command lifecycle quarantined") from None
    if lease.original_failure is not None:
        raise lease.original_failure
    return answer
