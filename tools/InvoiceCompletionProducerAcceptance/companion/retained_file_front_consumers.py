"""Proposed non-owning handoff to the existing original global host lifetime.

The immutable caller must qualify this module and original dependency bytes before
import. This does not spawn, construct host owners, create readers, grant authority
or produce a File/business receipt. Bridge integration is a separate reviewed delta.
"""
import held_host_lifetime as original
from datetime import datetime, timezone


class ConsumerHandoffRefused(ValueError):
    def __init__(self):
        super().__init__('Original File/front consumer handoff refused')


def require(value):
    if not value:
        raise ConsumerHandoffRefused()


class RetainedFileFrontConsumers:
    """Retain before birth; only global original settlement permits backend release.

    Normal children inherit output; front output goes to DEVNULL. No reader tasks
    or stream settlement claims are introduced. This object remains retained on
    every failed transition, including a failed original close/release attempt.
    """
    __slots__ = ('lifetime', 'driver', 'normal', 'front', 'context', 'file_row', 'front_row', 'admitted',
                 'failed', 'released')

    def __init__(self, lifetime, normal, front):
        self.lifetime, self.normal, self.front = lifetime, normal, front
        self.driver = None
        self.context = None
        self.file_row = self.front_row = None
        self.admitted = self.failed = self.released = False
        try:
            require(type(lifetime) is original.HostLifetime
                    and type(lifetime.driver) is original.LinuxLifetime)
            self.driver = lifetime.driver
            require(type(normal) is original.LifetimeNormalHosts
                    and type(front) is original.LifetimeFrontHost)
            require(lifetime.normal is normal and lifetime.front is front
                    and normal.lifetime is lifetime and front.lifetime is lifetime)
            require(normal.context is front.context)
            self.context = normal.context
            require(lifetime.parent_admitted and not lifetime.failed and not lifetime.closed
                    and not lifetime.birth_uncertain)
            require(front.process is None and not any(row.role in ('File', 'Front')
                    for row in lifetime.children))
            require(lifetime.pipe is front.pipe and not lifetime.bootstrap_closed)
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None

    def _custody(self):
        require(self.lifetime.driver is self.driver and type(self.driver) is original.LinuxLifetime)
        require(self.lifetime.normal is self.normal and self.lifetime.front is self.front
                and self.normal.lifetime is self.lifetime and self.front.lifetime is self.lifetime
                and self.lifetime.pipe is self.front.pipe
                and self.normal.context is self.context and self.front.context is self.context)

    def _live_context(self):
        self._custody()
        require(not self.failed and not self.released and self.lifetime.parent_admitted
                and not self.lifetime.failed and not self.lifetime.closed
                and not self.lifetime.birth_uncertain)
        self.context.validate(self.normal.environment, datetime.now(timezone.utc))
        self.context.validate(self.front.launcher_environment, datetime.now(timezone.utc))

    def _no_file_birth(self):
        require(not any(row.role == 'File' for row in self.lifetime.children)
                and not any(row[0].owner == 'File' for row in self.normal.owned))

    def observe_prebirth(self):
        """Source-owned parent custody before the first File/front birth."""
        try:
            self._live_context()
            require(not self.admitted and self.file_row is None and self.front_row is None
                    and self.front.process is None)
            self._no_file_birth()
            require(not any(row.role == 'Front' for row in self.lifetime.children))
            require(not self.front.pipe.closed and not self.front.pipe.sealed
                    and self.front.pipe.read_fd is not None and self.front.pipe.write_fd is not None
                    and not self.lifetime.bootstrap_closed)
            require(self.driver.bootstrap(self.front.pipe) == self.lifetime.bootstrap_binding)
            self.driver.verify(self.lifetime.parent_pidfd, self.lifetime.parent)
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None

    def observe_prepared_front(self):
        """Original observed Front only; no File birth, signing or admission yet."""
        try:
            self._live_context()
            require(not self.admitted and self.file_row is None)
            self._no_file_birth()
            rows = [row for row in self.lifetime.children
                    if row.owner is self.front and row.role == 'Front']
            require(len(rows) == 1)
            if self.front_row is None:
                self.front_row = rows[0]  # Retain BEFORE original observation.
            require(self.front_row is rows[0])
            row = self.front_row
            require(row.birth_attempted and not row.failed and not row.reaped
                    and row.identity is not None and row.pidfd is not None
                    and not row.pidfd.closed and not row.pidfd.close_uncertain
                    and row.descriptor_scope == 'devnull-output'
                    and type(row.process) is original.HeldPopen
                    and row.process is self.front.process and row.process.lifetime is self.lifetime
                    and row.process.stdin is None and row.process.stdout is None and row.process.stderr is None)
            require(not self.front.pipe.closed and not self.front.pipe.sealed
                    and self.front.pipe.read_fd is not None and self.front.pipe.write_fd is not None
                    and not self.lifetime.bootstrap_closed)
            require(self.driver.bootstrap(self.front.pipe) == self.lifetime.bootstrap_binding)
            self.driver.bootstrap_child(row.process, self.lifetime.bootstrap_binding)
            self.lifetime.observe_original(self.front)
            # Exact original API re-observes profile/source/env/listener and binds
            # the SAME original listener. Supplied metadata cannot replace it.
            self.front.observe_listener()
            require(row.listener is not None)
            self.lifetime.observe_original(self.front)
            require(any(item is row for item in self.lifetime.children)
                    and row.process is self.front.process and not row.failed and not row.reaped)
            require(self.driver.bootstrap(self.front.pipe) == self.lifetime.bootstrap_binding)
            self.driver.bootstrap_child(row.process, self.lifetime.bootstrap_binding)
            self._live_context()
            self._no_file_birth()
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None

    def admit_live(self):
        """Call after both ORIGINAL start/listener admissions, before HTTP use."""
        try:
            require(not self.failed and not self.released and not self.admitted)
            self._live_context()
            files = [row for row in self.lifetime.children
                     if row.owner is self.normal and row.role == 'File']
            fronts = [row for row in self.lifetime.children
                      if row.owner is self.front and row.role == 'Front']
            require(len(files) == len(fronts) == 1)
            # Retain exact original Child objects before any live observation.
            self.file_row = files[0]
            if self.front_row is None:
                self.front_row = fronts[0]
            require(self.front_row is fronts[0])
            self._rows()
            self.lifetime.observe_original(self.normal)
            self.lifetime.observe_original(self.front)
            self._live_context()
            self.admitted = True
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None

    def _rows(self):
        require(self.file_row is not None and self.front_row is not None)
        require(any(row is self.file_row for row in self.lifetime.children)
                and any(row is self.front_row for row in self.lifetime.children))
        require(self.file_row.owner is self.normal and self.file_row.role == 'File'
                and self.front_row.owner is self.front and self.front_row.role == 'Front')
        require(self.front_row.process is self.front.process)
        require(self.front.pipe.sealed and self.front.pipe.write_fd is None
                and not self.front.pipe.closed)
        require(not self.lifetime.bootstrap_closed and not self.lifetime.bootstrap_close_uncertain
                and self.driver.bootstrap(self.front.pipe) == self.lifetime.bootstrap_binding)
        self.driver.bootstrap_child(self.front_row.process, self.lifetime.bootstrap_binding)
        require(self.file_row.descriptor_scope == 'inherited-output'
                and self.front_row.descriptor_scope == 'devnull-output')
        for row in (self.file_row, self.front_row):
            require(row.birth_attempted and row.identity is not None and row.pidfd is not None
                    and row.listener is not None and not row.failed and not row.reaped
                    and type(row.process) is original.HeldPopen
                    and row.process.lifetime is self.lifetime
                    and row.process.stdin is None and row.process.stdout is None
                    and row.process.stderr is None)
        # Original normal publication must refer to the SAME File process.
        require(sum(spec.owner == 'File' and process is self.file_row.process
                    for spec, process, *_ in self.normal.owned) == 1)

    def observe_before_use(self):
        try:
            require(self.admitted and not self.failed and not self.released)
            self._live_context()
            self._rows()
            # Original owner uses pidfd/generation/non-reaping waitid observation.
            # No generic Popen.poll(), cached returncode or supplied receipt.
            self.lifetime.observe_original(self.normal)
            self.lifetime.observe_original(self.front)
            self._live_context()
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None

    def close_original_and_assert_backend_release(self):
        """Global fence, including other normal children; never per-child disposal."""
        try:
            self._custody()
            # Attempt physical original cleanup even after our own earlier refusal.
            # LifetimeFrontHost.close delegates to normal.close/lifetime.close;
            # normal.close owns the original expiry timer restoration.
            self.front.close()
            self.lifetime.assert_backend_release()
            require(self.lifetime.closed and not self.lifetime.failed
                    and self.lifetime.bootstrap_closed and self.front.pipe.closed
                    and self.front.pipe.read_fd is None and self.front.pipe.write_fd is None)
            require(not self.failed and self.admitted)
            self.released = True
        except BaseException:
            self.failed = True
            raise ConsumerHandoffRefused() from None
