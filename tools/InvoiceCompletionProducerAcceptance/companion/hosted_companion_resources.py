"""Dedicated hosted launcher primitives. Importing this module starts no resource.

Root must review the actual eight-host caller, exact source graph and pinned endpoint/daemon
composition before activation. A pure validator accepting test observations is not admission.
Actual admission calls observe() on the real Docker engine, never a supplied JSON snapshot.
"""
from dataclasses import dataclass
from datetime import datetime, timezone
import base64
import hashlib
import http.client
import ipaddress
import io
import json
import os
from pathlib import Path
import re
import selectors
import socket
import struct
import subprocess
import sys
import time
import uuid

ENVIRONMENT = "HostedFinancialCompletionAcceptance"
SECTION = "HostedFinancialCompletionAcceptance"
HEX64 = re.compile(r"[0-9a-f]{64}")
IMAGE = re.compile(r"[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}")
LABEL_PREFIX = "com.maliev.c821."


class AdmissionError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise AdmissionError(message)


def instant(value):
    require(isinstance(value, str), "UTC timestamp required")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise AdmissionError("UTC timestamp invalid") from None
    require(result.tzinfo is not None and result.utcoffset().total_seconds() == 0, "UTC offset must be zero")
    return result


def port(value):
    require(type(value) is int and 1 <= value <= 65535, "Explicit valid port required")
    return value


def origin(value):
    require(isinstance(value, str), "Literal origin required")
    match = re.fullmatch(r"http://(127\.0\.0\.1|\[::1\]):([1-9][0-9]{0,4})", value)
    require(match is not None, "Only canonical explicit HTTP loopback origin admitted")
    host = match[1].strip("[]")
    return host, port(int(match[2]))


def exact_json(data):
    require(isinstance(data, bytes) and len(data) <= 262144, "Bounded JSON bytes required")
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON field")
            result[key] = value
        return result
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(AdmissionError("Nonfinite JSON denied")))
    except (UnicodeError, json.JSONDecodeError):
        raise AdmissionError("Strict UTF8 JSON required") from None


@dataclass(frozen=True)
class Context:
    run_id: str
    attempt: int
    file_sha: str
    lease_id: str
    issued_utc: str
    expires_utc: str

    def validate(self, environment, now, cleanup=False):
        require(re.fullmatch(r"[1-9][0-9]{0,19}", self.run_id) is not None, "Hosted run ID required")
        require(int(self.run_id) <= 2**63-1, "Hosted run exceeds consuming Int64 contract")
        require(type(self.attempt) is int and 0 < self.attempt <= 2**31-1, "Hosted attempt required")
        require(re.fullmatch(r"[0-9a-f]{40}", self.file_sha) is not None, "Exact File source SHA required")
        try:
            lease = uuid.UUID(self.lease_id.removeprefix("c821-"))
        except (ValueError, AttributeError):
            raise AdmissionError("Canonical nonzero fixture lease required") from None
        require(lease.int != 0 and self.lease_id == "c821-" + str(lease), "Canonical nonzero fixture lease required")
        issued, expires = instant(self.issued_utc), instant(self.expires_utc)
        require(now.tzinfo is not None and now.utcoffset().total_seconds() == 0
                and issued <= now and 0 < (expires-issued).total_seconds() <= 1800,
                "Current finite admission required")
        require(cleanup or now < expires, "Admission expired")
        require(environment.get("GITHUB_ACTIONS") == "true" and environment.get("GITHUB_RUN_ID") == self.run_id
                and environment.get("GITHUB_RUN_ATTEMPT") == str(self.attempt), "Actual hosted run differs")
        require(environment.get("C821_COMPANION_PROFILE") == ENVIRONMENT, "Dedicated reviewed companion profile required")

    def labels(self, role):
        require(role in ("storage", "scanner"), "Unknown disposable role")
        return {LABEL_PREFIX+"run": self.run_id, LABEL_PREFIX+"attempt": str(self.attempt),
                LABEL_PREFIX+"file-source": self.file_sha, LABEL_PREFIX+"lease": self.lease_id,
                LABEL_PREFIX+"expires": self.expires_utc, LABEL_PREFIX+"role": role,
                LABEL_PREFIX+"persistent": "false"}


@dataclass(frozen=True)
class ContainerLease:
    role: str
    container_id: str
    image_reference: str
    created_utc: str
    started_utc: str
    host_ip: str
    host_port: int
    container_port: int
    network_id: str
    entrypoint: tuple = ()
    arguments: tuple = ()

    def validate(self):
        require(self.role in ("storage", "scanner") and HEX64.fullmatch(self.container_id) is not None
                and HEX64.fullmatch(self.network_id) is not None, "Exact container/network identities required")
        require(IMAGE.fullmatch(self.image_reference) is not None, "Pinned repository image digest required")
        require(self.host_ip in ("127.0.0.1", "::1"), "Literal loopback binding required")
        port(self.host_port); port(self.container_port)
        instant(self.created_utc); instant(self.started_utc)
        require(all(isinstance(value, str) and value and "\x00" not in value
                    for value in (*self.entrypoint, *self.arguments)), "Reviewed container command required")


