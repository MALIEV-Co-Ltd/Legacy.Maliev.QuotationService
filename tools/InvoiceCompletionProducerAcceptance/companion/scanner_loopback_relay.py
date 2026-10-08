"""Owned in-process loopback relay for an observed internal scanner endpoint.

Only one framed PING, VERSION or complete INSTREAM request is allowed per socket.
Payload is streamed unmodified and never persisted or included in receipts.
"""
import os
import errno
import socket
import struct
import threading
import time
from pathlib import Path


class StartupPingRefused(OSError):
    """Only a source-correlated, physically settled startup connect refusal."""


class _StartupAttempt:
    def __init__(self):
        self.client = self.accepted = self.backend = self.worker = None
        self.peer = self.original_failure = self.failure_index = self.caller_failure = None
        self.accounted = self.succeeded = self.settled = False
        self.cleanup_errors = ()
        self.deadline = None


class LoopbackRelay:
    def __init__(self, validate_backend, seconds=10, maximum_bytes=200*1024*1024, startup_tracking=False):
        if not 0 < seconds <= 10 or not 0 < maximum_bytes <= 200*1024*1024:
            raise ValueError("Finite relay limits required")
        if type(startup_tracking) is not bool:
            raise ValueError("Explicit startup tracking mode required")
        self._startup_tracking = startup_tracking
        self.validate_backend = validate_backend
        self.seconds = seconds
        self.maximum_bytes = maximum_bytes
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.sockets = set()
        self.workers = set()
        self.capacity = threading.BoundedSemaphore(4)
        self.failures = []
        self._startup_attempts = []
        self._startup_peers = {}
        self._startup_bad = False
        self._startup_sealed = False
        self._startup_unhandled = []
        self._startup_overflow = None
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            self.listener.bind(("127.0.0.1", 0))
            self.listener.listen(4)
            self.listener.settimeout(0.1)
            self.endpoint = self.listener.getsockname()
            self.identity = {"host": self.endpoint[0], "port": self.endpoint[1],
                             "ownerPid": os.getpid(), "listenerFd": self.listener.fileno(),
                             "maximumConnectionSeconds": seconds, "maximumPayloadBytes": maximum_bytes,
                             "maximumConcurrentConnections": 4}
            if os.name == "posix":
                self.identity["listenerSocket"] = os.readlink(f"/proc/self/fd/{self.listener.fileno()}")
                stat = Path("/proc/self/stat").read_text(encoding="ascii")
                self.identity["ownerKernelStartTicks"] = int(stat[stat.rfind(")")+2:].split()[19])
            self.acceptor = threading.Thread(target=self._accept, daemon=True, name="owned-scanner-relay-accept")
            self.acceptor.start()
        except Exception:
            self.listener.close()
            raise

    def _accept(self):
        while not self.stop.is_set():
            try:
                client, address = self.listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            try:
                with self.lock:
                    # Root the ORIGINAL returned accepted socket before rejection,
                    # close, capacity, association or worker construction.
                    self.sockets.add(client)
                    if self.stop.is_set() or address[0] != "127.0.0.1" or not self.capacity.acquire(blocking=False):
                        if not self.stop.is_set():
                            self._startup_bad = True
                        client.close()
                        if client.fileno() != -1:
                            self._startup_bad = True
                            return
                        self.sockets.discard(client)
                        continue
                    attempt = self._startup_peers.get(address) if self._startup_tracking and not self._startup_sealed else None
                    if self._startup_tracking and not self._startup_sealed and attempt is None:
                        self._startup_bad = True
                        errors = []
                        closed = False
                        try:
                            client.close()
                        except BaseException as error:
                            errors.append(error)
                        try:
                            closed = client.fileno() == -1
                            if closed:
                                self.sockets.discard(client)
                        except BaseException as error:
                            errors.append(error)
                        try:
                            self.capacity.release()
                        except BaseException as error:
                            errors.append(error)
                        for error in errors:
                            if len(self.failures) < 16:
                                index = len(self.failures)
                                self.failures.append(type(error).__name__)
                                self._startup_unhandled.append((index, error))
                            elif self._startup_overflow is None:
                                self._startup_overflow = error
                        if errors or not closed:
                            return
                        continue
                    if attempt is not None:
                        if self._startup_sealed or attempt.accepted is not None or attempt.client is None or attempt.client.fileno() < 0:
                            self._startup_bad = True
                            attempt = None
                        else:
                            attempt.accepted = client
                    worker = threading.Thread(target=self._forward, args=(client, attempt), daemon=True, name="owned-scanner-relay-stream")
                    self.workers.add(worker)
                    if attempt is not None:
                        attempt.worker = worker
                    worker.start()
            except BaseException as error:
                self._retain_unhandled_failure(error)
                # Original accepted socket is already retained in self.sockets;
                # returned worker is rooted before start. An uncertain setup
                # prevents further accepts rather than erasing its custody.
                with self.lock:
                    self._startup_bad = True
                return

    def _forward(self, client, attempt=None):
        backend = None
        command = bytearray()
        connecting = False
        deadline = attempt.deadline if attempt is not None else time.monotonic() + self.seconds
        def remaining():
            budget = deadline-time.monotonic()
            if self.stop.is_set() or budget <= 0:
                raise TimeoutError("Owned relay deadline expired")
            return budget
        def read_exact(connection, length):
            result = bytearray()
            while len(result) < length:
                connection.settimeout(remaining())
                chunk = connection.recv(min(65536, length-len(result)))
                if not chunk:
                    raise ValueError("Truncated scanner request frame")
                result.extend(chunk)
            return bytes(result)
        def send(connection, value):
            connection.settimeout(remaining())
            connection.sendall(value)
        def read_command():
            while len(command) < 16:
                command.extend(read_exact(client, 1))
                if command[-1] == 0:
                    break
            if bytes(command) not in (b"zPING\0", b"zVERSION\0", b"zINSTREAM\0"):
                raise ValueError("Unreviewed scanner relay command")
        try:
            # Validation happens for every accepted stream, before backend connect.
            host, port = self.validate_backend()
            remaining()
            if attempt is not None:
                read_command()
            backend = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            if attempt is not None:
                attempt.backend = backend
            with self.lock:
                if self.stop.is_set():
                    raise TimeoutError("Owned relay stopped")
                self.sockets.add(backend)
            backend.settimeout(remaining())
            connecting = True
            backend.connect((host, port))
            connecting = False
            if attempt is None:
                read_command()
            send(backend, command)
            if command == b"zINSTREAM\0":
                consumed = 0
                while True:
                    header = read_exact(client, 4)
                    length = struct.unpack("!I", header)[0]
                    if length > 1024*1024 or consumed+length > self.maximum_bytes:
                        raise ValueError("Scanner relay payload exceeded bound")
                    send(backend, header)
                    if not length:
                        break
                    consumed += length
                    while length:
                        chunk = read_exact(client, min(65536, length))
                        send(backend, chunk)
                        length -= len(chunk)
            response_bytes = 0
            while response_bytes < 4096:
                backend.settimeout(remaining())
                chunk = backend.recv(min(4096-response_bytes, 4096))
                if not chunk:
                    raise ValueError("Scanner relay response lacks terminator")
                response_bytes += len(chunk)
                if b"\0" in chunk and chunk.split(b"\0", 1)[1]:
                    raise ValueError("Unexpected scanner response trailing bytes")
                send(client, chunk)
                if b"\0" in chunk:
                    return
            raise ValueError("Scanner relay response exceeded bound")
        except BaseException as error:
            # The caller receives closed/truncated transport and fails closed.
            with self.lock:
                if len(self.failures) < 16:
                    index = len(self.failures)
                    self.failures.append(type(error).__name__)
                    eligible = (not self._startup_sealed and connecting
                                and type(error) is ConnectionRefusedError and error.errno == errno.ECONNREFUSED
                                and bytes(command) == b"zPING\0" and attempt is not None
                                and any(row is attempt for row in self._startup_attempts)
                                and self._startup_peers.get(attempt.peer) is attempt
                                and attempt.accepted is client and attempt.backend is backend
                                and attempt.original_failure is None)
                    if eligible:
                        attempt.original_failure = error
                        attempt.failure_index = index
                    else:
                        self._startup_bad = True
                        self._startup_unhandled.append((index,error))
                else:
                    self._startup_bad = True
                    if self._startup_overflow is None:
                        self._startup_overflow = error
        finally:
            close_failed = False
            for connection in (client, backend):
                if connection is not None:
                    try:
                        connection.close()
                    except BaseException as error:
                        close_failed = True
                        self._retain_unhandled_failure(error)
                    try:
                        if connection.fileno() == -1:
                            with self.lock:
                                self.sockets.discard(connection)
                        else:
                            close_failed = True
                    except BaseException as error:
                        close_failed = True
                        self._retain_unhandled_failure(error)
            try:
                self.capacity.release()
            except BaseException as error:
                close_failed = True
                self._retain_unhandled_failure(error)
            with self.lock:
                if close_failed:
                    self._startup_bad = True
                else:
                    self.workers.discard(threading.current_thread())

    def _retain_unhandled_failure(self, error):
        with self.lock:
            self._startup_bad = True
            if len(self.failures) < 16:
                index = len(self.failures)
                self.failures.append(type(error).__name__)
                self._startup_unhandled.append((index,error))
            elif self._startup_overflow is None:
                self._startup_overflow = error

    def _settle_startup(self, attempt, deadline):
        errors = []
        try:
            if attempt.client is not None:
                attempt.client.close()
                with self.lock:
                    if attempt.client.fileno() == -1:
                        self.sockets.discard(attempt.client)
        except BaseException as error:
            errors.append(error)
            self._retain_unhandled_failure(error)
        try:
            if attempt.worker is not None:
                attempt.worker.join(timeout=max(0, deadline-time.monotonic()))
        except BaseException as error:
            errors.append(error)
            self._retain_unhandled_failure(error)
        attempt.cleanup_errors = tuple(errors)
        with self.lock:
            settled = (not errors and time.monotonic() <= deadline
                       and attempt.client is not None and attempt.client.fileno() == -1
                       and attempt.accepted is not None and attempt.accepted.fileno() == -1
                       and attempt.worker is not None and not attempt.worker.is_alive()
                       and (attempt.backend is None or attempt.backend.fileno() == -1)
                       and attempt.client not in self.sockets and attempt.accepted not in self.sockets
                       and attempt.backend not in self.sockets and attempt.worker not in self.workers)
            if settled:
                attempt.settled = True
            else:
                self._startup_bad = True
        if not settled:
            raise ValueError("Original startup PING ownership unsettled")

    def startup_ping(self, timeout=3):
        if not self._startup_tracking:
            raise ValueError("Startup tracking was not enabled before birth")
        if type(timeout) not in (int, float) or not 0 < timeout <= 10:
            raise ValueError("Finite startup PING deadline required")
        deadline = time.monotonic()+timeout
        attempt = _StartupAttempt()
        attempt.deadline = deadline
        with self.lock:
            if (self.stop.is_set() or self._startup_bad or self._startup_sealed or len(self._startup_attempts) >= 16
                    or not all(type(row) is _StartupAttempt and row.settled
                               and (row.accounted != row.succeeded) for row in self._startup_attempts)):
                self._startup_bad = True
                raise ValueError("Startup PING owner quarantined or exhausted")
            # Retain the actual owner BEFORE any socket allocation or operation.
            self._startup_attempts.append(attempt)
        failure = None
        truncated = False
        answer = None
        def remaining():
            budget = deadline-time.monotonic()
            if self.stop.is_set() or budget <= 0:
                raise TimeoutError("Original startup PING deadline expired")
            return budget
        try:
            attempt.client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            with self.lock:
                self.sockets.add(attempt.client)
            attempt.client.bind(("127.0.0.1", 0))
            attempt.peer = attempt.client.getsockname()
            with self.lock:
                if attempt.peer in self._startup_peers:
                    raise ValueError("Ambiguous original startup peer")
                self._startup_peers[attempt.peer] = attempt
            attempt.client.settimeout(remaining())
            attempt.client.connect(self.endpoint)
            if attempt.client.getpeername() != self.endpoint or attempt.client.getsockname() != attempt.peer:
                raise ValueError("Original startup peer differs")
            attempt.client.settimeout(remaining())
            attempt.client.sendall(b"zPING\0")
            response = bytearray()
            while len(response) <= 4096:
                attempt.client.settimeout(remaining())
                chunk = attempt.client.recv(min(4097-len(response),4096))
                if not chunk:
                    truncated = True
                    raise ValueError("Original startup reply truncated")
                response.extend(chunk)
                if b"\0" in response:
                    if response != b"PONG\0":
                        raise ValueError("Original startup PONG differs")
                    answer = "PONG"
                    break
            if answer is None:
                raise ValueError("Original startup reply exceeded cap")
        except BaseException as error:
            failure = error
            attempt.caller_failure = error
        finally:
            # Attempt caller close and original worker join independently, once.
            try:
                self._settle_startup(attempt,deadline)
            except BaseException:
                with self.lock:
                    self._startup_bad = True
                raise
        with self.lock:
            index = attempt.failure_index
            accounted = (not self._startup_bad and truncated and type(failure) is ValueError
                         and attempt.settled and type(attempt.original_failure) is ConnectionRefusedError
                         and attempt.original_failure.errno == errno.ECONNREFUSED
                         and type(index) is int and 0 <= index < len(self.failures)
                         and self.failures[index] == type(attempt.original_failure).__name__
                         and self._startup_peers.get(attempt.peer) is attempt)
            if accounted:
                attempt.accounted = True
            elif not self._startup_bad and failure is None and answer == "PONG" and attempt.original_failure is None:
                attempt.succeeded = True
            else:
                self._startup_bad = True
        if accounted:
            raise StartupPingRefused("Original settled startup connect refused") from None
        if failure is not None:
            raise failure
        if not attempt.succeeded:
            raise ValueError("Original startup PING observation refused")
        return answer

    def _startup_history_valid(self):
        attempts = self._startup_attempts
        rows = tuple(row for row in attempts if row.accounted)
        return (not self._startup_bad and bool(attempts) and any(row.succeeded for row in attempts)
                and all(type(row) is _StartupAttempt and row.settled and (row.succeeded != row.accounted)
                        and row.client is not None and row.client.fileno() == -1
                        and row.accepted is not None and row.accepted.fileno() == -1
                        and row.worker is not None and not row.worker.is_alive()
                        and (row.backend is None or row.backend.fileno() == -1)
                        and self._startup_peers.get(row.peer) is row for row in attempts)
                and len(self.failures) == len(rows)
                and tuple(row.failure_index for row in rows) == tuple(range(len(self.failures)))
                and all(type(row.original_failure) is ConnectionRefusedError
                        and row.original_failure.errno == errno.ECONNREFUSED
                        and self.failures[row.failure_index] == type(row.original_failure).__name__ for row in rows))

    def seal_startup_readiness(self):
        with self.lock:
            if self._startup_sealed or not self._startup_history_valid() or self.sockets or self.workers:
                self._startup_bad = True
                raise ValueError("Original startup handoff unsettled or unhandled")
            self._startup_sealed = True
            return {"originalStartupAttempts": len(self._startup_attempts),
                    "originalSettledConnectRefusals": len(self.failures),
                    "originalStartupPongObserved": True,"originalStartupWorkersSettled": True}

    def ready_history_valid(self):
        with self.lock:
            try:
                valid = self._startup_sealed and self._startup_history_valid()
            except BaseException as error:
                self._startup_bad = True
                if len(self.failures) < 16:
                    index = len(self.failures)
                    self.failures.append(type(error).__name__)
                    self._startup_unhandled.append((index, error))
                elif self._startup_overflow is None:
                    self._startup_overflow = error
                return False
            if not valid:
                self._startup_bad = True
            return valid

    def close(self):
        deadline = time.monotonic()+5
        self.stop.set()
        self.listener.close()
        with self.lock:
            for connection in tuple(self.sockets):
                try:
                    connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                connection.close()
            workers = tuple(self.workers)
        self.acceptor.join(timeout=max(0, deadline-time.monotonic()))
        for worker in workers:
            worker.join(timeout=max(0, deadline-time.monotonic()))
        with self.lock:
            verified = not self.acceptor.is_alive() and not any(worker.is_alive() for worker in self.workers) and not self.sockets and self.listener.fileno() == -1
        inode_absent = None
        if os.name == "posix":
            try:
                links = []
                for descriptor in Path("/proc/self/fd").iterdir():
                    try:
                        links.append(os.readlink(descriptor))
                    except FileNotFoundError:
                        continue
                inode_absent = self.identity["listenerSocket"] not in links
                verified = verified and inode_absent
            except OSError:
                verified = False
        return {**self.identity, "cleanupVerified": verified, "listenerClosed": self.listener.fileno() == -1,
                "ownedListenerInodeAbsent": inode_absent, "connectionFailureTypes": list(self.failures),
                "acceptorStopped": not self.acceptor.is_alive()}
