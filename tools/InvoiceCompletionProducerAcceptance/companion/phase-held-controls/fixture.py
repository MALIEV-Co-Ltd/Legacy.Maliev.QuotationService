"""Finite synthetic topology; private journals are not artifact receipts."""
import json
import os
from pathlib import Path
import select
import signal
import sys
import time


def publish(root, name, value):
    pending = root / (name + '.pending')
    pending.write_bytes(json.dumps(value).encode('ascii'))
    pending.replace(root / (name + '.json'))


def main():
    signal.signal(signal.SIGALRM, signal.SIG_DFL)
    signal.alarm(15)
    role, mode, directory = sys.argv[1:]
    root = Path(directory)
    raw = Path('/proc/self/stat').read_bytes()
    fields = raw[raw.rfind(b')') + 2:].split()
    publish(root, role + '-birth', {'pid': os.getpid(), 'ticks': int(fields[19]),
            'ppid': os.getppid(), 'pgrp': os.getpgrp(), 'sid': os.getsid(0),
            'pipes': [[os.fstat(fd).st_dev, os.fstat(fd).st_ino] for fd in (0, 1, 2)]})
    if role == 'collector':
        while not (root / 'collector-release').exists():
            time.sleep(.01)
        os._exit(7 if mode in ('nonzero', 'preack-nonzero') else 0)
    if role != 'producer':
        raise RuntimeError('Synthetic role refused')
    os.set_blocking(2, False)
    os.write(2, b'synthetic-stderr\n')
    publish(root, 'producer-ready', {'stderrWritten': True})
    acknowledged = bytearray()
    emitted = False
    while True:
        try:
            os.write(2, b'synthetic-stderr\n')
        except BlockingIOError:
            pass
        if mode == 'blocked' and (root / 'emit-unsolicited').exists() and not emitted:
            os.write(1, b'unsolicited\n')
            publish(root, 'unsolicited', {'emitted': True})
            emitted = True
        if mode != 'blocked':
            ready, _, _ = select.select([0], [], [], .02)
            if ready:
                piece = os.read(0, 4096)
                if not piece:
                    os._exit(9)
                acknowledged.extend(piece)
                if len(acknowledged) > 8:
                    os._exit(8)
                if bytes(acknowledged) == b'stopped\n':
                    publish(root, 'ack', {'acknowledged': True})
                    os._exit(0)
        else:
            time.sleep(.02)


if __name__ == '__main__':
    main()
