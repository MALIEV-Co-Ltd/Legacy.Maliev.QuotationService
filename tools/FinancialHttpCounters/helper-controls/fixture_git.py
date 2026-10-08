#!/usr/bin/python3
"""Finite synthetic executable; no Git repository, network, shell or secrets."""
import json
import os
import pathlib
import sys
import time

root = pathlib.Path(os.environ["SOURCE_HELPER_ROOT"])
mode = os.environ["SOURCE_HELPER_CASE"]
args = sys.argv[1:]
allowed = [["rev-parse", "HEAD"], ["rev-parse", "HEAD^{tree}"],
           ["status", "--porcelain", "--untracked-files=all"]]
if len(args) < 3 or args[:2] != ["-C", str(root)] or args[2:] not in allowed:
    sys.exit(71)

def record(kind):
    stat = pathlib.Path(f"/proc/{os.getpid()}/stat").read_text()
    ticks = int(stat[stat.rindex(")") + 2:].split()[19])
    with (root / "births.jsonl").open("a") as output:
        output.write(json.dumps({"kind": kind, "pid": os.getpid(),
                                 "ppid": os.getppid(), "ticks": ticks,
                                 "stdoutPipe": "pipe:[%d]" % os.fstat(1).st_ino,
                                 "stderrPipe": "pipe:[%d]" % os.fstat(2).st_ino}) + "\n")
        output.flush()

record("git")
if mode == "fast":
    print("1" * 40 if args[2:] == allowed[0] else "2" * 40
          if args[2:] == allowed[1] else "", end="\n")
elif mode == "overflow":
    os.write(1, b"x" * 65537)
    (root / "overflow-written").write_text("65537")
    time.sleep(25)
elif mode == "timeout":
    (root / "blocked").write_text("ready")
    time.sleep(25)
elif mode == "inherited":
    if args[2:] != allowed[0]:
        sys.exit(74)
    try:
        created = os.open(root / "writer-created", os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.close(created)
    except FileExistsError:
        sys.exit(75)
    child = os.fork()
    if child == 0:
        record("pipe-writer")
        (root / "writer-ready").write_text("ready")
        deadline = time.monotonic() + 25
        while not (root / "release").exists() and time.monotonic() < deadline:
            if (root / "writer-bound").exists() and not (root / "writer-ack").exists():
                (root / "writer-ack.tmp").write_text((root / "writer-bound").read_text() + "|" + str(os.getpid()) + "|" + str(int(pathlib.Path(f"/proc/{os.getpid()}/stat").read_text().split(")", 1)[1].split()[19])))
            if (root / "writer-ack.tmp").exists():
                (root / "writer-ack.tmp").replace(root / "writer-ack")
            time.sleep(0.02)
        os.close(1)
        os.close(2)
        (root / "writer-closed").write_text("closed")
        os._exit(0)
    deadline = time.monotonic() + 5
    while not (root / "writer-ack").exists() and time.monotonic() < deadline:
        time.sleep(0.02)
    if not (root / "writer-ack").exists():
        sys.exit(73)
    print("1" * 40)
else:
    sys.exit(72)
