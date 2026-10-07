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


class Scanner:
    def __init__(self, run_id=None, deadline_seconds=180):
        self.run_id = str(uuid.uuid4()) if run_id is None else run_id
        if not re.fullmatch(r"[a-zA-Z0-9-]{1,64}", self.run_id):
            raise ValueError("Invalid owned run identity")
        if not 1 <= deadline_seconds <= 300:
            raise ValueError("Readiness must have a finite deadline")
        self.name = "financial-scanner-" + self.run_id
        self.deadline_seconds = deadline_seconds
        self.port = None
        self.container_id = None
        self.image_id = None
        self.network_id = None
        self.temp = None
        self.attempted = self.clean = self.infected = 0
        self.receipt = {"schemaVersion": 1, "runId": self.run_id, "imageReference": IMAGE,
                        "genuineEightHostFinancialAccepted": False, "resources": [],
                        "scannerReady": False, "cleanupVerified": False}

    def docker(self, *args, timeout=30):
        process = subprocess.Popen(["docker", *args], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        output = {process.stdout: bytearray(), process.stderr: bytearray()}
        selector = selectors.DefaultSelector()
        deadline = time.monotonic() + timeout
        try:
            for stream in output:
                os.set_blocking(stream.fileno(), False)
                selector.register(stream, selectors.EVENT_READ)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Docker command deadline expired")
                for key, _ in selector.select(min(remaining, 0.1)):
                    chunk = os.read(key.fileobj.fileno(), 4096)
                    if not chunk:
                        selector.unregister(key.fileobj)
                    else:
                        if sum(len(value) for value in output.values()) + len(chunk) > 262144:
                            raise ValueError("Docker output exceeded measurement bound")
                        output[key.fileobj].extend(chunk)
            process.wait(timeout=max(0.001, deadline-time.monotonic()))
            if process.returncode:
                raise subprocess.CalledProcessError(process.returncode, ["docker", *args])
            return output[process.stdout].decode("utf-8", "strict").strip()
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            selector.close()
            process.stdout.close()
            process.stderr.close()

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
        self.receipt["network"] = {"name": self.name + "-network", "owned": True}
        self.receipt["network"]["allocationIssuedUtc"] = datetime.now(timezone.utc).isoformat()
        self.network_id = self.docker("network", "create", "--internal", "--driver", "bridge", "--label",
                                      "financial.acceptance.run=" + self.run_id, self.name + "-network")
        if not re.fullmatch(r"[0-9a-f]{64}", self.network_id):
            raise ValueError("Invalid owned network identity")
        self.receipt["network"]["networkId"] = self.network_id
        # Record ownership before allocation; cleanup still runs after partial start.
        self.receipt["resources"] = [{"kind": "container", "name": self.name, "owned": True}]
        self.receipt["allocationIssuedUtc"] = datetime.now(timezone.utc).isoformat()
        self.receipt["stage"] = "create-owned-container"
        self.container_id = self.docker("create", "--name", self.name, "--label", "financial.acceptance.run=" + self.run_id,
            "--memory", "1536m", "--cpus", "2", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--tmpfs", "/tmp:rw,nosuid,nodev,size=32m",
            "--tmpfs", "/run:rw,nosuid,nodev,size=4m", "-p", "127.0.0.1::3310",
            "--network", self.network_id, "--ipc", "private", "--cgroupns", "private",
            "--entrypoint", "/usr/sbin/clamd", self.image_id, "--foreground", "--config-file=/etc/clamav/acceptance.conf")
        if not re.fullmatch(r"[0-9a-f]{64}", self.container_id):
            raise ValueError("Invalid observed created container identity")
        self.receipt["resources"][0]["containerId"] = self.container_id
        self.docker("start", self.container_id)
        binding = self.docker("port", self.container_id, "3310/tcp")
        match = re.fullmatch(r"127\.0\.0\.1:([0-9]+)", binding)
        if not match:
            raise ValueError("Scanner endpoint escaped loopback")
        self.port = int(match[1])
        container = json.loads(self.docker("inspect", self.container_id))[0]
        if container["Image"] != self.image_id or container["Id"] != self.container_id or not container["HostConfig"]["ReadonlyRootfs"]:
            raise ValueError("Runtime image or immutable root policy differs")
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
        deadline = time.monotonic() + self.deadline_seconds
        while time.monotonic() < deadline:
            try:
                if self.command(b"PING", timeout=min(3, max(0.001, deadline-time.monotonic()))) == "PONG":
                    break
            except (OSError, ValueError):
                pass
            time.sleep(0.5)
        else:
            raise TimeoutError("Actual clamd readiness deadline expired")
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

    def close(self):
        errors = []
        if self.receipt["resources"]:
            owned = False
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
                owned = True
                self.docker("stop", "--time", "5", self.container_id)
            except Exception as error:
                errors.append(type(error).__name__)
            if owned:
                if self.port:
                    try:
                        self.command(b"PING", timeout=1)
                    except OSError:
                        self.receipt.setdefault("controls", {})["unavailableAfterStop"] = True
                    except Exception as error:
                        errors.append(type(error).__name__)
                    else:
                        errors.append("StoppedScannerResponds")
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
