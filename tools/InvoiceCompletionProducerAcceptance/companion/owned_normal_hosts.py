"""Source-only outer lifetime owner for the eight real normal .NET hosts.

No import side effects or CLI. Exact reviewed environments, source graph, twelve-backend
admission and pinned storage/scanner composition must be supplied by the dedicated caller.
This module does not seed, migrate, build, synthesize authority, or execute the financial probe.
"""
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import time
import threading

import hosted_companion_resources as h
import tls_listener_admission as tls

OWNERS = frozenset({"Auth","Accounting","Quotation","Order","IAM","Document","File","Notification"})


@dataclass(frozen=True)
class HostSpec:
    owner: str
    repository: str
    source_sha: str
    source_tree: str
    executable_dll: str
    executable_sha256: str
    host_ip: str
    host_port: int
    environment: dict
    heap_limit_bytes: int
    tls: tls.TlsIdentity | None = None


def available_memory_bytes():
    value = re.findall(r"^MemAvailable:\s+([0-9]+) kB$",Path("/proc/meminfo").read_text(),re.M)
    h.require(len(value) == 1, "Actual Linux memory admission unavailable")
    return int(value[0])*1024


def validate_specs(specs, context, minimum_guard_bytes, available_bytes):
    h.require(len(specs) == 8 and {spec.owner for spec in specs} == OWNERS, "Exact eight-host reviewed graph required")
    h.require(type(minimum_guard_bytes) is int and minimum_guard_bytes > 0, "Existing positive memory guard required")
    endpoints = set()
    for spec in specs:
        h.require(spec.host_ip in ("127.0.0.1","::1"), "Normal host loopback binding required"); h.port(spec.host_port)
        h.require((spec.host_ip,spec.host_port) not in endpoints, "Duplicate normal host endpoint")
        endpoints.add((spec.host_ip,spec.host_port))
        h.require(re.fullmatch(r"[0-9a-f]{40}",spec.source_sha) is not None
                  and re.fullmatch(r"[0-9a-f]{40}",spec.source_tree) is not None
                  and h.HEX64.fullmatch(spec.executable_sha256) is not None, "Exact reviewed source/build graph required")
        h.require(type(spec.heap_limit_bytes) is int and 64*1024**2 <= spec.heap_limit_bytes <= 1024**3,
                  "Finite per-process GC heap limit required")
        expected = h.ENVIRONMENT if spec.owner == "File" else "Production"
        h.consumed(spec.environment,"ASPNETCORE_ENVIRONMENT",expected)
        if any(key.lower() == "dotnet_environment" for key in spec.environment):
            h.consumed(spec.environment,"DOTNET_ENVIRONMENT",expected)
        if spec.owner == "File":
            h.require(spec.source_sha == context.file_sha, "File source differs from shared SDK/scanner admission")
            h.consumed(spec.environment,h.SECTION+"__Enabled","true")
            h.consumed(spec.environment,h.SECTION+"__Admission__fileSourceSha",context.file_sha)
        if spec.owner == "Notification": h.consumed(spec.environment,"Notifications__DeliveryIntentsEnabled","false")
        tls.validate_configuration(spec)
    # This supplements, never lowers, the existing caller's admission threshold.
    required = max(minimum_guard_bytes,sum(spec.heap_limit_bytes for spec in specs)+1024**3)
    h.require(type(available_bytes) is int and available_bytes >= required,
              "Memory guard failed; serialize/reuse approved resources, never lower the guard")
    return required


def file_hash(path):
    digest=hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda:stream.read(1024*1024),b""): digest.update(chunk)
    return digest.hexdigest()


def source_and_build(spec):
    repository=Path(spec.repository); dll=Path(spec.executable_dll)
    h.require(repository.is_absolute() and dll.is_absolute() and dll.resolve().is_relative_to(repository.resolve())
              and not repository.is_symlink() and not dll.is_symlink(), "Exact normal source/build root required")
    head=h.command(["git","-C",str(repository),"rev-parse","--verify","HEAD"]).decode("ascii").strip()
    tree=h.command(["git","-C",str(repository),"rev-parse","--verify","HEAD^{tree}"]).decode("ascii").strip()
    dirty=h.command(["git","-C",str(repository),"status","--porcelain","--untracked-files=all"])
    h.require(head == spec.source_sha and tree == spec.source_tree and not dirty
              and file_hash(dll) == spec.executable_sha256, "Actual normal host source/build differs")


