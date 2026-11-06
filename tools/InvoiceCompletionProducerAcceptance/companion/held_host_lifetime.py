"""Additive lifetime owner for original normal/front acquisition seams.

No import-time acquisition. Evidence belongs to retained objects, never supplied
receipts. Direct-child scope excludes escaped descendants and eight acceptance.
"""
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import signal
import stat
import subprocess
import threading
import time

import held_linux_exit as original_exit
import owned_front_host as front_source
import owned_normal_hosts as normal_source

SOURCE_SHA256 = {
    'held_linux_exit': 'e7c809d0a1a9c8e8673ed5ef299dd530774d68a48196a739a90f0eaaf5e11515',
    'owned_normal_hosts': 'e5fee39ac7ced3b1304698c734abfd192660a29e94e7e1dcb366b34f15205988',
    'owned_front_host': '7b06c9abce7aed3cca7cc90944a75b2d01d4c91ec95b1286486444a29f04392b',
}


def verify_source_dependencies():
    # Parent qualifies immutable imports before loading; recheck exact association.
    for module in (original_exit, normal_source, front_source):
        path = Path(module.__file__)
        with path.open('rb') as stream:
            source = stream.read(65537)
        require(len(source) <= 65536 and hashlib.sha256(source).hexdigest() == SOURCE_SHA256[module.__name__])


class LifetimeRefused(RuntimeError):
    def __init__(self):
        super().__init__('Original held-host lifetime refused')


def require(value):
    if not value:
        raise LifetimeRefused()


@dataclass
class Pidfd:
    fd: int = -1
    closed: bool = False
    close_uncertain: bool = False


@dataclass(slots=True)
class Child:
    owner: object
    role: str
    process: object
    identity: object = None
    pidfd: object = None
    descriptors: object = None
    listener: object = None
    reaped: bool = False
    descriptor_scope: str = ''
    failed: bool = False
    birth_attempted: bool = False
    listener_absence_verified: bool = False


class HeldPopen(subprocess.Popen):
    """Existing live checks must not reap or accept Popen's cached exit status."""
    lifetime = None
    def poll(self):
        require(self.lifetime is not None)
        if self.returncode is not None:
            self.lifetime.failed = True
            raise LifetimeRefused()
        observation = self.lifetime.observe_process(self)
        if observation.settled:
            self.lifetime.failed = True
            raise LifetimeRefused()
        return None

    def _internal_poll(self, *args, **kwargs):
        if self.returncode is not None:
            return self.returncode  # Destructor only; public poll still refuses cache.
        return self.poll()

    def wait(self, *args, **kwargs):
        raise LifetimeRefused()  # Only this acquisition owner's raw waitpid may reap.


class LinuxLifetime:
    actual = True
    def now(self): return time.monotonic()
    def generation(self, pid): return original_exit.generation(pid)
    def pidfd(self, pid):
        handle = Pidfd()  # Wrapper allocated before descriptor acquisition.
        handle.fd = os.pidfd_open(pid, 0)
        return handle
    def mask(self): return signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
    def restore(self, previous): signal.pthread_sigmask(signal.SIG_SETMASK, previous)
    def null_descriptor(self):
        info = os.stat('/dev/null')
        return (info.st_rdev, stat.S_IFCHR)
    def verify(self, handle, identity):
        require(not handle.closed and not handle.close_uncertain)
        original_exit.verify_pidfd(handle.fd, identity.pid)
        require(self.generation(identity.pid)[:4] == (
            identity.start_ticks, identity.parent_pid, identity.process_group, identity.session))
    def observe(self, child):
        return original_exit.observe_original(child.process, child.pidfd, child.identity)
    def spawn(self, arguments, options): return HeldPopen(arguments, **options)
    def descriptors(self, pid):
        result = []
        for number in (0, 1, 2):
            path = '/proc/' + str(pid) + '/fd/' + str(number)
            info = os.stat(path)
            result.append((os.readlink(path), info.st_dev, info.st_ino, info.st_rdev, stat.S_IFMT(info.st_mode)))
        return tuple(result)
    def bootstrap(self, pipe):
        require(not pipe.closed and type(pipe.read_fd) is int)
        info = os.fstat(pipe.read_fd)
        require(stat.S_ISFIFO(info.st_mode))
        return (pipe.read_fd, os.readlink('/proc/self/fd/' + str(pipe.read_fd)), info.st_dev, info.st_ino)
    def bootstrap_child(self, process, binding):
        require(os.readlink('/proc/' + str(process.pid) + '/fd/' + str(binding[0])) == binding[1])
    def census(self, parent_pid, deadline):
        rows = {}
        with os.scandir('/proc') as entries:
            for count, entry in enumerate(entries, 1):
                require(count <= 32768 and self.now() < deadline)
                if not entry.name.isdecimal(): continue
                try: identity = self.generation(int(entry.name))
                except FileNotFoundError: continue
                rows[int(entry.name)] = identity
        # Never kill an observed descendant. Unknown descendants quarantine scope.
        selected = {}
        parents = {parent_pid}
        while parents:
            next_parents = set()
            for pid, identity in rows.items():
                if identity[1] in parents and pid not in selected:
                    selected[pid] = identity
                    next_parents.add(pid)
            parents = next_parents
        return selected
    def terminate(self, child, force):
        self.verify(child.pidfd, child.identity)
        os.kill(child.identity.pid, signal.SIGKILL if force else signal.SIGTERM)
    def reap(self, child):
        self.verify(child.pidfd, child.identity)
        observed = self.observe(child)
        require(observed.settled)
        pid, status = os.waitpid(child.identity.pid, os.WNOHANG)
        require(pid == child.identity.pid)
        child.process.returncode = os.waitstatus_to_exitcode(status)
        require(child.process.returncode == observed.exit_code)
        child.reaped = True
    def listener_absent(self, inode):
        require(type(inode) is str and inode.isdecimal())
        for path in ('/proc/net/tcp', '/proc/net/tcp6'):
            raw = original_exit.bounded_file(path, 2 * 1024**2).decode('ascii')
            for row in raw.splitlines()[1:]:
                fields = row.split()
                require(len(fields) >= 10)
                require(not (fields[3] == '0A' and fields[9] == inode))
    def close(self, handle):
        require(not handle.close_uncertain)
        if not handle.closed:
            try: os.close(handle.fd); handle.closed = True
            except BaseException: handle.close_uncertain = True; raise
    def pause(self): time.sleep(.01)


