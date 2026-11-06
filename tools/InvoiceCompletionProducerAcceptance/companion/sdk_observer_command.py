"""Private SDK-observer lease adapter, coupled to reviewed scanner command owner.

No descendant, all-FD, kernel-cap, parent-death or constructor recovery proof.
The importing caller must qualify both module bytes before import, retain this
module alive, and serialize all scanner/SDK commands under the original fence.
"""
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import time
import uuid

import scanner_docker_command as command
from regular_owned_files import regular_hash

_OWNERS = []
_HISTORY = []
_MAX_OUTPUT = 16384


class ObserverLifecycleError(RuntimeError):
    pass


def receipts():
    return tuple(dict(row) for row in _HISTORY)


def has_retained_owner():
    return bool(_OWNERS)


def require(condition):
    if not condition:
        raise ObserverLifecycleError("Exact SDK observer admission refused")


def exact_file(value, digest):
    require(type(value) is str and type(digest) is str and len(digest) == 64
            and all(c in "0123456789abcdefABCDEF" for c in digest))
    path = Path(value)
    require(path.is_absolute() and path.resolve() == path
            and all(not entry.is_symlink() for entry in (path, *path.parents))
            and regular_hash(path).upper() == digest.upper())
    return path


def request_identity(descriptor):
    info = os.fstat(descriptor)
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
            and stat.S_IMODE(info.st_mode) == 0o600)
    return (info.st_dev, info.st_ino, info.st_size)


class ObserverLease(command._Lease):
    def read(self, stream, collect):
        self.require_pipe(stream)
        chunk = os.read(self.bindings[stream][0], 4096)
        if not chunk:
            self.eof.add(stream)
            return False
        if collect:
            if sum(map(len, self.output.values())) + len(chunk) > _MAX_OUTPUT:
                raise ObserverLifecycleError("SDK observer output exceeded bound")
            self.output[stream].extend(chunk)
        return True


class RequestOwner:
    def __init__(self, directory, timeout):
        self.path = directory / ("start-" + uuid.uuid4().hex + ".json")
        self.descriptor = None
        self.identity = None
        self.create_attempted = False
        self.close_attempted = False
        self.closed = False
        self.unlinked = False
        self.lease = ObserverLease(timeout)
        self.failure = None
        self.release_failure = None

    def before_deadline(self):
        require(time.monotonic() < self.lease.end)

    def create(self, request):
        self.before_deadline()
        raw = json.dumps(request, separators=(",", ":"), allow_nan=False).encode("utf-8")
        require(0 < len(raw) <= 262144)
        self.before_deadline()
        self.create_attempted = True
        self.descriptor = os.open(self.path, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        self.identity = request_identity(self.descriptor)
        self.before_deadline()
        offset = 0
        while offset < len(raw):
            self.before_deadline()
            written = os.write(self.descriptor, raw[offset:])
            require(type(written) is int and 0 < written <= len(raw) - offset)
            offset += written
            self.before_deadline()
        self.identity = request_identity(self.descriptor)
        require(self.identity[2] == len(raw))
        self.before_deadline()

    def release_request(self):
        # No request mutation while the original helper/reader lifecycle is
        # unresolved. A failed close is never retried by integer descriptor.
        require(self.lease.receipt["cleanupVerified"])
        if self.descriptor is None:
            require(not self.create_attempted)
            return
        require(not self.close_attempted and self.identity is not None
                and request_identity(self.descriptor) == self.identity)
        info = os.lstat(self.path)
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                and (info.st_dev, info.st_ino, info.st_size) == self.identity)
        os.unlink(self.path)
        self.unlinked = True
        self.close_attempted = True
        os.close(self.descriptor)
        self.closed = True

    def snapshot(self):
        return {"OriginalHelperCleanupVerified": self.lease.receipt["cleanupVerified"],
                "OriginalHelperQuarantined": self.lease.receipt["quarantined"],
                "RequestCloseCompleted": self.closed, "RequestUnlinkCompleted": self.unlinked,
                "RequestRetained": not (self.closed and self.unlinked),
                "ObserverOwnerRetained": self in _OWNERS,
                "OriginalFailure": self.failure is not None,
                "RequestReleaseFailure": self.release_failure is not None,
                "DescendantCleanupProved": False, "KernelCapsObserved": False}


def observe(request, directory, dotnet, dotnet_sha256, dll, dll_sha256, environment, timeout):
    require(type(timeout) in (int, float) and 0 < timeout <= 7
            and type(environment) is dict and all(type(k) is str and type(v) is str
                and k and "\0" not in k + v and "=" not in k for k, v in environment.items()))
    executable = exact_file(dotnet, dotnet_sha256)
    assembly = exact_file(dll, dll_sha256)
    directory = Path(directory)
    require(directory.is_absolute() and directory.resolve() == directory and directory.is_dir()
            and all(not entry.is_symlink() for entry in (directory, *directory.parents)))
    require(command._FENCE.acquire(blocking=False))
    try:
        require(not _OWNERS and not command._OWNERS and len(_HISTORY) < 256
                and len(command._HISTORY) < 256)
        owner = RequestOwner(directory, timeout)
        _OWNERS.append(owner)
        command._OWNERS.append(owner.lease)
        answer = None
        try:
            owner.create(request)
            # Revalidate the qualified executable/assembly immediately before
            # original birth; no PATH lookup or altered env/cwd accepted.
            owner.before_deadline()
            exact_file(dotnet, dotnet_sha256)
            owner.before_deadline()
            exact_file(dll, dll_sha256)
            owner.before_deadline()
            previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
            try:
                owner.before_deadline()
                owner.lease.birth_attempted = True
                owner.lease.process = subprocess.Popen([str(executable), str(assembly), str(owner.path)],
                    env=dict(environment), cwd=str(assembly.parent), stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True, start_new_session=True)
            finally:
                signal.pthread_sigmask(signal.SIG_SETMASK, previous)
            owner.lease.bind()
            owner.lease.drain()
            require(owner.lease.process.returncode == 0)
            answer = bytes(owner.lease.output[owner.lease.process.stdout])
        except BaseException as error:
            owner.failure = error
            owner.lease.original_failure = error
        finally:
            try:
                owner.lease.cleanup()
            except BaseException as error:
                owner.lease.cleanup_failures.append(error)
                owner.lease.receipt.update(cleanupVerified=False, quarantined=True)
                if owner.lease not in command._OWNERS:
                    command._OWNERS.append(owner.lease)
            try:
                owner.release_request()
            except BaseException as error:
                owner.release_failure = error
            if owner.lease.receipt["cleanupVerified"] and owner.closed and owner.unlinked:
                _OWNERS.remove(owner)
            # Request uncertainty also fences future Docker/Git operations.
            if owner in _OWNERS and owner.lease not in command._OWNERS:
                command._OWNERS.append(owner.lease)
            command._HISTORY.append(dict(owner.lease.receipt))
            _HISTORY.append(owner.snapshot())
        if owner in _OWNERS:
            raise ObserverLifecycleError("SDK observer original owner quarantined") from None
        if owner.failure is not None:
            raise ObserverLifecycleError("SDK observer original operation refused") from None
        return answer
    finally:
        command._FENCE.release()
