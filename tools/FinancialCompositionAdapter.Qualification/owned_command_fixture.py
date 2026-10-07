"""Finite synthetic child; journals are private and never artifact payloads."""
import json
import os
import pathlib
import signal
import sys
import time


def record(root, role):
    raw = pathlib.Path('/proc/self/stat').read_bytes()
    fields = raw[raw.rfind(b')') + 2:].split()
    pipes = [os.fstat(fd) for fd in (1, 2)]
    value = {'pid': os.getpid(), 'ppid': os.getppid(), 'ticks': int(fields[19]),
             'session': os.getsid(0), 'pipes': [[p.st_dev, p.st_ino] for p in pipes]}
    pending = root / (role + '.pending')
    pending.write_text(json.dumps(value), encoding='ascii')
    pending.replace(root / (role + '.json'))


def main():
    signal.signal(signal.SIGALRM, signal.SIG_DFL)
    signal.alarm(15)
    mode, directory = sys.argv[1:]
    root = pathlib.Path(directory)
    record(root, 'leader')
    if mode == 'natural' or mode == 'selector':
        os.write(1, b'owned-synthetic-output')
        return
    if mode == 'overflow':
        (root / 'write-intent').write_bytes(b'1')
        os.write(1, b'x' * 65537)
        time.sleep(15)
        return
    if mode in ('timeout', 'recovery'):
        time.sleep(15)
        return
    if mode == 'inherited':
        writer = os.fork()
        if writer == 0:
            signal.alarm(15)
            record(root, 'writer')
            time.sleep(15)
            os._exit(0)
        end = time.monotonic() + 2
        while not (root / 'writer.json').exists():
            if time.monotonic() >= end:
                raise RuntimeError('Synthetic readiness refused')
            time.sleep(.01)
        return
    raise RuntimeError('Synthetic mode refused')


if __name__ == '__main__':
    main()