def validate_observations(context, lease, container, image, network, now, running=True):
    """Pure controls only. Actual callers obtain these observations through observe()."""
    lease.validate()
    require(container.get("Id") == lease.container_id and container.get("Created") == lease.created_utc,
            "Exact container creation identity differs")
    state = container.get("State", {})
    require(state.get("StartedAt") == lease.started_utc and not state.get("Restarting", True)
            and not state.get("Paused", True), "Exact running generation differs")
    if running:
        require(state.get("Running") is True and type(state.get("Pid")) is int and state["Pid"] > 0,
                "Actual running daemon required")
    config = container.get("Config", {})
    require(config.get("Image") == lease.image_reference, "Container image reference differs")
    require(tuple(config.get("Entrypoint") or ()) == lease.entrypoint
            and tuple(config.get("Cmd") or ()) == lease.arguments, "Container command override differs from reviewed plan")
    labels = config.get("Labels", {})
    require(all(labels.get(k) == v for k,v in context.labels(lease.role).items()), "Resource ownership differs")
    require(image.get("Id") == container.get("Image") and lease.image_reference in image.get("RepoDigests", []),
            "Actual image config/repository digest differs")
    host = container.get("HostConfig", {})
    require(host.get("Privileged") is False and host.get("ReadonlyRootfs") is True
            and "ALL" in (host.get("CapDrop") or []) and not host.get("CapAdd")
            and "no-new-privileges:true" in (host.get("SecurityOpt") or []), "Isolation flags missing")
    require(type(host.get("Memory")) is int and 0 < host["Memory"] <= 4*1024**3
            and type(host.get("NanoCpus")) is int and 0 < host["NanoCpus"] <= 2_000_000_000,
            "Finite memory/CPU limits required")
    require(host.get("RestartPolicy", {}).get("Name") == "no" and not host.get("AutoRemove"),
            "Explicit finite resource lifetime required")
    require(host.get("NetworkMode") == lease.network_id and host.get("PidMode") == ""
            and host.get("IpcMode") == "private" and host.get("CgroupnsMode") == "private",
            "Host/foreign namespace sharing denied")
    tmpfs = host.get("Tmpfs") or {}
    require(all(path in ("/tmp", "/run", "/data") and "size=" in options for path,options in tmpfs.items()),
            "Only bounded reviewed disposable tmpfs paths admitted")
    mounts = container.get("Mounts") or []
    require(all(mount.get("Type") == "tmpfs" and mount.get("Destination") in tmpfs
                and not mount.get("Source") and not mount.get("Name") for mount in mounts),
            "Persistent/bind/volume mounts denied")
    published = {f"{lease.container_port}/tcp": [{"HostIp": lease.host_ip, "HostPort": str(lease.host_port)}]}
    require(host.get("PortBindings") == published, "Declared loopback binding differs")
    actual_ports = {k:v for k,v in container.get("NetworkSettings", {}).get("Ports", {}).items() if v is not None}
    require(actual_ports == published, "Actual loopback publication differs")
    networks = container.get("NetworkSettings", {}).get("Networks", {})
    require(len(networks) == 1 and next(iter(networks.values())).get("NetworkID") == lease.network_id,
            "Foreign network attachment denied")
    require(network.get("Id") == lease.network_id and network.get("Internal") is True
            and network.get("Driver") == "bridge", "Dedicated internal bridge required")
    require(instant(network.get("Created")) >= instant(context.issued_utc)
            and instant(network.get("Created")) <= now, "Foreign network creation denied")
    network_labels = network.get("Labels", {})
    require(all(network_labels.get(k) == v for k,v in context.labels(lease.role).items()
                if k != LABEL_PREFIX+"role"), "Network ownership differs")
    require(instant(lease.created_utc) >= instant(context.issued_utc) and instant(lease.created_utc) <= now,
            "No earlier or future resource creation admitted")
    return {"kind":"container", "containerId":lease.container_id,
            "imageDigest":lease.image_reference.rsplit("@",1)[1], "createdUtc":lease.created_utc,
            "ownershipLabels":context.labels(lease.role), "hostIp":lease.host_ip,
            "hostPort":lease.host_port, "containerPort":lease.container_port}


_FAILED_DISCOVERY_QUERIES = []
_PROC_INVENTORIES = []
_HANDLED_DISCOVERY_QUERIES = []


def handled_discovery_queries():
    """Readonly index/category projection, not a supplied proof capability."""
    return tuple(_HANDLED_DISCOVERY_QUERIES)


def _settled_command(row):
    positive = ('returnedProcessObserved', 'generationBound', 'originalReaped', 'bothReadersEof',
                'bothReadersClosed', 'selectorCloseCompleted', 'pidfdCloseCompleted', 'cleanupVerified')
    return (all(row.get(key) is True for key in positive) and row.get('quarantined') is False
            and row.get('attemptLedgerCapped') is False)


def _discovery_probe(arguments):
    if len(arguments) not in (6, 7) or arguments[:3] != ('docker', 'container', 'exec'):
        return None
    if HEX64.fullmatch(arguments[3]) is None:
        return None
    tail = arguments[4:]
    path = None
    if len(tail) == 2 and tail[0] == 'cat':
        path = re.fullmatch(r'/proc/([1-9][0-9]*)/stat', tail[1])
    elif len(tail) == 3 and tail[:2] == ('ls', '-1'):
        path = re.fullmatch(r'/proc/([1-9][0-9]*)/fd', tail[2])
    elif len(tail) == 2 and tail[0] == 'readlink':
        path = re.fullmatch(r'/proc/([1-9][0-9]*)/fd/(?:0|[1-9][0-9]*)', tail[1])
    return (arguments[3], path[1]) if path else None


def _record_handled_discovery(error, container_id, pid, matched, current_bytes):
    import scanner_docker_command as owner
    failed = next((row for row in _FAILED_DISCOVERY_QUERIES if row[0] is error), None)
    if failed is None:
        # Original semantic predicate refusal has no command failure to relabel.
        return
    _, failed_index, arguments = failed
    require(_discovery_probe(arguments) == (container_id, pid) and matched is False,
            'Exact unmatched read-only discovery query required')
    inventory = next((row for row in _PROC_INVENTORIES if row[0] is current_bytes), None)
    require(inventory is not None, 'Actual held fresh process inventory required')
    _, inventory_index, inventory_arguments = inventory
    require(inventory_arguments == ('docker', 'container', 'exec', container_id, 'ls', '-1', '/proc')
            and inventory_index > failed_index, 'Exact later container inventory required')
    entries = current_bytes.decode('ascii').splitlines()
    require(any(re.fullmatch(r'[1-9][0-9]*', entry) for entry in entries) and pid not in entries,
            'Actual positive inventory must show probe absent')
    ledger = owner.command_receipts()
    require(0 <= failed_index < inventory_index < len(ledger)
            and _settled_command(ledger[failed_index]) and ledger[failed_index].get('originalFailure') is True
            and type(ledger[failed_index].get('originalExitCode')) is int
            and ledger[failed_index]['originalExitCode'] > 0
            and _settled_command(ledger[inventory_index]) and ledger[inventory_index].get('originalFailure') is False
            and type(ledger[inventory_index].get('originalExitCode')) is int
            and ledger[inventory_index]['originalExitCode'] == 0,
            'Actual settled failed probe and successful absence ledger required')
    require(len(_HANDLED_DISCOVERY_QUERIES) < 256
            and all(row[0] != failed_index for row in _HANDLED_DISCOVERY_QUERIES), 'Discovery association reused or capped')
    _HANDLED_DISCOVERY_QUERIES.append((failed_index, inventory_index, 'vanished-nonowner-read-query'))


