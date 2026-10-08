"""Finite disposable Python child; no producer DLL or business service substitute."""
import ctypes
import json
import os
import select
import signal
import socket
import sys
import time


def main():
    path = sys.argv[1] if len(sys.argv) == 2 else os.environ['HELD_SYNTHETIC_CONFIG']
    with open(path, encoding='utf-8') as stream:
        config = json.load(stream)
    assert set(config) == {'parentPid','port','bootstrapFd','deadline'}
    running = True
    def stop(signum, frame):
        nonlocal running
        running = False
    signal.signal(signal.SIGTERM, stop)
    # Independent finite synthetic lifetime even if an unexpected harness fault
    # prevents its normal exact-handle shutdown. No descendant is launched.
    assert os.getppid() == config['parentPid']
    libc = ctypes.CDLL(None, use_errno=True)
    assert libc.prctl(1, signal.SIGTERM, 0, 0, 0) == 0  # PR_SET_PDEATHSIG
    assert os.getppid() == config['parentPid']
    fd = config['bootstrapFd']
    if fd is not None:
        data = bytearray()
        while running and time.monotonic() < config['deadline']:
            if not select.select([fd], [], [], .05)[0]: continue
            block = os.read(fd, 256)
            if not block: break
            data.extend(block)
            assert len(data) <= 256
        assert data == b'held-lifetime-synthetic-bootstrap\n'
        os.close(fd)
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(('127.0.0.1', config['port']))
        listener.listen(4)
        listener.settimeout(.05)
        while running and time.monotonic() < config['deadline']:
            try: connection, address = listener.accept()
            except TimeoutError: continue
            with connection:
                connection.settimeout(.2)
                connection.recv(4096)
                connection.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok')


if __name__ == '__main__':
    try: main()
    except BaseException: sys.exit(1)  # No private config paths/tracebacks in inherited output.
