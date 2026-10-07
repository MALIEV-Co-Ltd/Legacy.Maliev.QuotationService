"""Descriptor-bound finite-size regular files, rejecting FIFO and path replacement."""
from contextlib import contextmanager
import hashlib
import os
from pathlib import Path
import signal
import stat

import hosted_companion_resources as h


def _component_open(path, proc_executable):
    parts = Path(path).parts
    h.require(parts[0] == "/" and len(parts) > 1 and str(Path(path)) == path,
              "Canonical absolute component path required")
    directory_flags = os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC | os.O_DIRECTORY | os.O_NOFOLLOW
    directory = os.open("/", directory_flags)
    try:
        for component in parts[1:-1]:
            child = os.open(component, directory_flags, dir_fd=directory)
            os.close(directory)
            directory = child
        flags = os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC | (0 if proc_executable else os.O_NOFOLLOW)
        return os.open(parts[-1], flags, dir_fd=directory)
    finally:
        os.close(directory)


@contextmanager
def open_regular(path, maximum, proc_executable=False):
    h.require(h.sys.platform == "linux" and type(maximum) is int and 0 < maximum <= 64 * 1024**2,
              "Hosted finite regular-file admission required")
    path = str(path)
    if proc_executable:
        import re
        h.require(re.fullmatch(r"/proc/[1-9][0-9]*/exe", path) is not None, "Only typed process executable link allowed")
    else:
        selected = Path(path)
        h.require(selected.is_absolute() and selected.resolve() == selected
                  and all(not item.is_symlink() for item in (selected, *selected.parents)), "Canonical regular file required")
    descriptor = None
    stream = None
    current_descriptor = None
    try:
        # Retain the returned descriptor before a pending owner expiry can unwind.
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
        try:
            descriptor = _component_open(path, proc_executable)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)
        identity = os.fstat(descriptor)
        h.require(stat.S_ISREG(identity.st_mode) and 1 <= identity.st_size <= maximum,
                  "Opened descriptor must be a bounded regular file")
        stream = os.fdopen(descriptor, "rb", buffering=0)
        descriptor = None
        yield stream, identity
        retained = os.fstat(stream.fileno())
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
        try:
            current_descriptor = _component_open(path, proc_executable)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)
        current = os.fstat(current_descriptor)
        h.require((retained.st_dev, retained.st_ino, retained.st_size, retained.st_mtime_ns)
                  == (identity.st_dev, identity.st_ino, identity.st_size, identity.st_mtime_ns)
                  and (current.st_dev, current.st_ino) == (identity.st_dev, identity.st_ino)
                  and stat.S_ISREG(current.st_mode), "Regular file changed or was replaced during observation")
    finally:
        try:
            if current_descriptor is not None:
                os.close(current_descriptor)
        finally:
            if stream is not None:
                stream.close()
            elif descriptor is not None:
                os.close(descriptor)


def regular_bytes(path, maximum):
    with open_regular(path, maximum) as (stream, identity):
        data = stream.read(maximum + 1)
        h.require(len(data) == identity.st_size and len(data) <= maximum, "Exact bounded regular bytes required")
        return data, identity


def regular_hash(path, maximum=64 * 1024**2, proc_executable=False):
    with open_regular(path, maximum, proc_executable) as (stream, identity):
        digest = hashlib.sha256()
        count = 0
        for chunk in iter(lambda: stream.read(65536), b""):
            count += len(chunk)
            h.require(count <= maximum, "Regular file grew beyond bound")
            digest.update(chunk)
        h.require(count == identity.st_size, "Regular file size changed")
        return digest.hexdigest()


def verify_source(spec, deadline):
    root = Path(spec.repository)
    dll = Path(spec.executable_dll)
    h.require(root.is_absolute() and root.resolve() == root and dll.is_absolute()
              and dll.is_relative_to(root), "Exact owned source/build root required")
    values = []
    for arguments in (["rev-parse", "HEAD"], ["rev-parse", "HEAD^{tree}"],
                      ["status", "--porcelain", "--untracked-files=all"]):
        values.append(h.command(["git", "-C", str(root), *arguments],
                                timeout=h.remaining_time(deadline), maximum=65536))
    h.require(values[0].decode("ascii").strip() == spec.source_sha
              and values[1].decode("ascii").strip() == spec.source_tree and not values[2],
              "Actual selected source differs")
    h.remaining_time(deadline)
    h.require(regular_hash(dll) == spec.executable_sha256, "Actual selected build differs")
    h.remaining_time(deadline)