def command(arguments, timeout=10, maximum=262144):
    """Stream bounded pipes; retain and reap the exact direct child before returning."""
    require(sys.platform == "linux", "Dedicated hosted Linux launcher required")
    require(0 < timeout <= 60 and type(maximum) is int and 0 < maximum <= 1048576,
            "Finite helper time/output limits required")
    require(type(arguments) in (list, tuple) and bool(arguments) and all(type(value) is str for value in arguments),
            "Explicit command argument vector required")
    if arguments[0] in ("docker", "git"):
        import scanner_docker_command as owner
        before = len(owner.command_receipts())
        try:
            if arguments[0] == "docker":
                output = owner.run_docker_bytes(arguments[1:], timeout=timeout)
            else:
                output = owner.run_git_bytes(arguments[1:], timeout=timeout)
        except subprocess.CalledProcessError:
            # Preserve original nonzero AdmissionError category. Original failed
            # command ledger remains intact; unsettled lifecycle is NOT mapped.
            error = AdmissionError("Resource observation/action failed; details retained separately")
            ledger = owner.command_receipts()
            arguments_tuple = tuple(arguments)
            if (_discovery_probe(arguments_tuple) is not None and len(ledger) == before + 1
                    and _settled_command(ledger[before]) and len(_FAILED_DISCOVERY_QUERIES) < 256):
                _FAILED_DISCOVERY_QUERIES.append((error, before, arguments_tuple))
            raise error from None
        require(type(output) is bytes and len(output) <= maximum, "Oversized observer response")
        arguments_tuple = tuple(arguments)
        if (len(arguments_tuple) == 7 and arguments_tuple[:3] == ('docker', 'container', 'exec')
                and arguments_tuple[4:] == ('ls', '-1', '/proc') and HEX64.fullmatch(arguments_tuple[3])):
            ledger = owner.command_receipts()
            require(len(ledger) == before + 1 and len(_PROC_INVENTORIES) < 256,
                    'Actual bounded inventory observation association required')
            _PROC_INVENTORIES.append((output, before, arguments_tuple))
        return output
    # NonDocker/nonGit legacy branch remains under its separate lifetime owner.
    process = None
    selector = selectors.DefaultSelector()
    try:
        process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=False)
        # The unreaped direct child remains owned by this Popen; its PID cannot be reused.
        output = {process.stdout: bytearray(), process.stderr: bytearray()}
        for stream in output:
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ)
        deadline = time.monotonic() + timeout
        while selector.get_map():
            remaining = deadline-time.monotonic()
            require(remaining > 0, "Owned helper deadline expired")
            for key, _ in selector.select(min(remaining, 0.1)):
                stream = key.fileobj
                # Read at most one byte beyond its cap; never buffer an unbounded reply.
                try: block = os.read(stream.fileno(), min(65536, maximum-len(output[stream])+1))
                except BlockingIOError: continue
                if not block:
                    selector.unregister(stream)
                    continue
                require(len(output[stream])+len(block) <= maximum, "Oversized observer response")
                output[stream].extend(block)
        remaining = deadline-time.monotonic()
        require(remaining > 0, "Owned helper deadline expired")
        process.wait(timeout=remaining)
        require(process.returncode == 0, "Resource observation/action failed; details retained separately")
        return bytes(output[process.stdout])
    except (OSError, subprocess.TimeoutExpired):
        raise AdmissionError("Bounded owned helper failed or timed out") from None
    finally:
        selector.close()
        if process is not None:
            try:
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        process.kill(); process.wait(timeout=5)
                require(process.poll() is not None, "Exact helper cleanup did not exit")
            finally:
                for stream in (process.stdout, process.stderr):
                    if stream is not None: stream.close()


def docker_one(kind, identity):
    result = exact_json(command(["docker", kind, "inspect", identity]))
    require(isinstance(result, list) and len(result) == 1 and isinstance(result[0], dict), "One exact engine observation required")
    return result[0]


def observe(context, lease, environment, now, cleanup=False):
    context.validate(environment, now, cleanup)
    lease.validate()
    container = docker_one("container", lease.container_id)
    image_id = container.get("Image", "")
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", image_id) is not None, "Actual image ID missing")
    image = docker_one("image", image_id)
    network = docker_one("network", lease.network_id)
    return validate_observations(context, lease, container, image, network, now, running=not cleanup)


def connected_clients(tables, host_ip, host_port):
    """Inspect established host TCP clients immediately before stopping an admitted disposable daemon."""
    wanted = ipaddress.ip_address(host_ip)
    def endpoint(encoded):
        address, number = encoded.split(":")
        raw = bytes.fromhex(address)
        require(len(raw) in (4,16), "Kernel TCP address shape differs")
        raw = b"".join(raw[i:i+4][::-1] for i in range(0,len(raw),4))
        value = ipaddress.ip_address(raw)
        if isinstance(value, ipaddress.IPv6Address) and value.ipv4_mapped:
            value = value.ipv4_mapped
        return value, int(number,16)
    for table in tables:
        lines = table.splitlines()
        require(lines and "local_address" in lines[0], "Kernel TCP table unavailable")
        for line in lines[1:]:
            fields = line.split()
            require(len(fields) >= 4, "Kernel TCP row malformed")
            if fields[3] == "01" and (endpoint(fields[1]) == (wanted,host_port)
                                       or endpoint(fields[2]) == (wanted,host_port)):
                return True
    return False


