"""POSIX transport for already-owned collector/producer pipes; no resource launcher.

Acquisition owners retain processes, source/descriptor identity, cancellation and
cleanup authority. This transport never admits receipts or declares acceptance.
"""
import math
import os
import selectors
import time


class TransportRejected(Exception):
    pass


def require(value):
    if not value:
        raise TransportRejected('Held parent transport refused')


def safe_setup(method):
    def guarded(self, *args, **kwargs):
        try:
            return method(self, *args, **kwargs)
        except BaseException:
            self.failed = True
            raise TransportRejected('Held parent setup refused') from None
    return guarded


class LineBuffer:
    def __init__(self, total_cap=65536):
        require(type(total_cap) is int and 4096 <= total_cap <= 1048576)
        self.pending = bytearray()
        self.total = 0
        self.cap = total_cap
        self.eof = False

    def feed(self, raw):
        require(type(raw) is bytes and not self.eof)
        self.total += len(raw)
        require(self.total <= self.cap and b'\r' not in raw)
        self.pending.extend(raw)
        # Every buffered complete or partial line has the exact grammar ceiling.
        require(all(len(part) <= 4095 for part in self.pending.split(b'\n')))

    def line(self):
        end = self.pending.find(b'\n')
        if end < 0:
            return None
        raw = bytes(self.pending[:end + 1])
        del self.pending[:end + 1]
        return raw

    def finish(self):
        require(not self.pending)
        self.eof = True


class OpaqueDrain:
    """Keep stderr moving without retaining or publishing its private bytes."""
    @safe_setup
    def __init__(self, stream, cancelled, expires):
        require(os.name == 'posix')
        self.stream, self.fd = stream, stream.fileno()
        require(type(self.fd) is int and self.fd >= 0 and callable(getattr(cancelled, 'is_set', None))
                and type(expires) in (int, float) and math.isfinite(expires) and time.monotonic() < expires)
        self.cancelled, self.expires, self.failed = cancelled, expires, False
        self.count, self.eof = 0, False
        os.set_blocking(self.fd, False)

    def consume(self):
        try:
            require(not self.failed and not self.eof and not self.cancelled.is_set()
                    and time.monotonic() < self.expires)
            try:
                raw = os.read(self.fd, 4096)
            except BlockingIOError:
                return
            self.count += len(raw)
            require(self.count <= 65536)
            self.eof = not raw
        except BaseException:
            self.failed = True
            raise TransportRejected('Held parent stderr drain refused') from None


class HeldPipe:
    @safe_setup
    def __init__(self, stream, cancelled, expires, stderr_drains=()):
        require(os.name == 'posix' and callable(getattr(cancelled, 'is_set', None)))
        require(type(expires) in (int, float) and math.isfinite(expires)
                and time.monotonic() < expires)
        self.stream = stream  # Retain the actual owner's handle; never reopen by path.
        self.fd = stream.fileno()
        require(type(self.fd) is int and self.fd >= 0)
        self.cancelled, self.expires = cancelled, expires
        require(type(stderr_drains) is tuple and all(type(d) is OpaqueDrain for d in stderr_drains)
                and len({self.fd, *(d.fd for d in stderr_drains)}) == 1 + len(stderr_drains)
                and all(d.cancelled is cancelled and d.expires == expires and not d.failed for d in stderr_drains))
        self.stderr_drains = stderr_drains
        self.failed = False
        os.set_blocking(self.fd, False)
        self.buffer = LineBuffer()

    def _remaining(self, deadline):
        require(not self.failed and not self.cancelled.is_set())
        require(type(deadline) in (int, float) and math.isfinite(deadline))
        left = min(deadline, self.expires) - time.monotonic()
        require(left > 0)
        return left

    def _wait(self, event, deadline, forbidden=()):
        with selectors.DefaultSelector() as selector:
            selector.register(self.fd, event)
            for fd in forbidden:
                selector.register(fd, selectors.EVENT_READ, 'forbidden')
            for drain in self.stderr_drains:
                if not drain.eof:
                    selector.register(drain.fd, selectors.EVENT_READ, drain)
            while True:
                ready = selector.select(min(0.05, self._remaining(deadline)))
                target_ready = False
                for key, _ in ready:
                    if key.fd == self.fd:
                        target_ready = True
                    elif key.data == 'forbidden':
                        require(False)  # Includes unsolicited command or early EOF.
                    else:
                        key.data.consume()
                        if key.data.eof:
                            selector.unregister(key.fd)
                self._remaining(deadline)
                if target_ready:
                    return

    def read_line(self, deadline, forbidden=()):
        try:
            while True:
                self._remaining(deadline)
                if forbidden:
                    with selectors.DefaultSelector() as guard:
                        for fd in forbidden:
                            guard.register(fd, selectors.EVENT_READ)
                        require(not guard.select(0))
                raw = self.buffer.line()
                if raw is not None:
                    return raw
                self._wait(selectors.EVENT_READ, deadline, forbidden)
                try:
                    chunk = os.read(self.fd, 4096)
                except BlockingIOError:
                    continue
                require(chunk)  # EOF before a required message is a refusal.
                self.buffer.feed(chunk)
        except BaseException:
            self.failed = True
            raise TransportRejected('Held parent transport refused') from None

    def write_line(self, raw, deadline):
        try:
            require(type(raw) is bytes and 0 < len(raw) <= 4096
                    and raw.endswith(b'\n') and raw.count(b'\n') == 1 and b'\r' not in raw)
            offset = 0
            while offset < len(raw):
                self._wait(selectors.EVENT_WRITE, deadline)
                try:
                    count = os.write(self.fd, raw[offset:])
                except BlockingIOError:
                    continue
                require(count > 0)
                offset += count
        except BaseException:
            self.failed = True
            raise TransportRejected('Held parent transport refused') from None


