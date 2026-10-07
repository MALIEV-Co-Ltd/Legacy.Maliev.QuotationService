"""Owned in-process loopback relay for an observed internal scanner endpoint.

Only one framed PING, VERSION or complete INSTREAM request is allowed per socket.
Payload is streamed unmodified and never persisted or included in receipts.
"""
import os
import socket
import struct
import threading
import time
from pathlib import Path


class LoopbackRelay:
    def __init__(self, validate_backend, seconds=10, maximum_bytes=200*1024*1024):
        if not 0 < seconds <= 10 or not 0 < maximum_bytes <= 200*1024*1024:
            raise ValueError("Finite relay limits required")
        self.validate_backend = validate_backend
        self.seconds = seconds
        self.maximum_bytes = maximum_bytes
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.sockets = set()
        self.workers = set()
        self.capacity = threading.BoundedSemaphore(4)
        self.failures = []
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
            with self.lock:
                if self.stop.is_set() or address[0] != "127.0.0.1" or not self.capacity.acquire(blocking=False):
                    client.close()
                    continue
                self.sockets.add(client)
                worker = threading.Thread(target=self._forward, args=(client,), daemon=True, name="owned-scanner-relay-stream")
                self.workers.add(worker)
                worker.start()

    def _forward(self, client):
        backend = None
        deadline = time.monotonic() + self.seconds
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
        try:
            # Validation happens for every accepted stream, before backend connect.
            host, port = self.validate_backend()
            remaining()
            backend = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            with self.lock:
                if self.stop.is_set():
                    raise TimeoutError("Owned relay stopped")
                self.sockets.add(backend)
            backend.settimeout(remaining())
            backend.connect((host, port))
            command = bytearray()
            while len(command) < 16:
                command.extend(read_exact(client, 1))
                if command[-1] == 0:
                    break
            if bytes(command) not in (b"zPING\0", b"zVERSION\0", b"zINSTREAM\0"):
                raise ValueError("Unreviewed scanner relay command")
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
        except Exception as error:
            # The caller receives closed/truncated transport and fails closed.
            with self.lock:
                if len(self.failures) < 16:
                    self.failures.append(type(error).__name__)
        finally:
            with self.lock:
                for connection in (client, backend):
                    if connection is not None:
                        connection.close()
                        self.sockets.discard(connection)
                self.workers.discard(threading.current_thread())
                self.capacity.release()

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