def require_unused(lease):
    require(not connected_clients([Path("/proc/net/tcp").read_text(), Path("/proc/net/tcp6").read_text()],
                                  lease.host_ip, lease.host_port), "Active local client; preserve resource")


def cleanup_owned(context, lease, environment, now):
    """Caller invokes finally only after all eight owned clients/hosts have exited. No broad cleanup."""
    observe(context, lease, environment, now, cleanup=True)
    require_unused(lease)
    command(["docker", "container", "stop", "--time", "10", lease.container_id], timeout=20)
    observe(context, lease, environment, datetime.now(timezone.utc), cleanup=True)
    stopped = docker_one("container", lease.container_id)
    require(stopped.get("State", {}).get("Running") is False and stopped.get("State", {}).get("Pid") == 0,
            "Graceful stop did not exit; preserve and report exact resource")
    require_unused(lease)
    command(["docker", "container", "rm", lease.container_id])
    # A daemon failure is not proof of absence. Positive complete engine inventory must omit this exact ID.
    inventory = command(["docker", "container", "ls", "--all", "--no-trunc", "--format", "{{.ID}}"]).decode("ascii").splitlines()
    require(all(HEX64.fullmatch(value) is not None for value in inventory) and lease.container_id not in inventory,
            "Removed container absence could not be verified")


def clamav_record(host, number, request):
    require(host in ("127.0.0.1", "::1"), "Literal scanner origin required")
    port(number)
    deadline = time.monotonic()+5
    with socket.create_connection((host,number), timeout=5) as connection:
        connection.settimeout(max(0.001,deadline-time.monotonic()))
        connection.sendall(request)
        response = bytearray()
        while True:
            require(time.monotonic() < deadline, "Scanner response deadline expired")
            connection.settimeout(max(0.001,deadline-time.monotonic()))
            part = connection.recv(4097-len(response))
            require(part and len(response)+len(part) <= 4096, "Missing or oversized scanner response")
            response.extend(part)
            if b"\0" in part:
                require(response.count(0) == 1 and response[-1] == 0, "Ambiguous scanner reply")
                return bytes(response[:-1]).decode("ascii")


def stream_request(payload):
    require(isinstance(payload, bytes) and len(payload) <= 4096, "Bounded readiness payload required")
    return b"zINSTREAM\0"+struct.pack("!I",len(payload))+payload+struct.pack("!I",0)


def parse_version(reply):
    match = re.fullmatch(r"ClamAV ([0-9][0-9A-Za-z._-]*)/([1-9][0-9]*)/[^\r\n\x00]{1,100}",reply)
    require(match is not None, "Actual loaded engine/database VERSION required")
    return match[1],match[2]


def database_hash_map(paths, readback):
    expected = {Path(path).name for path in paths}
    require(len(expected) == len(paths), "Duplicate database basename denied")
    result = {}
    for line in readback.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  /var/lib/clamav/((?:main|daily|bytecode)\.(?:cvd|cld))",line)
        require(match is not None and match[2] not in result, "Ambiguous database hash readback")
        result[match[2]] = match[1]
    require(set(result) == expected, "Hash readback differs from actual requested database set")
    return dict(sorted(result.items()))


@dataclass(frozen=True)
class ScannerPlan:
    executable: str
    executable_sha256: str
    argv: tuple
    configuration: str
    configuration_sha256: str
    database_files_sha256: dict
    startup_log: str

    def validate(self):
        require(self.executable.startswith("/") and self.configuration.startswith("/")
                and self.startup_log.startswith("/")
                and all(".." not in Path(p).parts for p in (self.executable,self.configuration,self.startup_log)),
                "Canonical reviewed scanner paths required")
        require(HEX64.fullmatch(self.executable_sha256) is not None
                and HEX64.fullmatch(self.configuration_sha256) is not None,
                "Reviewed scanner executable/configuration hashes required")
        require(self.argv == (self.executable,"--foreground","--config-file="+self.configuration),
                "Exact foreground clamd invocation required")
        require(len(self.database_files_sha256) in (2,3)
                and all(re.fullmatch(r"(?:main|daily|bytecode)\.(?:cvd|cld)",name)
                        and HEX64.fullmatch(digest) for name,digest in self.database_files_sha256.items())
                and len({Path(name).stem for name in self.database_files_sha256}) == len(self.database_files_sha256)
                and {"main","daily"}.issubset({Path(name).stem for name in self.database_files_sha256}),
                "Exact immutable reviewed database manifest required")


