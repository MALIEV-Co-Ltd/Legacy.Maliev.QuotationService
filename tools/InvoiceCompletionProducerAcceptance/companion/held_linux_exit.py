"""Borrow an owner's unreaped child/pidfd; never start, poll, reap or dispose."""
from dataclasses import dataclass
import os
import subprocess


class ExitObservationRefused(RuntimeError):
    def __init__(self):
        super().__init__('Held Linux exit observation refused')


def require(condition):
    if not condition:
        raise ExitObservationRefused()


@dataclass(frozen=True)
class OriginalIdentity:
    pid: int
    start_ticks: int
    parent_pid: int
    process_group: int
    session: int


@dataclass(frozen=True)
class ExitObservation:
    settled: bool
    exit_code: object
    # Observation is not cleanup, reaping, EOF or permission to acknowledge.
    reaped: bool = False
    disposed: bool = False


def bounded_file(path, cap):
    with open(path, 'rb') as stream:
        raw = stream.read(cap + 1)
    require(0 < len(raw) <= cap)
    return raw


def generation(pid):
    raw = bounded_file('/proc/' + str(pid) + '/stat', 8192)
    end = raw.rfind(b')')
    require(end > 0 and int(raw[:raw.index(b'(')].strip()) == pid)
    fields = raw[end + 2:].split()
    require(len(fields) >= 20)
    return (int(fields[19]), int(fields[1]), int(fields[2]), int(fields[3]), fields[0])


def verify_pidfd(fd, pid):
    require(os.readlink('/proc/self/fd/' + str(fd)) == 'anon_inode:[pidfd]')
    raw = bounded_file('/proc/self/fdinfo/' + str(fd), 4096)
    selected = {}
    for line in raw.splitlines():
        name, separator, value = line.partition(b':')
        if name in (b'Pid', b'NSpid'):
            require(separator and name not in selected)
            selected[name] = value.split()
    # Namespace-translated/expired/unbound descriptors are unsupported.
    require(selected == {b'Pid': [str(pid).encode()], b'NSpid': [str(pid).encode()]})


def observe_original(process, pidfd, identity):
    """Caller holds its exclusive lifetime fence throughout this borrowed call.

    identity must be the owner's independent original birth snapshot, not a
    freshly adopted PID. pidfd is its retained handle with fd/closed fields.
    No thread may poll/wait/reap/close either original handle during observation.
    """
    try:
        require(os.name == 'posix' and type(identity) is OriginalIdentity)
        require(isinstance(process, subprocess.Popen) and process.returncode is None)
        require(all(type(value) is int and value > 0 for value in
                    (identity.pid, identity.start_ticks, identity.parent_pid,
                     identity.process_group, identity.session)))
        require(process.pid == identity.pid and identity.parent_pid == os.getpid())
        require(type(pidfd.fd) is int and pidfd.fd >= 0 and type(pidfd.closed) is bool and not pidfd.closed)
        require(type(pidfd.close_uncertain) is bool and not pidfd.close_uncertain)
        held_fd = pidfd.fd
        require(hasattr(os, 'waitid') and hasattr(os, 'WNOWAIT'))
        expected = (identity.start_ticks, identity.parent_pid, identity.process_group, identity.session)
        require(generation(identity.pid)[:4] == expected)
        verify_pidfd(held_fd, identity.pid)
        kind = os.P_PIDFD if hasattr(os, 'P_PIDFD') else os.P_PID
        selected = held_fd if hasattr(os, 'P_PIDFD') else identity.pid
        status = os.waitid(kind, selected, os.WEXITED | os.WNOHANG | os.WNOWAIT)
        # Revalidate even after a nonsettled result. ECHILD is refusal, never0.
        require(process.returncode is None and pidfd.fd == held_fd and not pidfd.closed and not pidfd.close_uncertain)
        verify_pidfd(held_fd, identity.pid)
        after = generation(identity.pid)
        require(after[:4] == expected)
        if status is None:
            return ExitObservation(False, None)
        require(status.si_pid == identity.pid and type(status.si_status) is int)
        require(after[4] == b'Z')
        if status.si_code == os.CLD_EXITED:
            require(0 <= status.si_status <= 255)
            return ExitObservation(True, status.si_status)
        require(status.si_code in (os.CLD_KILLED, os.CLD_DUMPED) and 0 < status.si_status <= 64)
        return ExitObservation(True, -status.si_status)
    except BaseException:
        raise ExitObservationRefused() from None
