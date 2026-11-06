"""Held storage-front child and EOF-sealed public bootstrap for the canonical parent.

No import side effects, container creation, secret persistence or acceptance claims.
The dedicated caller must own the existing finite normal-host expiry timer.
"""
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import ipaddress
import re
import json
import os
from pathlib import Path
import signal
import socket
import stat
import subprocess
from urllib.parse import urlsplit

import hosted_companion_resources as h
import owned_normal_hosts as normal
import tls_listener_admission as tls
from regular_owned_files import regular_bytes, regular_hash


class PublicBootstrapPipe:
    """Own both ends; after EOF retain the read end for actual parent admission."""
    def __init__(self):
        self.read_fd, self.write_fd = os.pipe()
        self.sealed = False
        self.closed = False

    def seal(self, frame):
        h.require(not self.closed and not self.sealed, "Bootstrap can be sealed only once")
        h.require(type(frame) is dict and set(frame) == {"Algorithm", "PublicKey", "File"},
                  "Public signing frame required")
        h.require(frame["Algorithm"] == "GOOG4-RSA-SHA256" and type(frame["PublicKey"]) is str
                  and 1 <= len(frame["PublicKey"]) <= 4096 and type(frame["File"]) is dict,
                  "Bounded public signing identity required")
        # Do not admit a private signer, bearer, arbitrary extension or unknown File field.
        expected = {"Pid", "StartedUtc", "ExecutableDll", "ExecutableSha256", "RunId", "ExpiresUtc"}
        h.require(set(frame["File"]) == expected, "Exact observed File identity required")
        data = json.dumps(frame, separators=(",", ":"), ensure_ascii=True).encode("ascii")
        # One frame fits the minimum POSIX atomic pipe bound. It cannot wait on a reader.
        h.require(len(data) <= 4096, "Atomic bounded public frame required")
        h.require(os.write(self.write_fd, data) == len(data), "Public frame was not written completely")
        os.close(self.write_fd)
        self.write_fd = None
        self.sealed = True
        # Deliberately retain read_fd: no surviving writer may prevent the child's EOF.

    def close(self):
        if self.closed:
            return
        for name in ("write_fd", "read_fd"):
            descriptor = getattr(self, name)
            if descriptor is not None:
                os.close(descriptor)
                setattr(self, name, None)
        self.closed = True

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


@dataclass(frozen=True)
class FrontSpec:
    repository: str
    source_sha: str
    source_tree: str
    executable_dll: str
    executable_sha256: str
    dotnet_executable: str
    dotnet_sha256: str
    profile_path: str
    environment: dict
    heap_limit_bytes: int