def scanner_process(lease, plan):
    """Observe the socket owner inside the container; no supplied process JSON is proof."""
    require(isinstance(plan, ScannerPlan), "Reviewed scanner runtime plan missing")
    plan.validate()
    require(all(not Path(path).is_relative_to(Path(writable))
                for path in (plan.executable,plan.configuration,"/var/lib/clamav")
                for writable in ("/tmp","/run","/data")), "Scanner code/config/database must remain on immutable image root")
    def execute(*args, maximum=262144):
        return command(["docker","container","exec",lease.container_id,*args], maximum=maximum)
    tables = [execute("cat","/proc/net/"+name).decode("ascii") for name in ("tcp","tcp6")]
    matches = []
    for table in tables:
        rows = table.splitlines()
        require(rows and "local_address" in rows[0], "Scanner kernel socket table unavailable")
        for row in rows[1:]:
            fields = row.split()
            require(len(fields) >= 10, "Scanner socket row incomplete")
            if fields[3] == "0A" and int(fields[1].split(":")[1],16) == lease.container_port:
                require(re.fullmatch(r"[1-9][0-9]*",fields[9]), "Scanner listener inode missing")
                matches.append(fields[9])
    require(len(matches) == 1, "One actual scanner listener required")
    inode = matches[0]
    pids = [item for item in execute("ls","-1","/proc").decode("ascii").splitlines()
            if re.fullmatch(r"[1-9][0-9]*",item)]
    owners = []
    for pid in pids:
        matched = False
        try:
            initial = process_start_ticks(execute("cat",f"/proc/{pid}/stat",maximum=4096),int(pid))
            descriptors = execute("ls","-1",f"/proc/{pid}/fd").decode("ascii").splitlines()
            require(all(re.fullmatch(r"[0-9]+",fd) for fd in descriptors), "Unexpected scanner descriptor")
            for fd in descriptors:
                target = execute("readlink",f"/proc/{pid}/fd/{fd}",maximum=4096).decode("ascii").strip()
                if target == "socket:["+inode+"]":
                    matched = True; owners.append((int(pid),initial,fd)); break
        except AdmissionError as discovery_error:
            require(not matched, "Actual scanner listener vanished during discovery")
            # Positive fresh /proc inventory distinguishes exited nonowner helpers from
            # unreadable live state/engine failure. An arbitrary command failure is not absence.
            current_bytes = execute("ls","-1","/proc")
            current = current_bytes.decode("ascii").splitlines()
            require(any(re.fullmatch(r"[1-9][0-9]*",entry) for entry in current) and pid not in current,
                    "Live or uncertain scanner discovery process must not be skipped")
            _record_handled_discovery(discovery_error, lease.container_id, pid, matched, current_bytes)
    require(len(owners) == 1, "Scanner listener ownership ambiguous")
    pid, initial, descriptor = owners[0]
    start = process_start_ticks(execute("cat",f"/proc/{pid}/stat",maximum=4096),pid)
    require(start == initial, "Scanner listener generation changed during discovery")
    executable = execute("readlink",f"/proc/{pid}/exe",maximum=4096).decode("ascii").strip()
    require(executable == plan.executable, "Actual listener is not the reviewed clamd executable")
    argv = execute("cat",f"/proc/{pid}/cmdline",maximum=8192).decode("utf-8").split("\x00")
    require(argv[-1] == "" and tuple(argv[:-1]) == plan.argv, "Actual clamd arguments differ")
    executable_hash = execute("sha256sum",f"/proc/{pid}/exe").decode("ascii").split()
    require(executable_hash == [plan.executable_sha256,f"/proc/{pid}/exe"], "Actual clamd binary digest differs")
    require(execute("readlink",f"/proc/{pid}/root",maximum=4096).decode("ascii").strip() == "/",
            "Scanner process changed filesystem root")
    require(execute("readlink","-f",plan.configuration,maximum=4096).decode("ascii").strip() == plan.configuration,
            "Reviewed configuration must not redirect to writable storage")
    config = execute("cat",f"/proc/{pid}/root"+plan.configuration,maximum=65536)
    require(hashlib.sha256(config).hexdigest() == plan.configuration_sha256, "Consumed clamd configuration differs")
    directives = [line.split() for line in config.decode("ascii").splitlines()
                  if line.strip() and not line.lstrip().startswith("#")]
    for key,value in (("DatabaseDirectory","/var/lib/clamav"),("SelfCheck","0"),("LogFile",plan.startup_log)):
        require([row[1:] for row in directives if row[0] == key] == [[value]], "Reviewed clamd directive differs: "+key)
    require(not any(row[0] == "Include" for row in directives), "Unbound clamd configuration include denied")
    log = execute("cat",f"/proc/{pid}/root"+plan.startup_log,maximum=65536).decode("ascii")
    require(any(line.endswith("Reading databases from /var/lib/clamav") for line in log.splitlines()),
            "Actual clamd startup database load path missing")
    require(process_start_ticks(execute("cat",f"/proc/{pid}/stat",maximum=4096),pid) == start,
            "Scanner process generation changed during observation")
    require(execute("readlink",f"/proc/{pid}/fd/{descriptor}",maximum=4096).decode("ascii").strip() == "socket:["+inode+"]",
            "Actual scanner listening descriptor changed during observation")
    return {"pid":pid,"kernelStartTicks":start,"listenerInode":inode,"executable":executable,
            "executableSha256":plan.executable_sha256,"argv":list(plan.argv),
            "configurationSha256":plan.configuration_sha256,"databaseDirectory":"/var/lib/clamav"}


def scanner_readiness(context, lease, environment, now, plan=None):
    require(lease.role == "scanner", "Scanner lease required")
    observe(context,lease,environment,now)
    process = scanner_process(lease,plan)
    version = clamav_record(lease.host_ip,lease.host_port,b"zVERSION\0")
    engine, loaded = parse_version(version)
    benign = clamav_record(lease.host_ip,lease.host_port,stream_request(b"MALIEV hosted readiness benign control\n"))
    # Standard harmless antivirus test string assembled only in memory; no uploaded object or on-disk payload.
    test = b"X5O!P%@AP[4\\PZX54(P^)7CC)7}$"+b"EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
    infected = clamav_record(lease.host_ip,lease.host_port,stream_request(test))
    require(benign == "stream: OK", "Real benign engine control failed")
    require(re.fullmatch(r"stream: [^\r\n\x00]{1,160}Eicar[^\r\n\x00]{0,160} FOUND", infected, re.I) is not None
            or re.fullmatch(r"stream: Eicar[^\r\n\x00]{0,160} FOUND", infected, re.I) is not None,
            "Real EICAR engine control failed")
    directory = command(["docker","container","exec",lease.container_id,"readlink","-f","/var/lib/clamav"]).decode("ascii").strip()
    require(directory == "/var/lib/clamav", "Frozen database directory must not redirect to writable storage")
    paths = command(["docker","container","exec",lease.container_id,"find","/var/lib/clamav","-maxdepth","1","-type","f"]).decode("ascii").splitlines()
    databases = [p for p in paths if p.endswith((".cvd",".cld"))]
    require(len(databases) in (2,3) and all(re.fullmatch(r"/var/lib/clamav/(main|daily|bytecode)\.(cvd|cld)",p) for p in databases),
            "Reviewed frozen database files required")
    require(len({Path(p).stem for p in databases}) == len(databases)
            and {"main","daily"}.issubset({Path(p).stem for p in databases}), "Loaded database set ambiguous")
    hashes = command(["docker","container","exec",lease.container_id,"sha256sum",*sorted(databases)]).decode("ascii")
    file_hashes = database_hash_map(databases,hashes)
    require(file_hashes == plan.database_files_sha256, "Actual database bytes differ from reviewed immutable manifest")
    daily = next(p for p in databases if Path(p).stem == "daily")
    info = command(["docker","container","exec",lease.container_id,"sigtool","--info",daily]).decode("ascii")
    found = re.findall(r"^Version: ([0-9]+)$",info,re.M)
    require(found == [loaded], "Loaded VERSION differs from frozen daily database")
    observe(context,lease,environment,datetime.now(timezone.utc))
    require(scanner_process(lease,plan) == process, "Scanner listener changed during protocol controls")
    receipt = {"runId":context.run_id,"runAttempt":context.attempt,"fileSourceSha":context.file_sha,
               "resourceLeaseId":context.lease_id,"scannerContainerId":lease.container_id,
               "scannerImageDigest":lease.image_reference.rsplit("@",1)[1],"versionReply":version,
               "benignReply":benign,"eicarReply":infected,"databaseHashReadback":hashes,"daemonProcess":process,
               "observedUtc":datetime.now(timezone.utc).isoformat()}
    digest = hashlib.sha256(json.dumps(receipt,sort_keys=True,separators=(",",":")).encode()).hexdigest()
    identity = {"engineVersion":engine,"loadedDatabaseVersion":loaded,
                "databaseFilesSha256":file_hashes,
                "observedUtc":receipt["observedUtc"],"readinessReceiptSha256":digest}
    return identity,receipt


