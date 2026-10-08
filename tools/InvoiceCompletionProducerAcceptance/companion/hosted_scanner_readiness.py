"""Disposable hosted ClamAV measurement. Import Scanner for the real caller.

The CLI proves scanner controls only; it cannot certify the financial workflow.
No local execution is required. Docker commands are bounded and shell-free.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import tempfile
import threading
import time
import uuid
import selectors
import ipaddress
from scanner_loopback_relay import LoopbackRelay, StartupPingRefused
from scanner_docker_command import run_docker
from types import SimpleNamespace
from datetime import datetime, timezone

IMAGE = "clamav/clamav@sha256:7659dcb0db47941d3cf8336af84bbb63c7e70b76fc00601774358412b42ed186"
CONFIG = """Foreground yes
TCPSocket 3310
TCPAddr 0.0.0.0
DatabaseDirectory /var/lib/clamav
PidFile /tmp/acceptance-clamd.pid
LogFile /tmp/acceptance-clamd.log
LogTime yes
StreamMaxLength 200M
MaxScanSize 200M
MaxFileSize 200M
SelfCheck 0
"""
CLEAN = b"Synthetic disposable financial acceptance clean scanner control.\n"
# Construct the harmless standard test signature in memory; no test file persists.
EICAR = b"X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + b"EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"


def digest(value):
    return hashlib.sha256(value).hexdigest()


def no_docker_publication(container):
    host = container.get("HostConfig")
    settings = container.get("NetworkSettings")
    if not isinstance(host, dict) or not isinstance(settings, dict) or "PortBindings" not in host or "PublishAllPorts" not in host or "Ports" not in settings:
        raise ValueError("Actual Docker publication fields required")
    declared = host["PortBindings"]
    actual = settings["Ports"]
    if host["PublishAllPorts"] is not False or declared not in (None, {}) or not (actual is None or isinstance(actual, dict)):
        raise ValueError("Configured Docker port publication denied")
    if actual is not None and any(mapping is not None for mapping in actual.values()):
        raise ValueError("Actual Docker port publication denied")
    return {"declaredPortBindings": declared, "publishAllPorts": host["PublishAllPorts"],
            "actualPortBindings": actual, "dockerPortPublicationObserved": False,
            "absenceVerified": True}


def observed_runtime_policy(container):
    """Strict Engine configuration observation; not a kernel cgroup/capability proof."""
    host = container.get("HostConfig")
    keys = ("Memory", "NanoCpus", "CapDrop", "CapAdd", "ReadonlyRootfs")
    if not isinstance(host, dict) or any(key not in host for key in keys):
        raise ValueError("Actual Docker resource policy fields required")
    if type(host["Memory"]) is not int or host["Memory"] != 1536 * 1024 * 1024:
        raise ValueError("Observed Docker memory limit differs")
    if type(host["NanoCpus"]) is not int or host["NanoCpus"] != 2_000_000_000:
        raise ValueError("Observed Docker CPU limit differs")
    if type(host["CapDrop"]) is not list or host["CapDrop"] != ["ALL"]:
        raise ValueError("Observed Docker dropped capabilities differ")
    if host["CapAdd"] is not None and (type(host["CapAdd"]) is not list or host["CapAdd"] != []):
        raise ValueError("Observed Docker added capabilities denied")
    if host["ReadonlyRootfs"] is not True:
        raise ValueError("Observed Docker writable root denied")
    return {"memoryBytes": host["Memory"], "nanoCpus": host["NanoCpus"],
            "capDrop": list(host["CapDrop"]), "capAdd": host["CapAdd"],
            "readOnlyRoot": host["ReadonlyRootfs"], "engineConfigurationObserved": True,
            "kernelEnforcementObserved": False}


def database_hashes(output):
    rows = []
    for line in output.splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  (/var/lib/clamav/[^\s]+\.(?:cvd|cld))", line)
        if not match:
            raise ValueError("Unexpected scanner database measurement")
        rows.append({"path": match[2], "sha256": match[1]})
    if not rows or len({r["path"] for r in rows}) != len(rows):
        raise ValueError("Scanner databases absent or duplicated")
    return sorted(rows, key=lambda row: row["path"])


CONFIGURED_CENSUS_NETWORKS = 32
CONFIGURED_CENSUS_ITEM_BYTES = 8192
CONFIGURED_CENSUS_TOTAL_BYTES = 131072
CONFIGURED_CENSUS_SECONDS = 15
CONFIGURED_SUBNET_CANDIDATES = tuple(str(ipaddress.IPv4Network(
    (int(ipaddress.IPv4Address('10.253.240.0')) + index * 16, 28)))
    for index in range(16))


def configured_network_census(docker):
    """Read-only bounded Engine snapshot; atomic create remains conflict authority."""
    deadline = time.monotonic() + CONFIGURED_CENSUS_SECONDS
    aggregate = 0

    def observed(*args):
        nonlocal aggregate
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError('Configured network census deadline expired')
        value = docker(*args, timeout=min(3, remaining))
        if time.monotonic() >= deadline:
            raise TimeoutError('Configured network census deadline expired')
        if type(value) is not str:
            raise ValueError('Configured network census output type differs')
        size = len(value.encode('utf-8', 'strict'))
        aggregate += size
        if size > CONFIGURED_CENSUS_ITEM_BYTES or aggregate > CONFIGURED_CENSUS_TOTAL_BYTES:
            raise ValueError('Configured network census output bound exceeded')
        return value

    def listed():
        value = observed('network', 'ls', '--no-trunc', '--format', '{{.ID}}')
        rows = value.splitlines()
        if (len(rows) > CONFIGURED_CENSUS_NETWORKS or len(set(rows)) != len(rows)
                or any(re.fullmatch('[0-9a-f]{64}', row) is None for row in rows)):
            raise ValueError('Configured network census identity differs')
        return tuple(sorted(rows))

    ids = listed()
    subnets = []
    for network_id in ids:
        rows = json.loads(observed('network', 'inspect', network_id))
        if type(rows) is not list or len(rows) != 1 or type(rows[0]) is not dict or rows[0].get('Id') != network_id:
            raise ValueError('Configured network census inspect identity differs')
        ipam = rows[0].get('IPAM')
        if type(ipam) is not dict:
            raise ValueError('Configured network census IPAM shape differs')
        config = ipam.get('Config')
        if type(config) is not list:
            if 'Config' not in ipam:
                raise ValueError('Configured network census IPAM shape differs')
            if config is None:
                raise ValueError('Configured network census IPAM shape differs')
            raise ValueError('Configured network census IPAM shape differs')
        if len(ipam['Config']) > 8:
            raise ValueError('Configured network census IPAM shape differs')
        driver = rows[0].get('Driver')
        if type(driver) is not str or re.fullmatch('[a-zA-Z0-9_.-]{1,64}', driver) is None:
            raise ValueError('Configured network census driver differs')
        if not ipam['Config'] and driver not in ('host', 'null'):
            raise ValueError('Configured network census unknown empty IPAM refused')
        seen = set()
        for row in ipam['Config']:
            if type(row) is not dict or type(row.get('Subnet')) is not str:
                raise ValueError('Configured network census subnet shape differs')
            subnet = ipaddress.ip_network(row['Subnet'], strict=True)
            if str(subnet) != row['Subnet'] or str(subnet) in seen:
                raise ValueError('Configured network census subnet differs')
            seen.add(str(subnet))
            for name in ('Gateway', 'IPRange'):
                value = row.get(name)
                if value is not None and value != '':
                    if type(value) is not str:
                        raise ValueError('Configured network census IPAM value differs')
                    if name == 'Gateway':
                        address = ipaddress.ip_address(value)
                        if str(address) != value or address.version != subnet.version or address not in subnet:
                            raise ValueError('Configured network census gateway differs')
                    else:
                        address_range = ipaddress.ip_network(value, strict=True)
                        if str(address_range) != value or address_range.version != subnet.version or not address_range.subnet_of(subnet):
                            raise ValueError('Configured network census address range differs')
            if subnet.version == 4:
                subnets.append(subnet)
    if listed() != ids:
        raise ValueError('Configured network census changed before selection')
    for value in CONFIGURED_SUBNET_CANDIDATES:
        subnet = ipaddress.IPv4Network(value, strict=True)
        if (str(subnet) != value or subnet.prefixlen != 28 or subnet.num_addresses != 16
                or not any(subnet.subnet_of(ipaddress.IPv4Network(private))
                           for private in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16'))):
            raise ValueError('Configured network source candidate differs')
        if not any(subnet.overlaps(existing) for existing in subnets):
            return value, str(subnet.network_address + 1)
    raise ValueError('Configured network finite candidates exhausted')



class Scanner:
    def __init__(self, run_id=None, deadline_seconds=180, configured_network=False):
        self.run_id = str(uuid.uuid4()) if run_id is None else run_id
        if not re.fullmatch(r"[a-zA-Z0-9-]{1,64}", self.run_id):
            raise ValueError("Invalid owned run identity")
        if not 1 <= deadline_seconds <= 300:
            raise ValueError("Readiness must have a finite deadline")
        if type(configured_network) is not bool:
            raise ValueError("Configured network option must be boolean")
        self.configured_network = configured_network
        self._configured_subnet = None
        self._configured_network_created = None
        self._configured_network_failed = False
        self._configured_network_observed = False
        self.name = "financial-scanner-" + self.run_id
        self.deadline_seconds = deadline_seconds
        self.port = None
        self.container_id = None
        self.image_id = None
        self.network_id = None
        self.relay = None
        self.backend_identity = None
        self.temp = None
        self.attempted = self.clean = self.infected = 0
        self.receipt = {"schemaVersion": 1, "runId": self.run_id, "imageReference": IMAGE,
                        "genuineEightHostFinancialAccepted": False, "resources": [],
                        "scannerReady": False, "cleanupVerified": False}

    def validate_configured_network(self, network, *, empty=False, cleanup=False):
        """Separate exact ownership from use-policy; cleanup never needs readiness."""
        try:
            if not self.configured_network or self._configured_subnet is None or type(network) is not dict:
                raise ValueError('Original configured network plan required')
            created = datetime.fromisoformat(network['Created'].replace('Z', '+00:00'))
            issued = datetime.fromisoformat(self.receipt['network']['allocationIssuedUtc'])
            if (network.get('Id') != self.network_id or network.get('Name') != self.name + '-network'
                    or type(network.get('Labels')) is not dict
                    or network['Labels'].get('financial.acceptance.run') != self.run_id
                    or created.tzinfo is None or not issued <= created <= datetime.now(timezone.utc)):
                raise ValueError('Configured network original ownership differs')
            if self._configured_network_created is None:
                self._configured_network_created = network['Created']
            elif self._configured_network_created != network['Created']:
                raise ValueError('Configured network generation changed')
            if type(network.get('Containers')) is not dict or (empty and network['Containers'] != {}):
                raise ValueError('Configured network exact census differs')
            if cleanup:
                return
            if self._configured_network_failed:
                raise ValueError('Configured network prior admission failed')
            subnet, gateway = self._configured_subnet
            ipam = network.get('IPAM')
            if (network.get('Internal') is not True or network.get('Driver') != 'bridge'
                    or network.get('Scope') != 'local' or network.get('EnableIPv6') is not False
                    or network.get('Ingress') is not False or network.get('Attachable') is not False
                    or type(ipam) is not dict or ipam.get('Driver') != 'default'
                    or type(ipam.get('Config')) is not list or len(ipam['Config']) != 1
                    or type(ipam['Config'][0]) is not dict
                    or ipam['Config'][0].get('Subnet') != subnet
                    or ipam['Config'][0].get('Gateway') != gateway
                    or ipam['Config'][0].get('IPRange') not in (None, '')
                    or ipam['Config'][0].get('AuxiliaryAddresses') not in (None, {})):
                raise ValueError('Configured network actual IPAM or isolation differs')
            self._configured_network_observed = True
        except BaseException:
            self._configured_network_failed = True
            raise


    def docker(self, *args, timeout=30, capture_stderr=False):
        return run_docker(args, timeout=timeout, capture_stderr=capture_stderr)

    def command(self, request, payload=None, timeout=3):
        if payload is not None and (not isinstance(payload, bytes) or len(payload) > 200 * 1024 * 1024):
            raise ValueError("Complete scan payload exceeds bound")
        if not 0 < timeout <= 10:
            raise ValueError("Finite protocol deadline required")
        deadline = time.monotonic() + timeout
        def remaining():
            budget = deadline - time.monotonic()
            if budget <= 0:
                raise TimeoutError("Scanner protocol deadline expired")
            return budget
        with socket.create_connection(("127.0.0.1", self.port), timeout=remaining()) as connection:
            connection.settimeout(remaining())
            connection.sendall(b"z" + request + b"\0")
            if payload is not None:
                for offset in range(0, len(payload), 65536):
                    chunk = payload[offset:offset + 65536]
                    connection.settimeout(remaining())
                    connection.sendall(struct.pack("!I", len(chunk)) + chunk)
                connection.settimeout(remaining())
                connection.sendall(struct.pack("!I", 0))
            response = bytearray()
            while len(response) <= 4096:
                connection.settimeout(remaining())
                chunk = connection.recv(4096)
                if not chunk:
                    raise ValueError("Scanner response terminated without framing")
                response.extend(chunk)
                if b"\0" in response:
                    answer, remaining = response.split(b"\0", 1)
                    if remaining:
                        raise ValueError("Unexpected trailing scanner response")
                    return answer.decode("ascii", "strict")
            raise ValueError("Scanner response exceeded bound")

    def scan(self, payload):
        self.attempted += 1
        answer = self.command(b"INSTREAM", payload)
        self.last_scan_reply = answer
        if answer == "stream: OK":
            self.clean += 1
            return "clean"
        if answer.startswith("stream: ") and answer.endswith(" FOUND"):
            self.infected += 1
            return "infected"
        raise ValueError("Scanner returned error or unknown result")

    def measure(self):
        executable = self.docker("exec", self.container_id, "sh", "-ec", "command -v clamd")
        if not re.fullmatch(r"/[a-zA-Z0-9/._-]+", executable):
            raise ValueError("Invalid observed scanner executable")
        measured = self.docker("exec", self.container_id, "sha256sum", executable)
        match = re.fullmatch(r"([a-f0-9]{64})  " + re.escape(executable), measured)
        if not match:
            raise ValueError("Invalid scanner executable hash")
        config_hash = self.docker("exec", self.container_id, "sha256sum", "/etc/clamav/acceptance.conf")
        if config_hash != digest(CONFIG.encode()) + "  /etc/clamav/acceptance.conf":
            raise ValueError("Runtime scanner config differs from owned configuration")
        databases = self.docker("exec", self.container_id, "sh", "-ec",
            "find /var/lib/clamav -maxdepth 1 -type f \\( -name '*.cvd' -o -name '*.cld' \\) -exec sha256sum {} +")
        return {"executable": executable, "executableSha256": match[1],
                "configSha256": digest(CONFIG.encode()), "databases": database_hashes(databases)}

    def start(self):
        self.receipt["stage"] = "pull-pinned-base"
        self.temp = tempfile.TemporaryDirectory(prefix=self.name + "-")
        config = Path(self.temp.name) / "clamd.conf"
        config.write_bytes(CONFIG.encode())
        self.docker("pull", IMAGE, timeout=180)
        image = json.loads(self.docker("image", "inspect", IMAGE))[0]
        if IMAGE not in image.get("RepoDigests", []):
            raise ValueError("Pulled image digest not observed")
        self.receipt["imageId"] = image["Id"]
        dockerfile = "FROM " + IMAGE + "\nCOPY clamd.conf /etc/clamav/acceptance.conf\n"
        (Path(self.temp.name) / "Dockerfile").write_text(dockerfile)
        iidfile = Path(self.temp.name) / "image-id"
        self.receipt["derivedImage"] = {"owned": True, "runLabel": self.run_id, "dockerfileSha256": digest(dockerfile.encode())}
        self.receipt["stage"] = "build-owned-derived-image"
        self.docker("build", "--network", "none", "--pull=false", "--label", "financial.acceptance.run=" + self.run_id,
                    "--iidfile", str(iidfile), self.temp.name, timeout=120)
        self.image_id = iidfile.read_text().strip()
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", self.image_id):
            raise ValueError("Invalid observed derived image identity")
        derived = json.loads(self.docker("image", "inspect", self.image_id))[0]
        if derived["Config"]["Labels"].get("financial.acceptance.run") != self.run_id or derived["Id"] != self.image_id:
            raise ValueError("Derived image ownership differs")
        self.receipt["derivedImage"].update({"imageId": self.image_id, "baseImageId": image["Id"]})
        self.receipt["derivedImage"]["buildCompleted"] = True
        if self.configured_network:
            self._configured_subnet = configured_network_census(self.docker)
        self.receipt["network"] = {"name": self.name + "-network", "owned": True}
        self.receipt["network"]["allocationIssuedUtc"] = datetime.now(timezone.utc).isoformat()
        if self.configured_network:
            subnet, gateway = self._configured_subnet
            self.network_id = self.docker("network", "create", "--internal", "--driver", "bridge",
                "--subnet", subnet, "--gateway", gateway, "--label",
                "financial.acceptance.run=" + self.run_id, self.name + "-network")
        else:
            self.network_id = self.docker("network", "create", "--internal", "--driver", "bridge", "--label",
                                          "financial.acceptance.run=" + self.run_id, self.name + "-network")
        if not re.fullmatch(r"[0-9a-f]{64}", self.network_id):
            raise ValueError("Invalid owned network identity")
        self.receipt["network"]["networkId"] = self.network_id
        if self.configured_network:
            network_rows = json.loads(self.docker("network", "inspect", self.network_id, timeout=3))
            if type(network_rows) is not list or len(network_rows) != 1:
                self._configured_network_failed = True
                raise ValueError("One original configured network observation required")
            self.validate_configured_network(network_rows[0], empty=True)
        # Record ownership before allocation; cleanup still runs after partial start.
        self.receipt["resources"] = [{"kind": "container", "name": self.name, "owned": True}]
        self.receipt["allocationIssuedUtc"] = datetime.now(timezone.utc).isoformat()
        self.receipt["stage"] = "create-owned-container"
        self.container_id = self.docker("create", "--name", self.name, "--label", "financial.acceptance.run=" + self.run_id,
            "--memory", "1536m", "--cpus", "2", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--tmpfs", "/tmp:rw,nosuid,nodev,size=32m",
            "--tmpfs", "/run:rw,nosuid,nodev,size=4m",
            "--network", self.network_id, "--ipc", "private", "--cgroupns", "private",
            "--entrypoint", "/usr/sbin/clamd", self.image_id, "--foreground", "--config-file=/etc/clamav/acceptance.conf")
        if not re.fullmatch(r"[0-9a-f]{64}", self.container_id):
            raise ValueError("Invalid observed created container identity")
        self.receipt["resources"][0]["containerId"] = self.container_id
        self.receipt["stage"] = "start-owned-container"
        self.docker("start", self.container_id)
        self.receipt["stage"] = "observe-container-after-start"
        diagnostic = self.startup_diagnostic()
        state = diagnostic.get("state", {})
        if not diagnostic.get("observed") or diagnostic.get("runtimeBoundaryObserved") is not True or state.get("Running") is not True or state.get("Paused") is not False or state.get("Restarting") is not False or type(state.get("Pid")) is not int or state["Pid"] <= 0:
            raise ValueError("Actual scanner container is not running immediately after start")
        self.receipt["containerGeneration"] = {"createdUtc": diagnostic["createdUtc"], "startedUtc": state["StartedAt"]}
        self.receipt["stage"] = "start-owned-loopback-relay"
        self.validate_backend_endpoint()
        self.relay = object.__new__(LoopbackRelay)
        LoopbackRelay.__init__(self.relay, self.validate_backend_endpoint, startup_tracking=True)
        self.port = self.relay.endpoint[1]
        self.receipt["loopbackRelay"] = {**self.relay.identity, "backend": self.backend_identity,
                                         "portIsolation": self.receipt["portIsolation"], "ownedInProcess": True}
        container = json.loads(self.docker("inspect", self.container_id))[0]
        if container["Image"] != self.image_id or container["Id"] != self.container_id or not container["HostConfig"]["ReadonlyRootfs"]:
            raise ValueError("Runtime image or immutable root policy differs")
        self.observe_runtime_boundary(container, "current")
        mounts = container["Mounts"]
        if any(mount.get("Type") != "tmpfs" or mount.get("Destination") not in ("/tmp", "/run") for mount in mounts):
            raise ValueError("Unexpected runtime mounts or persistent volumes")
        network = json.loads(self.docker("network", "inspect", self.network_id))[0]
        if network["Id"] != self.network_id or not network["Internal"] or network["Driver"] != "bridge" or network["Labels"].get("financial.acceptance.run") != self.run_id:
            raise ValueError("Dedicated network observation differs")
        self.receipt["runtimePolicy"] = {"readOnlyRoot": True, "persistentVolumes": 0,
                                         "directEntrypoint": "/usr/sbin/clamd", "memoryBytes": 1536 * 1024 * 1024}
        self.receipt["containerGeneration"] = {"createdUtc": container["Created"], "startedUtc": container["State"]["StartedAt"]}
        self.receipt["stage"] = "measure-immutable-files"
        before = self.measure()
        self.receipt["stage"] = "await-actual-clamd-ping"
        readiness_started = time.monotonic()
        deadline = readiness_started + self.deadline_seconds
        final_slot_budget = min(3, self.deadline_seconds / 16)
        slot_spacing = (self.deadline_seconds - final_slot_budget) / 15
        attempt_index = 0
        while attempt_index < 16 and time.monotonic() < deadline:
            target = readiness_started + attempt_index * slot_spacing
            while time.monotonic() < target and time.monotonic() < deadline:
                pause = min(target, deadline) - time.monotonic()
                if pause <= 0:
                    break
                time.sleep(pause)
            if time.monotonic() >= deadline:
                raise TimeoutError("Actual clamd readiness deadline expired")
            attempt_index += 1
            try:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Actual clamd readiness deadline expired")
                answer = self.relay.startup_ping(timeout=min(3, remaining))
                if time.monotonic() >= deadline:
                    raise TimeoutError("Actual clamd readiness deadline expired")
                if answer == "PONG":
                    break
                raise ValueError("Original startup PING result refused")
            except StartupPingRefused:
                pass
            except BaseException:
                # Projection cannot replace the original refusal or cleanup path.
                try:
                    projection = self.relay.startup_admission_diagnostic()
                    if projection is not None:
                        self.receipt["startupAdmissionDiagnostic"] = projection
                except BaseException:
                    pass
                raise
            # Only the original settled/accounted StartupPingRefused reaches
            # this next slot. A successful source return is exclusively PONG.
        else:
            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(remaining)
            raise TimeoutError("Actual clamd readiness deadline expired")
        self.receipt["startupRelayHandoff"] = self.relay.seal_startup_readiness()
        version = self.command(b"VERSION")
        if not re.fullmatch(r"ClamAV [^/]+/[0-9]+/.+", version):
            raise ValueError("Engine/database readiness not observed")
        import hosted_companion_resources as h
        self.receipt["stage"] = "observe-daemon-and-daily-database"
        plan = h.ScannerPlan(before["executable"], before["executableSha256"],
                            (before["executable"], "--foreground", "--config-file=/etc/clamav/acceptance.conf"),
                            "/etc/clamav/acceptance.conf", before["configSha256"],
                            {Path(row["path"]).name: row["sha256"] for row in before["databases"]},
                            "/tmp/acceptance-clamd.log")
        plan.validate()
        lease = SimpleNamespace(container_id=self.container_id, container_port=3310)
        owner = h.scanner_process(lease, plan)
        engine, loaded = h.parse_version(version)
        daily = next(row["path"] for row in before["databases"] if Path(row["path"]).stem == "daily")
        info = self.docker("exec", self.container_id, "sigtool", "--info", daily)
        if re.findall(r"^Version: ([0-9]+)$", info, re.M) != [loaded]:
            raise ValueError("Loaded VERSION differs from observed daily database")
        self.receipt["stage"] = "real-clean-eicar-controls"
        if self.scan(CLEAN) != "clean":
            raise ValueError("Benign complete-file control failed")
        if self.scan(EICAR) != "infected":
            raise ValueError("EICAR complete-file control failed")
        if not re.fullmatch(r"stream: [^\r\n\x00]{0,160}Eicar[^\r\n\x00]{0,160} FOUND", self.last_scan_reply, re.I):
            raise ValueError("Actual EICAR signature detection not observed")
        after = self.measure()
        if before != after:
            raise ValueError("Scanner executable/config/databases changed during controls")
        if h.scanner_process(lease, plan) != owner:
            raise ValueError("Scanner socket owner/generation changed during controls")
        self.receipt.update({"scannerReady": True, "measurement": after, "version": version,
                             "daemonProcess": owner, "scannerPlan": plan.__dict__,
                             "parentAdmission": False,
                             "endpoint": {"host": "127.0.0.1", "port": self.port},
                             "controls": {"clean": True, "eicar": True},
                             "counters": self.counters()})
        self.receipt["counterScope"] = "Actual readiness control INSTREAM calls; financial File runtime counters remain unobserved"
        self.receipt["stage"] = "actual-scanner-controls-complete"
        return self

    def counters(self):
        return {"attempted": self.attempted, "clean": self.clean, "infected": self.infected}

    def validate_backend_endpoint(self):
        container = json.loads(self.docker("inspect", self.container_id, timeout=3))[0]
        network = json.loads(self.docker("network", "inspect", self.network_id, timeout=3))[0]
        if self.configured_network:
            self.validate_configured_network(network)
        generation = self.receipt["containerGeneration"]
        state = container.get("State", {})
        if container.get("Id") != self.container_id or container.get("Image") != self.image_id or container.get("Created") != generation["createdUtc"] or state.get("StartedAt") != generation["startedUtc"] or state.get("Running") is not True or state.get("Paused") is not False or state.get("Restarting") is not False or container.get("Config", {}).get("Labels", {}).get("financial.acceptance.run") != self.run_id:
            raise ValueError("Relay backend container ownership or generation differs")
        self.observe_runtime_boundary(container, "current")
        self.receipt["portIsolation"] = no_docker_publication(container)
        networks = container.get("NetworkSettings", {}).get("Networks", {})
        if len(networks) != 1 or container.get("HostConfig", {}).get("NetworkMode") != self.network_id:
            raise ValueError("Relay backend has foreign network attachment")
        attached = next(iter(networks.values()))
        if attached.get("NetworkID") != self.network_id or network.get("Id") != self.network_id or network.get("Internal") is not True or network.get("Driver") != "bridge" or network.get("Labels", {}).get("financial.acceptance.run") != self.run_id:
            raise ValueError("Relay requires the exact owned internal bridge")
        endpoint = network.get("Containers", {}).get(self.container_id, {})
        host = attached.get("IPAddress", "")
        address = ipaddress.IPv4Address(host)
        if str(address) != host or address.is_unspecified or address.is_multicast or address.is_loopback or address.is_link_local or str(ipaddress.IPv4Interface(endpoint.get("IPv4Address", "")).ip) != host or endpoint.get("EndpointID") != attached.get("EndpointID") or not re.fullmatch(r"[0-9a-f]{64}", attached.get("EndpointID", "")):
            raise ValueError("Relay backend IP/endpoint ownership differs")
        subnets = [ipaddress.ip_network(row["Subnet"]) for row in network.get("IPAM", {}).get("Config", []) if row.get("Subnet")]
        if not any(subnet.version == 4 and address in subnet for subnet in subnets):
            raise ValueError("Relay backend IP is outside the owned network subnet")
        identity = {"containerId": self.container_id, "networkId": self.network_id, "endpointId": attached["EndpointID"], "host": host, "port": 3310, **generation}
        if self.backend_identity is not None and self.backend_identity != identity:
            raise ValueError("Relay measured backend identity changed")
        self.backend_identity = identity
        return host, 3310

    def require_owned_container(self, container):
        config = container.get("Config", {})
        state = container.get("State", {})
        if (container.get("Id") != self.container_id or not self.container_id
                or container.get("Image") != self.image_id
                or config.get("Labels", {}).get("financial.acceptance.run") != self.run_id):
            raise ValueError("Runtime observation container ownership differs")
        generation = self.receipt.get("containerGeneration")
        if generation and (container.get("Created") != generation["createdUtc"]
                           or state.get("StartedAt") != generation["startedUtc"]):
            raise ValueError("Runtime observation container generation differs")
        if not isinstance(container.get("Created"), str) or not isinstance(state.get("StartedAt"), str):
            raise ValueError("Runtime observation generation fields required")

    def observe_runtime_boundary(self, container, phase):
        if phase not in ("start", "current", "preStop", "preRemoval"):
            raise ValueError("Unknown runtime observation phase")
        try:
            self.require_owned_container(container)
            policy = observed_runtime_policy(container)
            isolation = no_docker_publication(container)
        except Exception:
            failures = self.receipt.setdefault("runtimeBoundaryFailures", [])
            category = phase + "Refused"
            if category not in failures:
                failures.append(category)
            raise
        snapshot = {"containerId": container["Id"], "imageId": container["Image"],
                    "runLabel": self.run_id, "createdUtc": container["Created"],
                    "startedUtc": container["State"]["StartedAt"],
                    "policy": policy, "portIsolation": isolation}
        self.receipt.setdefault("runtimeBoundaryObservations", {})[phase] = snapshot
        return snapshot

    def startup_diagnostic(self):
        """Observe only the exact owned synthetic container; never replace admission."""
        diagnostic = {"containerId": self.container_id, "observedUtc": datetime.now(timezone.utc).isoformat()}
        try:
            if not self.container_id or not re.fullmatch(r"[0-9a-f]{64}", self.container_id):
                raise ValueError("Exact created container identity required for diagnostics")
            container = json.loads(self.docker("inspect", self.container_id, timeout=10))[0]
            if container.get("Id") != self.container_id or container.get("Image") != self.image_id or container.get("Config", {}).get("Labels", {}).get("financial.acceptance.run") != self.run_id:
                raise ValueError("Startup diagnostic ownership differs")
            state = container.get("State", {})
            diagnostic["state"] = {key: state.get(key) for key in ("Status", "Running", "Paused", "Restarting", "OOMKilled", "Dead", "Pid", "ExitCode", "Error", "StartedAt", "FinishedAt")}
            diagnostic["createdUtc"] = container.get("Created")
            diagnostic["declaredPortBindings"] = container.get("HostConfig", {}).get("PortBindings")
            diagnostic["actualPortBindings"] = container.get("NetworkSettings", {}).get("Ports")
            diagnostic["networkMode"] = container.get("HostConfig", {}).get("NetworkMode")
            diagnostic["networkIds"] = {key: value.get("NetworkID") for key, value in container.get("NetworkSettings", {}).get("Networks", {}).items()}
            diagnostic["observed"] = True
            try:
                self.observe_runtime_boundary(container, "start")
                diagnostic["runtimeBoundaryObserved"] = True
            except Exception as error:
                diagnostic["runtimeBoundaryObserved"] = False
                diagnostic["runtimeBoundaryErrorType"] = type(error).__name__
            try:
                # Fixed synthetic daemon only. Bounded command + bounded public tail;
                # no environment/config/private data are collected.
                tail = self.docker("logs", "--tail", "40", self.container_id, timeout=10, capture_stderr=True)
                diagnostic["startupLogTail"] = tail[-8192:]
                diagnostic["startupLogTailSha256"] = digest(tail.encode())
                diagnostic["startupLogTailTruncated"] = len(tail) > 8192
            except Exception as error:
                diagnostic["startupLogErrorType"] = type(error).__name__
        except Exception as error:
            diagnostic["observed"] = False
            diagnostic["diagnosticErrorType"] = type(error).__name__
        self.receipt["startupDiagnostic"] = diagnostic
        return diagnostic

    def close(self):
        errors = list(self.receipt.get("runtimeBoundaryFailures", []))
        if self.configured_network and self._configured_network_failed:
            errors.append("ConfiguredNetworkAdmissionRefused")
        if self.receipt["resources"]:
            owned = False
            removable = False
            try:
                if not self.container_id:
                    candidates = self.docker("ps", "-a", "--no-trunc", "--filter", "label=financial.acceptance.run=" + self.run_id,
                                             "--filter", "name=^/" + self.name + "$", "--format", "{{.ID}}").splitlines()
                    if len(candidates) > 1:
                        raise ValueError("Ambiguous container allocation recovery")
                    if candidates:
                        recovered = json.loads(self.docker("inspect", candidates[0]))[0]
                        created = datetime.fromisoformat(recovered["Created"].replace("Z", "+00:00"))
                        issued = datetime.fromisoformat(self.receipt["allocationIssuedUtc"])
                        if not re.fullmatch(r"[0-9a-f]{64}", recovered["Id"]) or recovered["Config"]["Labels"].get("financial.acceptance.run") != self.run_id or recovered["Image"] != self.image_id or recovered["Name"] != "/" + self.name or not issued <= created <= datetime.now(timezone.utc):
                            raise ValueError("Container allocation recovery ownership differs")
                        self.container_id = recovered["Id"]
                        self.receipt["resources"][0]["containerId"] = self.container_id
                        self.receipt["resources"][0]["reconciledAfterUncertainCreate"] = True
                    else:
                        raise ValueError("Container allocation completion uncertain")
                observed = json.loads(self.docker("inspect", self.container_id))[0]
                if observed["Config"]["Labels"].get("financial.acceptance.run") != self.run_id or not self.container_id or observed["Id"] != self.container_id:
                    raise ValueError("Container ownership differs; cleanup refused")
                generation = self.receipt.get("containerGeneration")
                if generation and (observed["Created"] != generation["createdUtc"] or observed["State"]["StartedAt"] != generation["startedUtc"]):
                    raise ValueError("Container generation changed; cleanup refused")
                self.require_owned_container(observed)
                cleanup_generation = (observed["Created"], observed["State"]["StartedAt"])
                owned = True
                # Policy drift is sticky, but does not abandon an exactly owned resource.
                try:
                    self.observe_runtime_boundary(observed, "preStop")
                except Exception as error:
                    errors.append(type(error).__name__)
                self.docker("stop", "--time", "5", self.container_id)
                stopped = json.loads(self.docker("inspect", self.container_id))[0]
                self.require_owned_container(stopped)
                if (stopped["Created"], stopped["State"]["StartedAt"]) != cleanup_generation:
                    raise ValueError("Cleanup-local container generation changed; removal refused")
                if any(stopped.get("State", {}).get(flag) is not False for flag in ("Running", "Paused", "Restarting")):
                    raise ValueError("Exact stopped scanner state was not observed")
                self.receipt["stoppedContainerObserved"] = True
                removable = True
                try:
                    self.observe_runtime_boundary(stopped, "preRemoval")
                except Exception as error:
                    errors.append(type(error).__name__)
            except Exception as error:
                errors.append(type(error).__name__)
            if owned:
                if self.port and self.receipt.get("stoppedContainerObserved"):
                    try:
                        self.command(b"PING", timeout=1)
                    except (OSError, ValueError):
                        self.receipt.setdefault("controls", {})["unavailableAfterStop"] = True
                    except Exception as error:
                        errors.append(type(error).__name__)
                    else:
                        errors.append("StoppedScannerResponds")
                if removable:
                    try:
                        self.docker("rm", self.container_id)
                    except Exception as error:
                        errors.append(type(error).__name__)
            # Absence is measured, not inferred from docker rm's exit code.
            try:
                remaining = self.docker("ps", "-a", "--filter", "name=^/" + self.name + "$", "--format", "{{.Names}}")
                if remaining:
                    errors.append("OwnedContainerRemains")
            except Exception as error:
                errors.append(type(error).__name__)
        if self.relay:
            try:
                self.receipt["loopbackRelay"] = {**self.receipt.get("loopbackRelay", {}), **self.relay.close()}
                if not self.receipt["loopbackRelay"]["cleanupVerified"]:
                    errors.append("OwnedRelayCleanupUncertain")
            except Exception as error:
                errors.append(type(error).__name__)
        if self.receipt.get("derivedImage"):
            try:
                if not self.receipt["derivedImage"].get("buildCompleted"):
                    errors.append("DerivedBuildCompletionUncertain")
                if not self.image_id:
                    discovered = self.docker("image", "ls", "--no-trunc", "--filter", "label=financial.acceptance.run=" + self.run_id, "--format", "{{.ID}}").splitlines()
                    if len(discovered) > 1:
                        raise ValueError("Derived image allocation identity uncertain")
                    self.image_id = discovered[0] if discovered else None
                if self.image_id:
                    image = json.loads(self.docker("image", "inspect", self.image_id))[0]
                    if image["Id"] != self.image_id or image["Config"]["Labels"].get("financial.acceptance.run") != self.run_id:
                        raise ValueError("Derived image cleanup ownership differs")
                    references = self.docker("ps", "-a", "--filter", "ancestor=" + self.image_id, "--format", "{{.ID}}")
                    if references:
                        raise ValueError("Derived image remains referenced")
                    self.docker("image", "rm", self.image_id)
                    remaining = self.docker("image", "ls", "--no-trunc", "--filter", "label=financial.acceptance.run=" + self.run_id, "--format", "{{.ID}}")
                    if remaining:
                        raise ValueError("Derived image remains")
                    self.receipt["derivedImage"]["cleanupVerified"] = True
                else:
                    self.receipt["derivedImage"]["cleanupVerified"] = True
            except Exception as error:
                errors.append(type(error).__name__)
        if self.receipt.get("network"):
            try:
                if not self.network_id:
                    candidates = self.docker("network", "ls", "--no-trunc", "--filter", "label=financial.acceptance.run=" + self.run_id,
                                             "--filter", "name=^" + self.name + "-network$", "--format", "{{.ID}}").splitlines()
                    if len(candidates) != 1:
                        raise ValueError("Network allocation identity uncertain or ambiguous")
                    recovered = json.loads(self.docker("network", "inspect", candidates[0]))[0]
                    created = datetime.fromisoformat(recovered["Created"].replace("Z", "+00:00"))
                    issued = datetime.fromisoformat(self.receipt["network"]["allocationIssuedUtc"])
                    if not re.fullmatch(r"[0-9a-f]{64}", recovered["Id"]) or recovered["Name"] != self.name + "-network" or recovered["Labels"].get("financial.acceptance.run") != self.run_id or not issued <= created <= datetime.now(timezone.utc):
                        raise ValueError("Network allocation recovery ownership differs")
                    self.network_id = recovered["Id"]
                    self.receipt["network"]["networkId"] = self.network_id
                    self.receipt["network"]["reconciledAfterUncertainCreate"] = True
                network = json.loads(self.docker("network", "inspect", self.network_id))[0]
                if self.configured_network:
                    self.validate_configured_network(network, empty=True, cleanup=True)
                if network["Id"] != self.network_id or network["Labels"].get("financial.acceptance.run") != self.run_id or network.get("Containers"):
                    raise ValueError("Owned network cleanup refused")
                self.docker("network", "rm", self.network_id)
                remaining = self.docker("network", "ls", "--no-trunc", "--filter", "label=financial.acceptance.run=" + self.run_id, "--format", "{{.ID}}")
                if remaining:
                    raise ValueError("Owned network remains")
                self.receipt["network"]["cleanupVerified"] = True
            except Exception as error:
                errors.append(type(error).__name__)
        if self.temp:
            config_directory = self.temp.name
            try:
                self.temp.cleanup()
                if Path(config_directory).exists():
                    errors.append("OwnedConfigRemains")
            except Exception as error:
                errors.append(type(error).__name__)
        self.receipt.update({"cleanupVerified": not errors, "cleanupErrors": errors,
                             "counters": self.counters()})
        return not errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or not __import__("sys").platform.startswith("linux"):
        parser.error("Run only on a standard hosted Ubuntu runner")
    run = os.environ.get("GITHUB_RUN_ID", "")
    attempt = os.environ.get("GITHUB_RUN_ATTEMPT", "")
    head = os.environ.get("FINANCIAL_SOURCE_HEAD", "")
    if not re.fullmatch(r"[1-9][0-9]{0,19}", run) or int(run) > 2**63 - 1 or not re.fullmatch(r"[1-9][0-9]{0,9}", attempt) or int(attempt) > 2**31 - 1 or not re.fullmatch(r"[0-9a-f]{40}", head):
        parser.error("Hosted run, attempt and exact head identities are required")
    actual_head = subprocess.check_output(["git", "rev-parse", "HEAD"], timeout=5, text=True).strip()
    if actual_head != head:
        parser.error("Actual checked-out source differs from the declared exact head")
    if args.output.is_absolute() or ".." in args.output.parts or args.output.parts[:2] != ("TestResults", "HostedScanner"):
        parser.error("Receipt must be below TestResults/HostedScanner")
    if any(path.is_symlink() for path in (args.output, *args.output.parents)) or not args.output.resolve().is_relative_to(Path.cwd().resolve()):
        parser.error("Receipt path must stay in the workspace without symbolic links")
    scanner = Scanner()
    scanner.receipt["hostedIdentity"] = {"runId": run, "attempt": attempt, "head": head}
    scanner.receipt["hostedIdentity"]["eventSha"] = os.environ.get("GITHUB_SHA", "")
    failed = False
    try:
        scanner.start()
    except Exception as error:
        scanner.receipt["failureType"] = type(error).__name__
        if isinstance(error, subprocess.CalledProcessError):
            scanner.receipt["failureCommand"] = list(error.cmd)
            scanner.receipt["failureReason"] = error.stderr
        if isinstance(error, ValueError):
            scanner.receipt["failureReason"] = str(error)[:256]
        failed = True
    finally:
        failed = not scanner.close() or failed
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(scanner.receipt, indent=2) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