class PhaseRelay:
    """One channel reservation. No descriptor ownership or READY inferred here."""
    @safe_setup
    def __init__(self, coordinator, producer_out, producer_in, collector_out, collector_in):
        pipes = (producer_out, producer_in, collector_out, collector_in)
        require(all(type(pipe) is HeldPipe for pipe in pipes)
                and len({pipe.fd for pipe in pipes}) == 4
                and all(not pipe.failed for pipe in pipes)
                and all(pipe.cancelled is pipes[0].cancelled and pipe.expires == pipes[0].expires for pipe in pipes))
        drains = pipes[0].stderr_drains
        require(len(drains) == 2 and all(pipe.stderr_drains == drains for pipe in pipes)
                and all(d.cancelled is pipes[0].cancelled and d.expires == pipes[0].expires and not d.failed for d in drains)
                and len({*(p.fd for p in pipes), *(d.fd for d in drains)}) == 6)
        self.coordinator = coordinator
        self.producer_out, self.producer_in = producer_out, producer_in
        self.collector_out, self.collector_in = collector_out, collector_in
        self.failed = False

    def _quarantine(self):
        self.failed = True
        for pipe in (self.producer_out, self.producer_in, self.collector_out, self.collector_in):
            pipe.failed = True
            for drain in pipe.stderr_drains:
                drain.failed = True

    def sessions_started(self, deadline):
        try:
            require(not self.failed)
            deadline = min(deadline, time.monotonic() + 15)
            raw = self.collector_out.read_line(deadline)
            # The coordinator validates actual collector bytes; no synthesized start.
            self.coordinator.collector_started(raw, time.monotonic())
        except BaseException:
            self._quarantine()
            raise TransportRejected('Held parent phase relay refused') from None


    def phase(self, deadline):
        try:
            require(not self.failed)
            deadline = min(deadline, time.monotonic() + 15)
            command = self.coordinator.producer_phase(
                self.producer_out.read_line(deadline), time.monotonic())
            self.collector_in.write_line(command, deadline)
            if command != b'stop\n':
                require(not self.producer_out.buffer.pending)
                held = self.coordinator.collector_held(
                    self.collector_out.read_line(deadline, forbidden=(self.producer_out.fd,)), time.monotonic())
                self.producer_in.write_line(held, deadline)
            # Stop needs independently admitted actual receipt, exit and drained readers.
        except BaseException:
            self._quarantine()
            raise TransportRejected('Held parent phase relay refused') from None


def drain_owned_readers(output_pipes, stderr_drains, deadline):
    """Final BOTH-child post-shutdown reader drain; no exit/cleanup admission.

    Cannot be used before producer stopped acknowledgement: both stderr EOFs
    are required. Collector pre-ack lifetime/reader operations remain unavailable
    here and must belong to its independently qualified acquisition owner.
    """
    try:
        require(type(output_pipes) is tuple and len(output_pipes) == 2
                and all(type(p) is HeldPipe for p in output_pipes)
                and type(stderr_drains) is tuple and len(stderr_drains) == 2
                and all(type(d) is OpaqueDrain for d in stderr_drains))
        cancelled, expires = output_pipes[0].cancelled, output_pipes[0].expires
        channels = (*output_pipes, *stderr_drains)
        require(len({c.fd for c in channels}) == len(channels)
                and all(c.cancelled is cancelled and c.expires == expires and not c.failed for c in channels)
                and all(p.stderr_drains == stderr_drains for p in output_pipes)
                and not cancelled.is_set() and time.monotonic() < expires
                and type(deadline) in (int, float) and math.isfinite(deadline))
        deadline = min(deadline, time.monotonic() + 15)
        with selectors.DefaultSelector() as selector:
            for channel in channels:
                if type(channel) is HeldPipe:
                    require(not channel.failed and not channel.buffer.pending)
                    selector.register(channel.fd, selectors.EVENT_READ, channel)
                elif not channel.eof:
                    selector.register(channel.fd, selectors.EVENT_READ, channel)
            while selector.get_map():
                require(not cancelled.is_set() and math.isfinite(expires) and math.isfinite(deadline))
                left = min(expires, deadline) - time.monotonic()
                require(left > 0)
                for key, _ in selector.select(min(0.05, left)):
                    channel = key.data
                    if type(channel) is OpaqueDrain:
                        channel.consume()
                        if channel.eof:
                            selector.unregister(channel.fd)
                    else:
                        try:
                            raw = os.read(channel.fd, 4096)
                        except BlockingIOError:
                            continue
                        # No unconsumed collector or producer stdout is legal after
                        # ordered stop. It cannot be silently discarded into a pass.
                        require(not raw)
                        channel.buffer.finish()
                        selector.unregister(channel.fd)
        # EOF is no exit proof. No actual owning settled-exit API is admitted here.
        return None
    except BaseException:
        for collection in (output_pipes, stderr_drains):
            if type(collection) is tuple:
                for channel in collection:
                    if type(channel) in (HeldPipe, OpaqueDrain):
                        channel.failed = True
        raise TransportRejected('Held parent reader drain refused') from None


def observe_owned_settled_exit():
    """Explicitly unavailable until a qualified owning kernel lifetime API exists."""
    raise TransportRejected('Owning settled-exit integration unavailable')

