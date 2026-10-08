"""Borrow held phase owners; actual collector EOF/settled exit precedes stop ACK.

Caller holds both acquisition owners' exclusive lifetime fences throughout.
Original Popen, pidfd, birth identity and streams must never be newly adopted.
"""
import hashlib
import math
import os
from pathlib import Path
import re
import selectors
import time
import held_parent_io as transport
import held_linux_exit as kernel_exit
import coordinator


DEPENDENCY_SHA256 = {
    'held_parent_io': 'b1f65e46aabafa312500e987e4dc1fbe06034e663cf2d04001638a09b3fe84e7',
    'held_linux_exit': 'e7c809d0a1a9c8e8673ed5ef299dd530774d68a48196a739a90f0eaaf5e11515',
    'coordinator': '6dd6302bd82fc249767ad24b32e5555afabf4ae2aec48a465d385e85d613fed4',
}


class PreackRefused(RuntimeError):
    def __init__(self):
        super().__init__('Owned collector pre-ack refused')


def require(value):
    if not value:
        raise PreackRefused()


def verify_dependencies():
    # Independent immutable source qualification must occur BEFORE these imports.
    # This recheck binds exact byte association, not hostile mutable Python globals.
    for module in (transport, kernel_exit, coordinator):
        path = Path(module.__file__)
        with path.open('rb') as stream:
            raw = stream.read(1048577)
        require(len(raw) <= 1048576 and hashlib.sha256(raw).hexdigest() == DEPENDENCY_SHA256[module.__name__])


def quarantine(channels):
    for channel in channels:
        if type(channel) in (transport.HeldPipe, transport.OpaqueDrain):
            channel.failed = True


def pipe_links(channels):
    links = tuple(os.readlink('/proc/self/fd/' + str(c.fd)) for c in channels)
    require(all(re.fullmatch(r'pipe:\[[1-9][0-9]*\]', value) for value in links)
            and len(set(links)) == len(channels))
    return links


def validate_channels(collector, producer, collector_out, collector_err, producer_out, producer_err):
    require(type(collector_out) is transport.HeldPipe and type(producer_out) is transport.HeldPipe
            and type(collector_err) is transport.OpaqueDrain and type(producer_err) is transport.OpaqueDrain)
    channels = (collector_out, collector_err, producer_out, producer_err)
    require(isinstance(collector, kernel_exit.subprocess.Popen)
            and isinstance(producer, kernel_exit.subprocess.Popen) and producer.returncode is None)
    require(len({c.fd for c in channels}) == 4 and collector is not producer)
    require(collector_out.stream is collector.stdout and collector_err.stream is collector.stderr
            and producer_out.stream is producer.stdout and producer_err.stream is producer.stderr)
    require(all(not c.stream.closed and c.stream.fileno() == c.fd and not c.failed for c in channels))
    cancelled, expires = collector_out.cancelled, collector_out.expires
    require(type(expires) in (int, float) and math.isfinite(expires)
            and all(c.cancelled is cancelled and c.expires == expires for c in channels))
    drains = (collector_err, producer_err)
    require(collector_out.stderr_drains == drains and producer_out.stderr_drains == drains
            and not collector_out.buffer.pending and not producer_out.buffer.pending
            and not producer_out.buffer.eof)
    return channels, cancelled, expires


def drain_collector_preack(collector, producer, collector_pidfd, original_identity,
                          collector_out, collector_err, producer_out, producer_err, deadline):
    """Collector-only EOF + real nonreaping settled exit; no receipt or ACK emitted.

    Producer stderr is serviced but need not reach EOF. Producer stdout activity
    (including EOF) is forbidden while it must remain held for stopped ACK.
    No owner handle is closed, reaped, killed or replaced. Exit code stays private.
    """
    supplied = (collector_out, collector_err, producer_out, producer_err)
    try:
        verify_dependencies()
        channels, cancelled, expires = validate_channels(collector, producer, *supplied)
        require(type(deadline) in (int, float) and math.isfinite(deadline))
        deadline = min(deadline, expires, time.monotonic() + 15)
        original_links = pipe_links(channels)
        with selectors.DefaultSelector() as selector:
            selector.register(producer_out.fd, selectors.EVENT_READ, producer_out)
            for channel in (collector_out, collector_err, producer_err):
                eof = channel.buffer.eof if type(channel) is transport.HeldPipe else channel.eof
                if not eof:
                    selector.register(channel.fd, selectors.EVENT_READ, channel)
            while True:
                require(not cancelled.is_set() and time.monotonic() < deadline
                        and all(not c.failed for c in channels))
                # Check every ready channel even if collector was already settled;
                # no queued producer stdout can be silently acknowledged.
                for key, _ in selector.select(0):
                    channel = key.data
                    if channel is producer_out:
                        raise PreackRefused()
                    if type(channel) is transport.OpaqueDrain:
                        channel.consume()
                        if channel.eof:
                            selector.unregister(channel.fd)
                    else:
                        try:
                            raw = os.read(channel.fd, 4096)
                        except BlockingIOError:
                            continue
                        require(not raw)  # Stop emitted no further stdout message.
                        channel.buffer.finish()
                        selector.unregister(channel.fd)
                observed = kernel_exit.observe_original(collector, collector_pidfd, original_identity)
                require(type(observed) is kernel_exit.ExitObservation and observed.reaped is False
                        and observed.disposed is False and type(observed.settled) is bool)
                if observed.settled:
                    require(type(observed.exit_code) is int and observed.exit_code == 0)
                    if collector_out.buffer.eof and collector_err.eof:
                        require(pipe_links(channels) == original_links and not cancelled.is_set()
                                and time.monotonic() < deadline)
                        return observed
                else:
                    require(observed.exit_code is None)
                # Finite wait services readiness on the next iteration; no raw bytes
                # are discarded and producer stderr EOF is never a completion gate.
                left = deadline - time.monotonic()
                require(left > 0)
                selector.select(min(0.05, left))
    except BaseException:
        quarantine(supplied)
        raise PreackRefused() from None