def admit_public_profile_shape(profile):
    """Declarations only; original owner and C# runtime still observe actual resources."""
    keys = {"RunId", "ExpiresUtc", "FrontOrigin", "Backend", "Repository", "SourceSha",
            "ExecutableDll", "ExecutableSha256", "ParentPid", "ParentKernelStartTicks",
            "ParentExecutablePath", "ParentExecutableSha256", "ParentScriptPath",
            "ParentScriptSha256", "BootstrapPipeHandle", "BootstrapPipeInode"}
    h.require(type(profile) is dict and (set(profile) == keys
              or set(profile) == keys | {"SharedScannerBridge"}), "Exact public front profile required")
    if "SharedScannerBridge" not in profile:
        return
    shared = profile["SharedScannerBridge"]
    fields = {"ScannerContainerId", "ScannerCreated", "ScannerStarted", "ScannerPid", "ScannerImageId",
              "ScannerBridgeIp", "NetworkName", "Subnet", "Gateway"}
    h.require(type(shared) is dict and set(shared) == fields, "Exact shared declaration required")
    backend = profile["Backend"]
    h.require(type(backend) is dict and backend.get("BindOwnedIpv4") is True,
              "Original owned private IPv4 binding required")
    h.require(type(shared["ScannerPid"]) is int and 0 < shared["ScannerPid"] <= 2**31 - 1
              and type(shared["ScannerContainerId"]) is str
              and re.fullmatch("[0-9a-f]{64}", shared["ScannerContainerId"]) is not None
              and shared["ScannerContainerId"] != backend.get("ContainerId")
              and type(shared["ScannerImageId"]) is str
              and re.fullmatch("sha256:[0-9a-f]{64}", shared["ScannerImageId"]) is not None,
              "Exact distinct shared scanner declaration required")
    lease = backend.get("LeaseId")
    h.require(type(lease) is str and lease == profile["RunId"]
              and shared["NetworkName"] == "financial-scanner-" + lease[5:] + "-network",
              "Same original shared ownership lease required")
    issued = h.instant(backend["IssuedUtc"])
    expires = h.instant(profile["ExpiresUtc"])
    created, started = h.instant(shared["ScannerCreated"]), h.instant(shared["ScannerStarted"])
    h.require(issued <= h.instant(backend["NetworkCreated"]) <= created <= started < expires,
              "Shared original generation order required")
    h.require(all(type(shared[key]) is str for key in ("Subnet", "Gateway", "ScannerBridgeIp")),
              "Exact canonical shared addresses required")
    network = ipaddress.IPv4Network(shared["Subnet"], strict=True)
    pool = ipaddress.IPv4Network("10.253.240.0/24")
    scanner = ipaddress.IPv4Address(shared["ScannerBridgeIp"])
    storage = ipaddress.IPv4Address(backend["BridgeIp"])
    h.require(str(network) == shared["Subnet"] and network.prefixlen == 28 and network.subnet_of(pool)
              and str(network.network_address + 1) == shared["Gateway"]
              and str(scanner) == shared["ScannerBridgeIp"] and str(storage) == backend["BridgeIp"]
              and scanner != storage and all(network.network_address + 1 < ip < network.broadcast_address
                                              for ip in (scanner, storage)), "Exact distinct usable shared addresses required")


def load_public_profile(spec, context, pipe):
    path = Path(spec.profile_path)
    root = Path(spec.repository) / "TestResults" / "C821ProducerProfiles"
    h.require(path.is_absolute() and path.resolve() == path and path.is_relative_to(root),
              "Canonical owned profile path required")
    h.require(all(not item.is_symlink() for item in (path, *path.parents)), "Redirected profile rejected")
    encoded, info = regular_bytes(path, 32768)
    h.require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
              and stat.S_IMODE(info.st_mode) == 0o600, "Owner-only regular profile required")
    def unique(pairs):
        result = {}
        for name, value in pairs:
            h.require(name not in result, "Duplicate profile property")
            result[name] = value
        return result
    retained = path.stat()
    h.require((retained.st_dev, retained.st_ino, retained.st_size, retained.st_mtime_ns)
              == (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns)
              and all(not item.is_symlink() for item in (path, *path.parents)), "Front profile changed during read")
    profile = json.loads(encoded, object_pairs_hook=unique)
    admit_public_profile_shape(profile)
    h.require(profile["RunId"] == context.lease_id and profile["ExpiresUtc"] == context.expires_utc
              and profile["Repository"] == spec.repository and profile["SourceSha"] == spec.source_sha
              and profile["ExecutableDll"] == spec.executable_dll
              and profile["ExecutableSha256"] == spec.executable_sha256, "Front profile source/lease differs")
    expected_script = str(Path(spec.repository) / "tools/InvoiceCompletionProducerAcceptance/companion/run_eight_host_financial_acceptance.py")
    prefix = f"/proc/{os.getpid()}"
    actual_args = h.bounded_file(prefix + "/cmdline", 65536).split(b"\0")
    h.require(profile["ParentPid"] == os.getpid() and len(actual_args) >= 3
              and actual_args[0].decode() == profile["ParentExecutablePath"]
              and actual_args[1].decode() == expected_script == profile["ParentScriptPath"]
              and os.readlink(prefix + "/exe") == profile["ParentExecutablePath"]
              and regular_hash(prefix + "/exe", proc_executable=True) == profile["ParentExecutableSha256"]
              and regular_hash(expected_script, 4 * 1024**2) == profile["ParentScriptSha256"]
              and h.process_start_ticks(h.bounded_file(prefix + "/stat", 4096), os.getpid())
                  == profile["ParentKernelStartTicks"], "Actual canonical parent identity differs")
    link = os.readlink(f"/proc/self/fd/{pipe.read_fd}")
    h.require(link == "pipe:[" + profile["BootstrapPipeInode"] + "]"
              and str(pipe.read_fd) == profile["BootstrapPipeHandle"], "Actual owner pipe differs")
    parsed = urlsplit(profile["FrontOrigin"])
    h.require(parsed.scheme == "http" and parsed.hostname in ("127.0.0.1", "::1")
              and parsed.path == "/" and not parsed.query and not parsed.fragment
              and parsed.username is None and parsed.password is None
              and parsed.port is not None and 1024 <= parsed.port <= 65535, "Exact loopback front origin required")
    return profile, hashlib.sha256(encoded).hexdigest()