class HostLifetime:
    """Bind original owner objects and retain sticky refusal through physical retry."""
    def __init__(self, driver=None, cleanup_seconds=25):
        require(type(cleanup_seconds) in (int, float) and 0 < cleanup_seconds <= 25)
        self.driver = driver or LinuxLifetime()
        self.budget = cleanup_seconds
        self.children = []
        self.parent = None
        self.parent_pidfd = None
        self.baseline = None
        self.parent_admitted = False
        self.normal = None
        self.front = None
        self.pipe = None
        self.bootstrap_binding = None
        self.bootstrap_closed = False
        self.bootstrap_close_uncertain = False
        self.cleanup_failures = ()
        self.failed = False
        self.closed = False
        self.birth_uncertain = False
        self._gate = threading.Lock()

    @contextmanager
    def fence(self):
        if threading.current_thread() is not threading.main_thread():
            self.failed = True
            raise LifetimeRefused()
        previous = self.driver.mask()
        acquired = False
        try:
            acquired = self._gate.acquire(blocking=False)
            if not acquired: self.failed = True
            require(acquired)
            yield
        finally:
            if acquired: self._gate.release()
            self.driver.restore(previous)

    def admit_parent(self):
        with self.fence():
            try:
                require(self.parent is None and not self.closed and not self.failed)
                if type(self.driver) is LinuxLifetime: verify_source_dependencies()
                identity = self.driver.generation(os.getpid())
                self.parent = original_exit.OriginalIdentity(os.getpid(), *identity[:4])
                self.parent_pidfd = self.driver.pidfd(os.getpid())
                self.driver.verify(self.parent_pidfd, self.parent)
                self.baseline = self.driver.census(os.getpid(), self.driver.now() + 2)
                self.driver.verify(self.parent_pidfd, self.parent)
                self.parent_admitted = True
            except BaseException:
                # Retain every acquired original handle. Cleanup may close the
                # parent pidfd without inventing a replacement baseline census.
                self.failed = True
                raise LifetimeRefused() from None

    def bind_normal(self, owner):
        try:
            require(type(owner) is LifetimeNormalHosts and self.parent_admitted
                    and not self.failed and not self.closed and self.normal is None
                    and owner.lifetime is self and not owner.owned)
            self.normal = owner
        except BaseException:
            self.failed = True
            raise LifetimeRefused() from None

    def bind_front(self, owner):
        try:
            require(type(owner) is LifetimeFrontHost and self.parent_admitted
                    and not self.failed and not self.closed and self.front is None
                    and owner.lifetime is self and owner.process is None)
            self.front, self.pipe = owner, owner.pipe
            self.bootstrap_binding = self.driver.bootstrap(self.pipe)
        except BaseException:
            self.failed = True
            raise LifetimeRefused() from None

    def acquire(self, owner, role, arguments, options, descriptor_scope):
        with self.fence():
            try:
                require(self.parent_admitted and not self.failed and not self.closed)
                require((owner is self.normal and role in normal_source.OWNERS) or (owner is self.front and role == 'Front'))
                require(not any(row.role == role for row in self.children))
                self.driver.verify(self.parent_pidfd, self.parent)
                inherited = self.driver.descriptors(os.getpid())
            except BaseException:
                self.failed = True
                raise LifetimeRefused() from None
            row = None
            try:
                # Allocate and retain the slotted custody record BEFORE calling
                # the birth API. Returned process custody must not depend on a
                # later Child allocation or growing the ledger list.
                row = Child(owner, role, None, descriptor_scope=descriptor_scope)
                self.children.append(row)
                row.birth_attempted = True
                row.process = self.driver.spawn(arguments, options)
                process = row.process
                process.lifetime = self
                birth = self.driver.generation(process.pid)
                row.identity = original_exit.OriginalIdentity(process.pid, *birth[:4])
                require(birth[1] == self.parent.pid and birth[2:4] == (self.parent.process_group, self.parent.session))
                row.pidfd = self.driver.pidfd(process.pid)
                self.driver.verify(row.pidfd, row.identity)
                row.descriptors = self.driver.descriptors(process.pid)
                expected_null = self.driver.null_descriptor()
                require(row.descriptors[0][3:] == expected_null)
                if descriptor_scope == 'inherited-output':
                    require(row.descriptors[1:] == inherited[1:])
                else:
                    require(descriptor_scope == 'devnull-output' and all(item[3:] == expected_null for item in row.descriptors[1:]))
                    require(self.driver.bootstrap(self.pipe) == self.bootstrap_binding)
                    self.driver.bootstrap_child(process, self.bootstrap_binding)
                require(not self.driver.observe(row).settled)
                return process
            except BaseException:
                self.failed = True
                if row is not None:
                    row.failed = True
                    if row.birth_attempted and row.process is None:
                        self.birth_uncertain = True
                raise LifetimeRefused() from None

    def _row(self, process):
        rows = [row for row in self.children if row.process is process]
        require(len(rows) == 1)
        return rows[0]

    def _observe(self, process):
        require(not self.failed and not self.closed)
        row = self._row(process)
        require(not row.reaped and process.returncode is None)
        self.driver.verify(self.parent_pidfd, self.parent)
        require(self.driver.descriptors(process.pid) == row.descriptors)
        return self.driver.observe(row)

    def observe_process(self, process):
        with self.fence():
            try: return self._observe(process)
            except BaseException:
                self.failed = True
                raise LifetimeRefused() from None

    def observe_original(self, owner):
        with self.fence():
            try:
                require(owner is self.normal or owner is self.front)
                rows = [row for row in self.children if row.owner is owner]
                require(rows)
                for row in rows:
                    require(not self._observe(row.process).settled)
                # Live original capability observation only, no supplied receipt.
                return None
            except BaseException:
                self.failed = True
                raise LifetimeRefused() from None

    def bind_listener(self, owner, process, inode):
        with self.fence():
            try:
                row = self._row(process)
                require(row.owner is owner and not row.reaped and type(inode) is str and inode.isdecimal())
                require(row.listener is None or row.listener == inode)
                row.listener = inode
            except BaseException:
                self.failed = True
                raise LifetimeRefused() from None

    def close(self):
        with self.fence():
            if self.closed: return
            if not self.parent_admitted:
                # No owner can bind or child can be acquired before complete
                # parent admission. A failed initial census is not a cleanup
                # prerequisite for this exact parent descriptor, nor may a later
                # census be promoted to the missing original baseline.
                require(not self.children and self.normal is None and self.front is None)
                try:
                    if self.parent_pidfd is not None:
                        self.driver.close(self.parent_pidfd)
                    self.closed = True
                except BaseException:
                    self.failed = True
                    raise LifetimeRefused() from None
                return
            end = self.driver.now() + self.budget
            failures = []
            # Direct exact children only. Unknown descendants are never signal targets.
            try:
                census = self.driver.census(self.parent.pid, end)
                allowed = dict(self.baseline)
                allowed.update({r.identity.pid: self.driver.generation(r.identity.pid) for r in self.children if r.identity is not None and not r.reaped})
                require(all(pid in allowed and identity[:4] == allowed[pid][:4] for pid, identity in census.items()))
            except BaseException: failures.append('descendant-census')
            for row in reversed(self.children):
                if not row.birth_attempted:
                    continue  # A retained pre-birth slot owns no child to reap.
                try:
                    require(row.identity is not None and row.pidfd is not None)
                    if not row.reaped:
                        observation = self.driver.observe(row)
                        if not observation.settled:
                            self.driver.terminate(row, False)
                            grace = min(end, self.driver.now() + 1)
                            while not self.driver.observe(row).settled and self.driver.now() < grace:
                                self.driver.pause()
                            if not self.driver.observe(row).settled: self.driver.terminate(row, True)
                        while not self.driver.observe(row).settled:
                            require(self.driver.now() < end)
                            self.driver.pause()
                        self.driver.reap(row)
                    require(row.reaped and row.process.returncode is not None)
                    try:
                        require(row.listener is not None)
                        self.driver.listener_absent(row.listener)
                        row.listener_absence_verified = True
                    finally:
                        self.driver.close(row.pidfd)
                except BaseException:
                    row.failed = True
                    failures.append(row.role)
            settled = not self.birth_uncertain and all(
                not row.birth_attempted or row.reaped for row in self.children)
            if settled:
                try:
                    after = self.driver.census(self.parent.pid, end)
                    require(all(pid in self.baseline and identity[:4] == self.baseline[pid][:4] for pid, identity in after.items()))
                except BaseException: failures.append('final-census')
                # Exact independent descriptors are disposed after original
                # child settlement even when listener evidence is incomplete.
                # Failure of one disposal never skips another acquired handle.
                if self.pipe is not None and not self.bootstrap_closed:
                    try:
                        require(not self.bootstrap_close_uncertain)
                        require(self.driver.bootstrap(self.pipe) == self.bootstrap_binding)
                        try: self.pipe.close()
                        except BaseException:
                            self.bootstrap_close_uncertain = True
                            raise
                        require(self.pipe.closed and self.pipe.read_fd is None and self.pipe.write_fd is None)
                        self.bootstrap_closed = True
                    except BaseException: failures.append('bootstrap-close')
                try:
                    self.driver.close(self.parent_pidfd)
                except BaseException: failures.append('parent-close')
            self.cleanup_failures = tuple(failures)
            self.closed = not failures and not self.birth_uncertain
            self.failed = self.failed or bool(failures) or self.birth_uncertain
            if not self.closed: raise LifetimeRefused()

    def release_observation(self):
        require(self.closed and not self.failed and not self.birth_uncertain and self.children
                and all(row.birth_attempted and row.reaped and row.pidfd.closed
                        and row.listener_absence_verified and not row.failed for row in self.children))
        return {'ActualLinuxBackendUsed': type(self.driver) is LinuxLifetime,
                'OriginalDirectChildrenReaped': len(self.children),
                'OriginalPidfdsClosed': True, 'OwnedListenerInodesAbsent': True,
                'CreatedHostOutputReaders': 0, 'EscapedDescendantsAccepted': False,
                'GenuineEightHostFinancialAccepted': False}

    def assert_backend_release(self):
        """Real direct-child fence only; caller still owns broader descendant contract."""
        require(type(self.driver) is LinuxLifetime)
        self.release_observation()
        return None