def verify_file_source(context, repository, expected_tree, executable_dll, expected_dll_sha256):
    """Exact source/build prerequisite; caller separately re-observes the actual File PID after launch."""
    require(re.fullmatch(r"[0-9a-f]{40}", expected_tree) is not None and HEX64.fullmatch(expected_dll_sha256) is not None,
            "Reviewed source tree and executable digest required")
    root = Path(repository)
    dll = Path(executable_dll)
    require(root.is_absolute() and dll.is_absolute() and not root.is_symlink() and not dll.is_symlink(),
            "Exact absolute owned source/build paths required")
    require(dll.resolve().is_relative_to(root.resolve()), "File executable escaped exact source/build root")
    head = command(["git","-C",str(root),"rev-parse","--verify","HEAD"]).decode("ascii").strip()
    tree = command(["git","-C",str(root),"rev-parse","--verify","HEAD^{tree}"]).decode("ascii").strip()
    dirty = command(["git","-C",str(root),"status","--porcelain","--untracked-files=all"])
    require(head == context.file_sha and tree == expected_tree and not dirty, "Actual File source/build checkout differs")
    digest = hashlib.sha256()
    with dll.open("rb") as stream:
        count = 0
        for chunk in iter(lambda:stream.read(1024*1024),b""):
            count += len(chunk)
            require(count <= 32*1024*1024, "Unexpected oversized File executable")
            digest.update(chunk)
    require(digest.hexdigest() == expected_dll_sha256, "Actual File executable differs")
    return {"fileSourceSha":head,"fileSourceTree":tree,"fileExecutableSha256":digest.hexdigest()}


def build_file_configuration(context, storage, scanner, environment, now, repository, source_tree, executable_dll, executable_sha256, scanner_plan=None):
    """No startup/write. Observe real prerequisites, then return the agreed section and separate proof receipt."""
    context.validate(environment,now)
    require(storage.role == "storage" and scanner.role == "scanner"
            and storage.container_id != scanner.container_id and storage.host_ip == scanner.host_ip
            and storage.host_port != scanner.host_port and storage.network_id == scanner.network_id,
            "Separate exact storage/scanner resources on one owned isolated network required")
    source = verify_file_source(context,repository,source_tree,executable_dll,executable_sha256)
    storage_identity = observe(context,storage,environment,now)
    scanner_identity = observe(context,scanner,environment,now)
    database_identity, readiness = scanner_readiness(context,scanner,environment,now,scanner_plan)
    completed = datetime.now(timezone.utc)
    context.validate(environment,completed)
    observe(context,storage,environment,completed)
    observe(context,scanner,environment,completed)
    host = storage.host_ip if storage.host_ip == "127.0.0.1" else "[::1]"
    storage_origin = f"http://{host}:{storage.host_port}"
    origin(storage_origin)
    admission = {"schemaVersion":1,"runId":context.run_id,"runAttempt":context.attempt,
                 "fileSourceSha":context.file_sha,"issuedUtc":context.issued_utc,"expiresUtc":context.expires_utc,
                 "storageOrigin":storage_origin,"storageEndpointIdentity":storage_identity,
                 "scannerHost":scanner.host_ip,"scannerPort":scanner.host_port,
                 "scannerContainerId":scanner.container_id,"scannerImageDigest":scanner.image_reference.rsplit("@",1)[1],
                 "scannerDatabaseIdentity":database_identity,"resourceLeaseId":context.lease_id}
    evidence = {"sourceBuild":source,"storageIdentity":storage_identity,"scannerIdentity":scanner_identity,
                "scannerReadiness":readiness,"observedUtc":completed.isoformat(),
                "notProven":["actual File process/startup","actual File ClamAvFileSafetyScanner unavailable-path controls",
                             "actual SDK storage protocol/object/generation/signed URL","eight-host financial join","resource cleanup"]}
    return {SECTION:{"Enabled":True,"Admission":admission}},evidence


def configuration_environment(configuration):
    """Normal hierarchical configuration consumption, no CLI override or private signer material."""
    require(set(configuration) == {SECTION} and set(configuration[SECTION]) == {"Enabled","Admission"},
            "Exact agreed profile section required")
    result = {}
    def flatten(value, parts):
        if isinstance(value,dict):
            for key,child in value.items():
                require(isinstance(key,str) and key and ":" not in key and "__" not in key, "Configuration alias denied")
                flatten(child,[*parts,key])
        else:
            require(type(value) in (str,int,bool), "Only exact configuration scalars admitted")
            result["__".join(parts)] = str(value).lower() if type(value) is bool else str(value)
    flatten(configuration,[])
    return result