class OwnedFrontHost:
    """No detached child: caller retains handle, exact generation, pipe and finite lease."""
    def __init__(self, spec, context, environment, pipe, minimum_guard_bytes):
        self.spec = spec
        self.context = context
        self.launcher_environment = environment
        self.pipe = pipe
        self.guard = minimum_guard_bytes
        self.process = None
        self.ticks = None
        self.cleanup_failure = None
        self.profile = None

    def acquire_child(self, arguments, **options):
        """Explicit acquisition seam; the original default retains its existing child."""
        return subprocess.Popen(arguments, **options)

    def start(self):
        h.require(h.sys.platform == "linux" and self.process is None and not self.pipe.sealed
                  and not self.pipe.closed, "Single hosted front launch required")
        self.context.validate(self.launcher_environment, datetime.now(timezone.utc))
        h.require(signal.getitimer(signal.ITIMER_REAL)[0] > 0
                  and signal.getsignal(signal.SIGALRM) != signal.SIG_DFL,
                  "Existing finite parent expiry owner required")
        h.require(type(self.guard) is int and self.guard >= 4 * 1024**3
                  and 64 * 1024**2 <= self.spec.heap_limit_bytes <= 256 * 1024**2
                  and normal.available_memory_bytes() >= max(self.guard, self.spec.heap_limit_bytes + 1024**3),
                  "Front capacity guard failed")
        # Observe source separately so an untrusted executable cannot FIFO-block the older hash helper.
        head = h.command(["git", "-C", self.spec.repository, "rev-parse", "HEAD"]).decode("ascii").strip()
        tree = h.command(["git", "-C", self.spec.repository, "rev-parse", "HEAD^{tree}"]).decode("ascii").strip()
        dirty = h.command(["git", "-C", self.spec.repository, "status", "--porcelain", "--untracked-files=all"])
        h.require(head == self.spec.source_sha and tree == self.spec.source_tree and not dirty
                  and Path(self.spec.executable_dll).is_relative_to(Path(self.spec.repository))
                  and regular_hash(self.spec.executable_dll) == self.spec.executable_sha256,
                  "Actual front source/build differs")
        h.require(Path(self.spec.dotnet_executable).is_absolute()
                  and regular_hash(self.spec.dotnet_executable) == self.spec.dotnet_sha256,
                  "Exact front dotnet executable required")
        self.profile, self.profile_hash = load_public_profile(self.spec, self.context, self.pipe)
        parsed = urlsplit(self.profile["FrontOrigin"])
        with socket.socket(socket.AF_INET if parsed.hostname == "127.0.0.1" else socket.AF_INET6,
                           socket.SOCK_STREAM) as probe:
            probe.bind((parsed.hostname, parsed.port))
        environment = dict(self.spec.environment)
        for name, value in {"ASPNETCORE_ENVIRONMENT": "Production", "GITHUB_ACTIONS": "true",
                            "GITHUB_RUN_ID": self.context.run_id, "GITHUB_RUN_ATTEMPT": str(self.context.attempt),
                            "GITHUB_SHA": self.spec.source_sha, "GITHUB_WORKSPACE": self.spec.repository,
                            "C821_FIXTURE_RUN_ID": self.context.lease_id,
                            "C821_FIXTURE_EXPIRES_UTC": self.context.expires_utc,
                            "DOTNET_GCHeapHardLimit": format(self.spec.heap_limit_bytes, "x")}.items():
            aliases = [key for key in environment if key.replace(":", "__").lower() == name.lower()]
            h.require(not aliases or aliases == [name] and environment[name] == value, "Front environment conflicts")
            environment[name] = value
        self.actual_environment = environment
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
        try:
            self.process = self.acquire_child([self.spec.dotnet_executable, self.spec.executable_dll, self.spec.profile_path],
                cwd=str(Path(self.spec.executable_dll).parent), env=environment, stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True,
                pass_fds=(self.pipe.read_fd,), start_new_session=False)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)
        self.ticks = h.process_start_ticks(h.bounded_file(f"/proc/{self.process.pid}/stat", 4096), self.process.pid)
        return self.process

    def observe_listener(self):
        h.require(self.process is not None and self.process.poll() is None and self.ticks is not None,
                  "Held live front required")
        self.context.validate(self.launcher_environment, datetime.now(timezone.utc))
        profile, digest = load_public_profile(self.spec, self.context, self.pipe)
        h.require(profile == self.profile and digest == self.profile_hash, "Front profile changed")
        prefix = f"/proc/{self.process.pid}"
        h.require(h.process_start_ticks(h.bounded_file(prefix + "/stat", 4096), self.process.pid) == self.ticks
                  and os.readlink(prefix + "/exe") == self.spec.dotnet_executable
                  and regular_hash(prefix + "/exe", proc_executable=True) == self.spec.dotnet_sha256
                  and regular_hash(self.spec.executable_dll) == self.spec.executable_sha256
                  and h.bounded_file(prefix + "/cmdline", 65536).split(b"\0") ==
                    [item.encode() for item in (self.spec.dotnet_executable, self.spec.executable_dll, self.spec.profile_path)] + [b""],
                  "Actual held front identity differs")
        actual = h.process_environment(h.bounded_file(prefix + "/environ", 262144))
        h.require(actual == self.actual_environment, "Actual complete front environment differs")
        parsed = urlsplit(profile["FrontOrigin"])
        tables = [h.bounded_file(path, 2 * 1024**2).decode("ascii") for path in ("/proc/net/tcp", "/proc/net/tcp6")]
        inode = h.listening_inode(tables, parsed.hostname, parsed.port)
        descriptors = list(Path(prefix + "/fd").iterdir())
        h.require(len(descriptors) <= 4096, "Bounded front descriptors required")
        links = [os.readlink(path) for path in descriptors]
        tls.require_listener_identity(self.ticks, self.ticks, inode, links)
        tls.require_only_owned_listener(tables, links, inode)
        h.require(h.process_start_ticks(h.bounded_file(prefix + "/stat", 4096), self.process.pid) == self.ticks
                  and self.process.poll() is None, "Front generation changed during observation")
        return {"pid": self.process.pid, "kernelStartTicks": self.ticks, "listenerInode": inode,
                "expiresUtc": self.context.expires_utc, "profileSha256": digest,
                "publicBootstrapSealed": self.pipe.sealed, "applicationHealthObserved": False,
                "backendObserved": False, "genuineEightHostFinancialAccepted": False}

    def close(self):
        if self.process is None:
            return
        try:
            if self.process.poll() is None:
                current = h.process_start_ticks(h.bounded_file(f"/proc/{self.process.pid}/stat", 4096), self.process.pid)
                if self.ticks is None:
                    self.ticks = current  # Held unreaped direct child before first successful capture.
                h.require(current == self.ticks, "Front generation uncertain; preserve exact handle")
                self.process.terminate()
                try:
                    self.process.wait(timeout=12)
                except subprocess.TimeoutExpired:
                    h.require(h.process_start_ticks(h.bounded_file(f"/proc/{self.process.pid}/stat", 4096), self.process.pid)
                              == self.ticks, "Front generation changed before force")
                    self.process.kill()
                    self.process.wait(timeout=5)
            h.require(self.process.poll() is not None, "Owned front did not exit")
        except BaseException as error:
            self.cleanup_failure = {"owner": "Front", "pid": self.process.pid,
                                    "kernelStartTicks": self.ticks, "expiresUtc": self.context.expires_utc,
                                    "errorType": type(error).__name__}
            raise

    def __enter__(self):
        try:
            self.start()
            return self
        except BaseException:
            self.close()
            raise

    def __exit__(self, *_):
        self.close()