def _write_stop_ack_owned(ack, producer_in, producer_out, channels, deadline):
    """Actual ACK write, watching forbidden stdout through EVERY wait/attempt."""
    cancelled, expires = producer_in.cancelled, producer_in.expires
    require(type(ack) is bytes and ack == b'stopped\n')
    retained = tuple((channel, channel.fd, channel.stream) for channel in channels)
    original_links = pipe_links(channels)

    def binding():
        require(not cancelled.is_set() and time.monotonic() < min(deadline, expires))
        require(all(c.fd == fd and c.stream is stream and not stream.closed
                    and stream.fileno() == fd and not c.failed
                    and c.cancelled is cancelled and c.expires == expires for c, fd, stream in retained))
        require(pipe_links(channels) == original_links)
        require(producer_in.stderr_drains == (channels[1], channels[3])
                and not producer_out.buffer.pending and not producer_out.buffer.eof)

    offset = 0
    with selectors.DefaultSelector() as selector:
        selector.register(producer_in.fd, selectors.EVENT_WRITE, producer_in)
        selector.register(producer_out.fd, selectors.EVENT_READ, producer_out)
        for drain in producer_in.stderr_drains:
            if not drain.eof:
                selector.register(drain.fd, selectors.EVENT_READ, drain)
        while offset < len(ack):
            binding()
            ready = selector.select(min(0.05, min(deadline, expires) - time.monotonic()))
            binding()
            # Handle forbidden activity before ANY write, even when stdin is ready
            # in that same readiness batch. EOF is activity too, never a pass.
            require(not any(key.data is producer_out for key, _ in ready))
            writable = False
            for key, _ in ready:
                if key.data is producer_in:
                    writable = True
                else:
                    key.data.consume()
                    if key.data.eof:
                        selector.unregister(key.fd)
            if not writable:
                continue
            binding()
            # Recheck forbidden stdout immediately before a partial write attempt.
            with selectors.DefaultSelector() as guard:
                guard.register(producer_out.fd, selectors.EVENT_READ, producer_out)
                require(not guard.select(0))
            try:
                count = os.write(producer_in.fd, ack[offset:])
            except BlockingIOError:
                continue
            require(type(count) is int and 0 < count <= len(ack) - offset)
            offset += count
            binding()


def complete_collector_stop(phase, private_receipt, expected_collector_source, expected_input_sha256, expected_targets,
                            collector, producer, collector_pidfd, original_identity,
                            collector_out, collector_err, producer_out, producer_err, producer_in, deadline):
    """Only actual source-bound receipt admission may produce the stopped ACK."""
    supplied = (collector_out, collector_err, producer_out, producer_err, producer_in)
    try:
        require(type(deadline) in (int, float) and math.isfinite(deadline))
        deadline = min(deadline, collector_out.expires, time.monotonic() + 15)
        require(type(phase) is coordinator.PhaseCoordinator and type(private_receipt) is coordinator.PrivateReceipt)
        require(type(expected_collector_source) is coordinator.SourceAssociation
                and private_receipt.association == expected_collector_source)
        expected_collector_source.validate()
        require(type(producer_in) is transport.HeldPipe and not producer_in.failed
                and producer_in.stream is producer.stdin and not producer_in.stream.closed
                and producer_in.stream.fileno() == producer_in.fd
                and producer_in.fd not in {c.fd for c in supplied[:-1]}
                and producer_in.cancelled is collector_out.cancelled
                and producer_in.expires == collector_out.expires
                and producer_in.stderr_drains == (collector_err, producer_err))
        input_link = pipe_links(supplied)
        observed = drain_collector_preack(collector, producer, collector_pidfd, original_identity,
                                         collector_out, collector_err, producer_out, producer_err, deadline)
        ack = phase.collector_stopped(private_receipt, observed.exit_code,
                                      collector_out.buffer.eof and collector_err.eof,
                                      time.monotonic(), expected_input_sha256, expected_targets)
        require(ack == b'stopped\n')
        require(pipe_links(supplied) == input_link)
        _write_stop_ack_owned(ack, producer_in, producer_out, supplied, deadline)
        # Source-approved observation only. Owner still must reap/dispose its exact
        # collector and producer; this module never emits cleanup/eight acceptance.
        return None
    except BaseException:
        quarantine(supplied)
        raise PreackRefused() from None