class LifetimeNormalHosts(normal_source.OwnedNormalHosts):
    def __init__(self, lifetime, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.lifetime = lifetime
        lifetime.bind_normal(self)

    def spawn_owned(self, spec, environment):
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
        try:
            process = self.lifetime.acquire(self, spec.owner, [self.dotnet, spec.executable_dll],
                {'cwd': str(Path(spec.executable_dll).parent), 'env': environment,
                 'stdin': subprocess.DEVNULL, 'start_new_session': False}, 'inherited-output')
            row = [spec, process, self.lifetime._row(process).identity.start_ticks,
                   normal_source.datetime.now(normal_source.timezone.utc).isoformat()]
            self.owned.append(row)
            return row
        finally: signal.pthread_sigmask(signal.SIG_SETMASK, previous)

    def start(self, owner):
        result = super().start(owner)
        row = next(row for row in self.owned if row[0].owner == owner)
        self.lifetime.bind_listener(self, row[1], result['listenerInode'])
        return result

    def close(self):
        if self.closed: return
        if self.timer_owned and not self.timer_paused:
            signal.setitimer(signal.ITIMER_REAL, 0)
            self.timer_paused = True
        try:
            self.lifetime.close()
            self.closed = True
            self.cleanup_failures = []
            self.release_timer()
        except BaseException:
            self.cleanup_failures = [('OriginalLifetime', 'retained')]
            if self.timer_owned:
                signal.setitimer(signal.ITIMER_REAL, 1)
                self.timer_paused = False
            raise


class LifetimeFrontHost(front_source.OwnedFrontHost):
    def __init__(self, lifetime, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.lifetime = lifetime
        lifetime.bind_front(self)

    def acquire_child(self, arguments, **options):
        return self.lifetime.acquire(self, 'Front', arguments, options, 'devnull-output')

    def observe_listener(self):
        result = super().observe_listener()
        self.lifetime.bind_listener(self, self.process, result['listenerInode'])
        return result

    def close(self):
        if self.lifetime.normal is not None:
            self.lifetime.normal.close()
        else:
            self.lifetime.close()