@dataclass(frozen=True)
class FileProcessLease:
    pid: int
    start_ticks: int
    executable_dll: str
    executable_sha256: str
    dotnet_executable: str
    dotnet_sha256: str
    host_ip: str
    host_port: int


def process_start_ticks(data, expected_pid):
    require(isinstance(data,bytes) and len(data) <= 4096, "Bounded kernel process identity required")
    try:
        prefix,_,tail = data.rpartition(b")")
        pid_text,_,_ = prefix.partition(b" (")
        fields = tail.split()
        require(int(pid_text) == expected_pid and len(fields) >= 20 and fields[0] not in (b"Z",b"X"),
                "Live exact process kernel identity required")
        return int(fields[19])  # /proc/PID/stat field22, after comm/state
    except (ValueError,IndexError):
        raise AdmissionError("Malformed kernel process identity") from None


def bounded_file(path, maximum):
    with Path(path).open("rb") as stream:
        data = stream.read(maximum+1)
    require(len(data) <= maximum, "Oversized process observation")
    return data


def process_environment(data):
    result = {}
    for entry in data.split(b"\0"):
        if not entry: continue
        key,separator,value = entry.partition(b"=")
        require(separator and key, "Malformed process environment")
        name = key.decode("utf-8"); text = value.decode("utf-8")
        require(name not in result, "Repeated process environment key")
        result[name] = text
    return result


def consumed(environment, key, expected):
    aliases = [name for name in environment if name.replace(":","__").lower() == key.lower()]
    require(aliases == [key] and environment[key] == expected, "Actual consumed process key differs or is aliased")


def listening_inode(tables, host_ip, host_port):
    expected = ipaddress.ip_address(host_ip)
    matches = []
    for table in tables:
        lines = table.splitlines()
        require(lines and "local_address" in lines[0], "Kernel listening table unavailable")
        for line in lines[1:]:
            fields = line.split()
            require(len(fields) >= 10, "Kernel socket identity row incomplete")
            if fields[3] != "0A": continue
            address,number = fields[1].split(":")
            raw = bytes.fromhex(address)
            require(len(raw) in (4,16), "Kernel listener address shape differs")
            raw = b"".join(raw[i:i+4][::-1] for i in range(0,len(raw),4))
            if ipaddress.ip_address(raw) == expected and int(number,16) == host_port:
                require(re.fullmatch(r"[1-9][0-9]*",fields[9]) is not None, "Actual listener inode required")
                matches.append(fields[9])
    require(len(matches) == 1, "Exactly one literal loopback listener required")
    return matches[0]


def observe_file_process(context, lease, environment, now):
    """Independently verify exact live File kernel generation, DLL/executable, consumed profile and listening FD."""
    context.validate(environment,now)
    require(sys.platform == "linux" and type(lease.pid) is int and lease.pid > 0
            and type(lease.start_ticks) is int and lease.start_ticks > 0,
            "Exact live hosted File process lease required")
    require(lease.host_ip in ("127.0.0.1","::1"), "File API loopback origin required"); port(lease.host_port)
    dll = Path(lease.executable_dll); dotnet = Path(lease.dotnet_executable)
    require(dll.is_absolute() and dotnet.is_absolute() and not dll.is_symlink() and not dotnet.is_symlink()
            and HEX64.fullmatch(lease.executable_sha256) is not None and HEX64.fullmatch(lease.dotnet_sha256) is not None,
            "Actual absolute File executable identities required")
    proc = Path(f"/proc/{lease.pid}")
    first = process_start_ticks(bounded_file(proc/"stat",4096),lease.pid)
    require(first == lease.start_ticks, "File PID reused or generation differs")
    args = bounded_file(proc/"cmdline",65536).decode("utf-8").rstrip("\0").split("\0")
    actual_executable = os.readlink(proc/"exe")
    require(len(args) == 2 and args[0] in ("dotnet",lease.dotnet_executable)
            and args[1] == lease.executable_dll and actual_executable == lease.dotnet_executable,
            "Actual File executable/arguments differ")
    actual = process_environment(bounded_file(proc/"environ",262144))
    consumed(actual,"ASPNETCORE_ENVIRONMENT",ENVIRONMENT)
    if any(key.lower() == "dotnet_environment" for key in actual): consumed(actual,"DOTNET_ENVIRONMENT",ENVIRONMENT)
    consumed(actual,"GITHUB_ACTIONS","true"); consumed(actual,"GITHUB_RUN_ID",context.run_id)
    consumed(actual,"GITHUB_RUN_ATTEMPT",str(context.attempt))
    consumed(actual,"C821_FIXTURE_RUN_ID",context.lease_id)
    consumed(actual,"C821_FIXTURE_EXPIRES_UTC",context.expires_utc)
    consumed(actual,SECTION+"__Enabled","true")
    consumed(actual,SECTION+"__Admission__fileSourceSha",context.file_sha)
    consumed(actual,SECTION+"__Admission__resourceLeaseId",context.lease_id)
    consumed(actual,SECTION+"__Admission__expiresUtc",context.expires_utc)
    host = lease.host_ip if lease.host_ip == "127.0.0.1" else "[::1]"
    consumed(actual,"ASPNETCORE_URLS",f"http://{host}:{lease.host_port}")
    require(hashlib.sha256(bounded_file(dll,32*1024*1024)).hexdigest() == lease.executable_sha256
            and hashlib.sha256(bounded_file(dotnet,32*1024*1024)).hexdigest() == lease.dotnet_sha256,
            "Actual loaded executable file digests differ")
    tables = [Path("/proc/net/tcp").read_text(),Path("/proc/net/tcp6").read_text()]
    inode = listening_inode(tables,lease.host_ip,lease.host_port)
    sockets = []
    for descriptor in (proc/"fd").iterdir():
        try: target = os.readlink(descriptor)
        except FileNotFoundError: continue
        if target.startswith("socket:["): sockets.append(target)
    require("socket:["+inode+"]" in sockets, "Actual loopback listener does not belong to the File process")
    final = process_start_ticks(bounded_file(proc/"stat",4096),lease.pid)
    require(final == lease.start_ticks, "File process changed during observation")
    context.validate(environment,datetime.now(timezone.utc))
    return {"pid":lease.pid,"kernelStartTicks":lease.start_ticks,"executableDll":lease.executable_dll,
            "executableSha256":lease.executable_sha256,"dotnetExecutable":lease.dotnet_executable,
            "dotnetSha256":lease.dotnet_sha256,"hostIp":lease.host_ip,"hostPort":lease.host_port,
            "listeningInode":inode}