def require_unused_expiry_signal():
    h.require(threading.current_thread() is threading.main_thread()
              and signal.getsignal(signal.SIGALRM) == signal.SIG_DFL
              and signal.getitimer(signal.ITIMER_REAL) == (0.0,0.0)
              and signal.SIGALRM not in signal.pthread_sigmask(signal.SIG_BLOCK,set()),
              "Dedicated launcher must own an unused unblocked expiry signal/timer")


class OwnedNormalHosts:
    """Retains actual child handles, exact kernel generations and finite scope; no broad process selectors."""
    def __init__(self, context, launcher_environment, specs, dotnet_executable, dotnet_sha256, minimum_guard_bytes):
        self.context=context; self.environment=dict(launcher_environment); self.specs=tuple(specs)
        self.dotnet=str(Path(dotnet_executable)); self.dotnet_hash=dotnet_sha256
        self.minimum_guard=minimum_guard_bytes; self.owned=[]; self.closed=False
        self.timer_owned=False
        self.timer_paused=False
        self.cleanup_failures=[]

    def __enter__(self):
        self.context.validate(self.environment,datetime.now(timezone.utc))
        h.require(h.sys.platform == "linux" and Path(self.dotnet).is_absolute() and not Path(self.dotnet).is_symlink()
                  and h.HEX64.fullmatch(self.dotnet_hash) is not None and file_hash(self.dotnet) == self.dotnet_hash,
                  "Exact normal hosted dotnet executable required")
        validate_specs(self.specs,self.context,self.minimum_guard,available_memory_bytes())
        for spec in self.specs: source_and_build(spec)
        for spec in self.specs:
            if spec.owner != "File": tls.load_identity(spec.tls)
        require_unused_expiry_signal()
        remaining=(h.instant(self.context.expires_utc)-datetime.now(timezone.utc)).total_seconds()
        h.require(remaining > 0,"Host owner lease expired before timer admission")
        signal.signal(signal.SIGALRM,self.expire)
        self.timer_owned=True
        try: signal.setitimer(signal.ITIMER_REAL,remaining)
        except BaseException:
            signal.signal(signal.SIGALRM,signal.SIG_DFL); self.timer_owned=False; raise
        return self

    def expire(self,signum,frame):
        # Finite lease enforcement stops only handles held by this owner, even if a caller catches the expiry.
        self.close()
        raise h.AdmissionError("Owned normal-host lease expired; owned children closed")

    def start(self, owner):
        h.require(not self.closed and not self.cleanup_failures
                  and not any(row[0].owner == owner for row in self.owned), "No duplicate host or failed/closed scope reuse")
        spec=next((spec for spec in self.specs if spec.owner == owner),None)
        h.require(spec is not None, "Unknown normal host")
        self.context.validate(self.environment,datetime.now(timezone.utc))
        h.require(available_memory_bytes() >= max(self.minimum_guard,spec.heap_limit_bytes+256*1024**2),
                  "Current startup memory guard failed")
        source_and_build(spec)
        # A conflicting user/shared listener is a blocker, never a cleanup target.
        family=socket.AF_INET if spec.host_ip == "127.0.0.1" else socket.AF_INET6
        with socket.socket(family,socket.SOCK_STREAM) as probe: probe.bind((spec.host_ip,spec.host_port))
        environment=dict(spec.environment)
        environment.update({"GITHUB_ACTIONS":"true","GITHUB_RUN_ID":self.context.run_id,
                            "GITHUB_RUN_ATTEMPT":str(self.context.attempt),"C821_FIXTURE_RUN_ID":self.context.lease_id,
                            "C821_FIXTURE_EXPIRES_UTC":self.context.expires_utc,
                            "DOTNET_GCHeapHardLimit":format(spec.heap_limit_bytes,"x")})
        tls.validate_configuration(spec)
        if spec.owner != "File": tls.load_identity(spec.tls)
        # Only exact normal dotnet+DLL arguments; reviewed consumed environment carries all configuration.
        try:
            row=self.spawn_owned(spec,environment)
            process=row[1]
            deadline=time.monotonic()+2
            while True:
                h.require(process.poll() is None, "Normal host exited during startup")
                try: row[2]=h.process_start_ticks(h.bounded_file(f"/proc/{process.pid}/stat",4096),process.pid); break
                except FileNotFoundError:
                    h.require(time.monotonic() < deadline,"Kernel startup observation deadline expired"); time.sleep(0.01)
            h.require(os.readlink(f"/proc/{process.pid}/exe") == self.dotnet, "Started normal executable differs")
            self.context.validate(self.environment,datetime.now(timezone.utc))
            # Bounded retry permits startup latency, never adopts another process's listener.
            readiness_deadline=time.monotonic()+15
            while True:
                try:
                    readiness=tls.admit_readiness(spec,process,row[2],self.dotnet,self.context,self.environment,
                                                  budget=min(5,max(0,readiness_deadline-time.monotonic())))
                    break
                except (OSError,h.AdmissionError):
                    h.require(process.poll() is None and time.monotonic() < readiness_deadline,
                              "Actual normal listener/TLS readiness failed before finite deadline")
                    self.context.validate(self.environment,datetime.now(timezone.utc))
                    time.sleep(0.05)
        except BaseException:
            self.close()
            raise
        return {"owner":spec.owner,"pid":process.pid,"kernelStartTicks":row[2],"observedStartUtc":row[3],
                "sourceSha":spec.source_sha,"sourceTree":spec.source_tree,"executableDll":spec.executable_dll,
                "executableSha256":spec.executable_sha256,"dotnetExecutable":self.dotnet,"dotnetSha256":self.dotnet_hash,
                "hostIp":spec.host_ip,"hostPort":spec.host_port,"expiresUtc":self.context.expires_utc,
                "heapLimitBytes":spec.heap_limit_bytes,"persistentData":False,**readiness}

    def spawn_owned(self,spec,environment):
        # Defer the owner's expiry signal only across child-handle acquisition
        # and ledger handoff. Restoring the original mask delivers any pending
        # expiry after the exact child is tracked, without resetting its lease.
        previous=signal.pthread_sigmask(signal.SIG_BLOCK,{signal.SIGALRM})
        try:
            process=subprocess.Popen([self.dotnet,spec.executable_dll],cwd=str(Path(spec.executable_dll).parent),
                                     env=environment,stdin=subprocess.DEVNULL,start_new_session=False)
            row=[spec,process,None,datetime.now(timezone.utc).isoformat()]
            self.owned.append(row)
            return row
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK,previous)

    def close(self):
        if self.closed: return
        # Prevent signal re-entry during bounded cleanup, but retain the expiry
        # handler until every child has actually exited.
        if self.timer_owned and not self.timer_paused:
            signal.setitimer(signal.ITIMER_REAL,0)
            self.timer_paused=True
        failures=[]
        for spec,process,start_ticks,_ in reversed(self.owned):
            try:
                if process.poll() is None:
                    try: current=h.process_start_ticks(h.bounded_file(f"/proc/{process.pid}/stat",4096),process.pid)
                    except (OSError,h.AdmissionError):
                        h.require(start_ticks is None,"Previously observed process generation is unavailable")
                        current=None  # Only this held, unreaped direct child can use handle ownership.
                    # Before initial kernel capture succeeds, an unreaped direct child is still
                    # owned by this exact Popen. Capture its generation now; never infer from an idle PID.
                    if start_ticks is None: start_ticks=current
                    h.require(current == start_ticks,
                              "Unverified/reused process; preserve and report exact handle")
                    process.terminate()
                    try: process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        if start_ticks is not None:
                            current=h.process_start_ticks(h.bounded_file(f"/proc/{process.pid}/stat",4096),process.pid)
                            h.require(current == start_ticks, "Process generation changed before forced exact termination")
                        else: h.require(process.poll() is None,"Unobserved direct child already exited before force")
                        process.kill(); process.wait(timeout=5)
                h.require(process.poll() is not None, "Owned normal process did not exit")
            except BaseException as error:
                failures.append((spec.owner,process.pid,type(error).__name__))
        self.closed=not failures
        self.cleanup_failures=failures
        if self.closed:
            self.release_timer()
        elif self.timer_owned:
            # Cleanup retries do not extend the admitted service lease. Handles,
            # exact generations and the original expiry remain retained; an
            # uncertain process is never signalled merely to satisfy expiry.
            signal.setitimer(signal.ITIMER_REAL,5)
            self.timer_paused=False
        h.require(not failures,"Owned normal host cleanup incomplete; retain exact ownership/expiry evidence")

    def __exit__(self,exception_type,exception,traceback):
        self.close()
        return False

    def release_timer(self):
        if self.timer_owned:
            if not self.timer_paused: signal.setitimer(signal.ITIMER_REAL,0)
            signal.signal(signal.SIGALRM,signal.SIG_DFL)
            self.timer_owned=False
            self.timer_paused=False