def parse_signing_handshake(data):
    value = exact_json(data)
    require(isinstance(value,dict) and set(value) == {"Algorithm","PublicKey"}
            and value["Algorithm"] == "GOOG4-RSA-SHA256" and isinstance(value["PublicKey"],str)
            and len(value["PublicKey"]) <= 8192, "Exact actual File PascalCase public-key handshake required")
    try: der = base64.b64decode(value["PublicKey"],validate=True)
    except ValueError: raise AdmissionError("Canonical public SPKI base64 required") from None
    require(256 <= len(der) <= 4096 and der[0] == 0x30 and base64.b64encode(der).decode("ascii") == value["PublicKey"],
            "Bounded canonical public SPKI material required")
    # The actual observed File implementation exports RSA SPKI. This transport parser is not a crypto verifier;
    # the admitted storage endpoint must import this public key and verify actual SDK V4 signatures separately.
    return value,hashlib.sha256(der).hexdigest()


def remaining_time(deadline):
    value = deadline-time.monotonic()
    require(value > 0, "Actual File handshake total deadline expired")
    return value


class DeadlineRaw(io.RawIOBase):
    """Each underlying receive consumes one total budget, including HTTP header/body drips."""
    def __init__(self, raw, sock, deadline):
        super().__init__(); self.raw=raw; self.sock=sock; self.deadline=deadline

    def readable(self): return True

    def readinto(self, buffer):
        self.sock.settimeout(remaining_time(self.deadline))
        count=self.raw.readinto(buffer)
        remaining_time(self.deadline)
        return count

    def close(self):
        try: self.raw.close()
        finally: super().close()


class DeadlineSocket:
    def __init__(self, sock, deadline): self.sock=sock; self.deadline=deadline

    def sendall(self, data):
        self.sock.settimeout(remaining_time(self.deadline)); self.sock.sendall(data)
        remaining_time(self.deadline)

    def makefile(self, mode):
        require(mode == "rb", "Only bounded binary HTTP response stream admitted")
        return io.BufferedReader(DeadlineRaw(self.sock.makefile("rb",buffering=0),self.sock,self.deadline))

    def close(self): self.sock.close()


class DeadlineHTTPConnection(http.client.HTTPConnection):
    def __init__(self, host, port, deadline):
        super().__init__(host,port,timeout=remaining_time(deadline)); self.deadline=deadline

    def connect(self):
        # Literal loopback connect bypasses DNS; send/header/body share this one deadline.
        require(self.host in ("127.0.0.1","::1") and self._tunnel_host is None, "Direct literal handshake only")
        sock=socket.socket(socket.AF_INET if self.host == "127.0.0.1" else socket.AF_INET6,socket.SOCK_STREAM)
        try:
            sock.settimeout(remaining_time(self.deadline)); sock.connect((self.host,self.port))
            remaining_time(self.deadline); self.sock=DeadlineSocket(sock,self.deadline)
        except BaseException: sock.close(); raise


def deadline_http_connection(host, port, deadline): return DeadlineHTTPConnection(host,port,deadline)


def read_actual_signing_key(context, lease, environment, now, bearer_token):
    """Authenticated fixed actual File route; no proxy/redirect, public key only, identity observed before/after."""
    require(isinstance(bearer_token,str) and len(bearer_token) <= 65536
            and re.fullmatch(r"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",bearer_token) is not None,
            "Actual ordinary raw Auth-issued JWT required; never persist it")
    before = observe_file_process(context,lease,environment,now)
    lease_remaining = (instant(context.expires_utc)-datetime.now(timezone.utc)).total_seconds()
    require(lease_remaining > 0, "Actual File handshake lease expired before transport")
    deadline = time.monotonic()+min(5,lease_remaining)
    connection = deadline_http_connection(lease.host_ip,lease.host_port,deadline)
    response = None
    try:
        connection.request("GET","/file/acceptance/signing-key",
                           headers={"Authorization":"Bearer "+bearer_token,"Accept":"application/json","Connection":"close"})
        response = connection.getresponse()
        remaining_time(deadline)
        require(response.status == 200, "Actual authenticated File key handshake failed or lease expired")
        require(response.getheader("Content-Type") in ("application/json","application/json; charset=utf-8")
                and response.getheader("Content-Encoding") is None, "Exact public-key JSON transport required")
        length = response.getheader("Content-Length")
        require(length is None or re.fullmatch(r"[0-9]{1,5}",length) is not None and int(length) <= 8192,
                "Bounded public-key response length required")
        body = response.read(8193); require(len(body) <= 8192, "Oversized public-key response")
        remaining_time(deadline)
        value,fingerprint = parse_signing_handshake(body)
    except (OSError,http.client.HTTPException):
        raise AdmissionError("Bounded actual File handshake transport failed") from None
    finally:
        try:
            if response is not None: response.close()
        finally: connection.close()
    after = observe_file_process(context,lease,environment,datetime.now(timezone.utc))
    require(before == after, "File identity changed during public-key handshake")
    return value,{"runId":context.run_id,"runAttempt":context.attempt,"fileSourceSha":context.file_sha,
                  "resourceLeaseId":context.lease_id,"fileProcess":after,"route":"/file/acceptance/signing-key",
                  "publicSpkiSha256":fingerprint,"bodySha256":hashlib.sha256(body).hexdigest(),
                  "observedUtc":datetime.now(timezone.utc).isoformat(),
                  "notProven":"SDK V4 signature verification/object bytes; endpoint must prove separately"}
