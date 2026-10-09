"""Pure source controls only; all owner/kernel/protocol observations are synthetic."""
import ast
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone, timedelta
from functools import wraps
import copy
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import time
import types
import unittest
from urllib.parse import urlsplit

ROOT = Path(__file__).parent


def require(value, message):
    if not value:
        raise ValueError(message)


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def load(name, values):
    tree = ast.parse((ROOT / name).read_text(encoding="utf-8"))
    tree.body = [node for node in tree.body if not isinstance(node, (ast.Import, ast.ImportFrom))
                 and not isinstance(node, ast.If)]
    module = types.ModuleType(name)
    module.__dict__.update(values)
    exec(compile(tree, name, "exec"), module.__dict__)
    return module


@dataclass(frozen=True)
class Spec:
    owner: str
    source_sha: str
    environment: dict


class Lifetime:
    pass


class Normal:
    pass


class Front:
    def start(self):
        self.events.append("front-start")
        self.process = types.SimpleNamespace(pid=31)

    def observe_listener(self):
        self.events.append("original-listener")
        return {"held": "pure-fixed"}


class Cap:
    def __init__(self, life, normal, front):
        self.lifetime, self.normal, self.front = life, normal, front
        self.context = normal.context
        self.failed = self.admitted = self.released = False

    def _custody(self):
        require(self.lifetime.normal is self.normal and self.lifetime.front is self.front, "custody")

    def _no_file_birth(self):
        require(not self.normal.owned, "File birth")


class Lease:
    def arguments(self):
        return ["-host", self.bridge_ip]


class Scanner:
    def measure(self):
        return copy.deepcopy(self.receipt["measurement"])

    def command(self, *args, **kwargs):
        return "ClamAV 1.4.3/88/model"

    def scan(self, payload):
        self.last_scan_reply = "stream: OK" if payload == b"model-clean" else "stream: Eicar-Test-Signature FOUND"
        return "clean" if payload == b"model-clean" else "infected"

    def docker(self, *args, **kwargs):
        return "Version: 88\n"

    def validate_configured_network(self, network):
        require(len(network["IPAM"]["Config"]) == 1, "network")


class Bridge:
    def refresh(self):
        self.events.append("bridge-refresh")
        return {"modelOnly": True}

    def _retained_consumers(self):
        return self.cap

    def retain_file_front_consumers(self, cap):
        self.events.append("retained-before-birth")
        require(cap.front.process is None, "before birth")
        self.cap = cap

    def _inspect(self, kind, identity):
        return copy.deepcopy(self.snapshots[(kind, identity)])


def flatten(configuration):
    result = {}
    def visit(value, prefix):
        if type(value) is dict:
            for key, child in value.items():
                visit(child, prefix + [key])
        else:
            result["__".join(prefix)] = str(value).lower() if type(value) is bool else str(value)
    visit(configuration, [])
    return result


class Controls(unittest.TestCase):
    def setUp(self):
        self.now = datetime.now(timezone.utc)
        self.issue = (self.now - timedelta(minutes=1)).isoformat()
        self.expiry = (self.now + timedelta(minutes=10)).isoformat()
        self.generation = self.now.isoformat()
        self.context = types.SimpleNamespace(run_id="44", attempt=1, file_sha="f" * 40,
            lease_id="c821-00000000-0000-0000-0000-000000000001", issued_utc=self.issue,
            expires_utc=self.expiry)
        self.events = []
        self.h = types.SimpleNamespace(require=require, instant=instant, SECTION="HostedFinancialCompletionAcceptance",
            HEX64=re.compile("[0-9a-f]{64}"), configuration_environment=flatten, origin=lambda value: require(
                value == "http://127.0.0.1:1234", "origin"), parse_version=lambda value: ("1.4.3", "88"))
        # Execute the actual new declarative helper, never original runtime I/O.
        tree = ast.parse((ROOT / "owned_front_host.py").read_text(encoding="utf-8"))
        helper = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                      and node.name == "admit_public_profile_shape")
        env = {"h": self.h, "ipaddress": ipaddress, "re": re}
        exec(compile(ast.Module([helper], []), "profile-helper", "exec"), env)
        self.profile_gate = env["admit_public_profile_shape"]
        self.profile = {key: "model" for key in {"RunId", "ExpiresUtc", "FrontOrigin", "Backend", "Repository",
            "SourceSha", "ExecutableDll", "ExecutableSha256", "ParentPid", "ParentKernelStartTicks",
            "ParentExecutablePath", "ParentExecutableSha256", "ParentScriptPath", "ParentScriptSha256",
            "BootstrapPipeHandle", "BootstrapPipeInode"}}
        self.normal, self.front, self.life, self.bridge = Normal(), Front(), Lifetime(), Bridge()
        self.life.normal, self.life.front = self.normal, self.front
        self.normal.context = self.front.context = self.bridge.context = self.context
        self.normal.timer_owned = True
        self.normal.closed = False
        self.normal.cleanup_failures = []
        self.normal.owned = []
        self.normal.minimum_guard = 4096 * 1024**2
        owners = {"Auth", "Accounting", "Quotation", "Order", "IAM", "Document", "File", "Notification"}
        self.normal.specs = tuple(Spec(owner, "f" * 40, {self.h.SECTION + "__Enabled": "true",
            self.h.SECTION + "__Admission__fileSourceSha": "f" * 40} if owner == "File" else {"authority": "unchanged"})
            for owner in sorted(owners))
        self.front.events = self.bridge.events = self.events
        self.front.process = types.SimpleNamespace(pid=31)
        self.front.ticks = 9
        self.front.launcher_environment = {}
        self.front.actual_environment = {}
        self.front.pipe = object()
        self.front.spec = types.SimpleNamespace(dotnet_executable="/model/dotnet", dotnet_sha256="d" * 64,
            profile_path="/model/profile")
        self.cap = Cap(self.life, self.normal, self.front)
        self.bridge.cap = self.cap
        self.bridge._failure = False
        lease = Lease()
        for key, value in {"container_id": "a" * 64, "scanner_id": "b" * 64, "network_id": "c" * 64,
            "bind_owned_ipv4": True, "lease_id": self.context.lease_id, "run_id": "44", "attempt": "1",
            "file_sha": "f" * 40, "issued": self.issue, "expires": self.expiry, "created": self.generation,
            "started": self.generation, "network_created": self.generation, "bridge_ip": "10.253.240.3",
            "port": 4443, "executable_sha": "e" * 64, "kernel_ticks": 5}.items():
            setattr(lease, key, value)
        self.bridge._borrow = lease
        scanner = Scanner()
        scanner.container_id = self.bridge.scanner_container_id = lease.scanner_id
        scanner.port = 1235
        scanner.image_id = "sha256:" + "d" * 64
        scanner.relay = types.SimpleNamespace(endpoint=("127.0.0.1", 1235))
        scanner.receipt = {"measurement": {"executable": "/usr/sbin/clamd",
            "executableSha256": "3" * 64, "configSha256": "4" * 64, "databases": [{"path": "/var/lib/clamav/main.cvd", "sha256": "1" * 64},
            {"path": "/var/lib/clamav/daily.cvd", "sha256": "2" * 64}]}}
        self.bridge.scanner = scanner
        self.bridge.snapshots = {("container", lease.container_id): {"HostConfig": {"Memory": 128 * 1024**2,
            "NanoCpus": 1000000000}}, ("container", lease.scanner_id): {"Id": lease.scanner_id,
            "Created": self.generation, "State": {"StartedAt": self.generation, "Pid": 22}, "Image": scanner.image_id,
            "NetworkSettings": {"Networks": {"owned": {"NetworkID": lease.network_id, "IPAddress": "10.253.240.2"}}}},
            ("network", lease.network_id): {"Name": "financial-scanner-" + lease.lease_id[5:] + "-network",
            "IPAM": {"Config": [{"Subnet": "10.253.240.0/28", "Gateway": "10.253.240.1"}]}}}
        self.h.ScannerPlan = lambda *args: types.SimpleNamespace(validate=lambda: None)
        self.h.scanner_process = lambda *args: {"pid": 22, "kernelStartTicks": 8, "listenerInode": "90"}
        def validate(specs, context, guard, memory):
            require(len(specs) == 8 and {spec.owner for spec in specs} == owners and guard == self.normal.minimum_guard,
                    "graph and original guard")
        self.builder = load("shared_file_front_configuration.py", {"h": self.h, "replace": replace, "dataclass": dataclass, "field": field, "SimpleNamespace": types.SimpleNamespace,
            "datetime": datetime, "timezone": timezone, "Path": Path, "hashlib": hashlib, "json": json,
            "re": re, "time": time, "urlsplit": urlsplit, "wraps": wraps,
            "lifetime": types.SimpleNamespace(HostLifetime=Lifetime, LifetimeNormalHosts=Normal, LifetimeFrontHost=Front),
            "normal_source": types.SimpleNamespace(HostSpec=Spec, OWNERS=owners, validate_specs=validate,
                available_memory_bytes=lambda: 16384 * 1024**2), "storage": types.SimpleNamespace(Lease=Lease,
                IMAGE="model@sha256:" + "e" * 64, IMAGE_ID="sha256:" + "e" * 64,
                observe=lambda *args: self.events.append("original-storage-observe")),
            "scanner_source": types.SimpleNamespace(Scanner=Scanner, CLEAN=b"model-clean", EICAR=b"model-infected"),
            "BorrowedScannerBridge": Bridge, "RetainedFileFrontConsumers": Cap,
            "admit_public_profile_shape": self.profile_gate, "verify_source": lambda *args: self.events.append("source-verify"),
            "observe_start": lambda *args: {"StartedUtc": self.generation}})
        backend, shared = self.builder.observe_shared_declaration(self.bridge)
        self.profile.update(RunId=lease.lease_id, ExpiresUtc=self.expiry, FrontOrigin="http://127.0.0.1:1234/",
                            Backend=backend, SharedScannerBridge=shared)
        self.front.profile = self.profile
        self.caller = load("run_eight_host_financial_acceptance.py", {"h": self.h, "dataclass": dataclass, "field": field,
            "lifetime": types.SimpleNamespace(HostLifetime=Lifetime, LifetimeNormalHosts=Normal, LifetimeFrontHost=Front),
            "RetainedFileFrontConsumers": Cap, "BorrowedScannerBridge": Bridge,
            "install_observed_file_configuration": self.builder.install_observed_file_configuration,
            "observe_shared_declaration": self.builder.observe_shared_declaration,
            "load_public_profile": lambda *args: (copy.deepcopy(self.front.profile), "pure-model")})

    def test_original16_and_exact17_accepted(self):
        self.profile_gate(self.profile)
        old = dict(self.profile)
        del old["SharedScannerBridge"]
        self.profile_gate(old)

    def test_unknown_and_null_optional_refused(self):
        for change in [{"Unknown": True}, {"SharedScannerBridge": None}]:
            with self.assertRaises(Exception): self.profile_gate({**self.profile, **change})

    def test_shared_missing_extra_boolean_pid_refused(self):
        for shared in [{}, {**self.profile["SharedScannerBridge"], "Unknown": 1},
                       {**self.profile["SharedScannerBridge"], "ScannerPid": True}]:
            with self.assertRaises(Exception): self.profile_gate({**self.profile, "SharedScannerBridge": shared})

    def test_declared_scanner_pid_matches_int32_contract(self):
        with self.assertRaises(ValueError): self.profile_gate({**self.profile,
            "SharedScannerBridge": {**self.profile["SharedScannerBridge"], "ScannerPid": 2**31}})

    def test_shared_address_and_generation_refusals(self):
        for key, value in [("Subnet", "10.253.240.0/24"), ("Subnet", "192.168.0.0/28"),
                           ("Gateway", "10.253.240.2"), ("ScannerBridgeIp", "10.253.240.3"),
                           ("ScannerStarted", self.expiry), ("NetworkName", "other")]:
            with self.assertRaises(Exception): self.profile_gate({**self.profile,
                "SharedScannerBridge": {**self.profile["SharedScannerBridge"], key: value}})

    def test_wildcard_mode_refused(self):
        self.bridge._borrow.bind_owned_ipv4 = False
        with self.assertRaises(ValueError): self.builder.observe_shared_declaration(self.bridge)
        with self.assertRaises(ValueError): self.profile_gate({**self.profile,
            "Backend": {**self.profile["Backend"], "BindOwnedIpv4": False}})

    def test_two_phase_updates_only_file_after_observation(self):
        old = self.normal.specs
        observed = self.builder.install_observed_file_configuration(self.cap, self.bridge, "model", "d" * 64)
        updated = observed.spec
        self.assertEqual(updated.owner, "File")
        self.assertTrue(all(before is after for before, after in zip(old, self.normal.specs) if before.owner != "File"))
        self.assertEqual(updated.environment[self.h.SECTION + "__Admission__storageEndpointIdentity__kind"], "process")
        self.assertEqual(updated.environment[self.h.SECTION + "__Admission__scannerPort"], "1235")
        self.assertFalse(self.normal.owned)

    def test_admission_alias_refuses_without_publication(self):
        old = self.normal.specs
        file = next(spec for spec in old if spec.owner == "File")
        file.environment[self.h.SECTION + ":Enabled"] = "true"
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")
        self.assertIs(self.normal.specs, old)
        self.assertTrue(self.cap.failed and self.bridge._failure)

    def test_prior_runtime_fields_refuse(self):
        file = next(spec for spec in self.normal.specs if spec.owner == "File")
        file.environment[self.h.SECTION + "__Admission__scannerPort"] = "42"
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")

    def test_file_birth_prevents_configuration(self):
        self.normal.owned.append(object())
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")

    def test_shared_profile_substitution_refuses(self):
        self.front.profile["SharedScannerBridge"]["ScannerPid"] = 99
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")

    def test_unrelated_found_signature_refuses(self):
        original = self.bridge.scanner.scan
        def unrelated(payload):
            result = original(payload)
            if result == "infected": self.bridge.scanner.last_scan_reply = "stream: Unrelated-Synthetic-Signature FOUND"
            return result
        self.bridge.scanner.scan = unrelated
        with self.assertRaises(ValueError): self.builder.observe_scanner_database(self.bridge)

    def test_daemon_change_refuses_current_readiness(self):
        calls = []
        def observer(*args):
            calls.append(1)
            return {"pid": 22, "kernelStartTicks": len(calls), "listenerInode": "90"}
        self.h.scanner_process = observer
        with self.assertRaises(ValueError): self.builder.observe_scanner_database(self.bridge)

    def test_retained_full_readiness_digest(self):
        identity, evidence = self.builder.observe_scanner_database(self.bridge)
        self.assertEqual(identity["readinessReceiptSha256"], hashlib.sha256(
            json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()).hexdigest())
        for key in ("fileSourceSha", "resourceLeaseId", "scannerContainerId", "scannerImageDigest",
                    "versionReply", "benignReply", "eicarReply", "daemonProcessBefore", "daemonProcessAfter"):
            self.assertIn(key, evidence)
        observed = self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")
        self.assertNotIn("eicarReply", repr(observed))
        self.assertEqual(observed.private_scanner_readiness["eicarReply"], "stream: Eicar-Test-Signature FOUND")

    def test_changed_measurements_refuse(self):
        self.bridge.scanner.measure = lambda: {"databases": []}
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")

    def test_missing_timer_refuses(self):
        self.normal.timer_owned = False
        with self.assertRaises(ValueError): self.builder.install_observed_file_configuration(self.cap, self.bridge, "x", "x")

    def test_caller_retains_before_original_front_birth(self):
        self.front.process = None
        prepared = self.caller.prepare_file_front_startup(self.life, self.normal, self.front, self.bridge, "x", "x")
        self.assertIs(self.bridge.cap, prepared.capability)
        self.assertLess(self.events.index("retained-before-birth"), self.events.index("front-start"))

    def test_failure_retains_quarantined_originals(self):
        self.front.process = None
        self.front.start = lambda: (_ for _ in ()).throw(ValueError("private-model"))
        with self.assertRaises(ValueError): self.caller.prepare_file_front_startup(self.life, self.normal, self.front, self.bridge, "x", "x")
        self.assertTrue(self.bridge.cap.failed and self.bridge._failure)
        self.assertIs(self.bridge.cap.front, self.front)

    def test_prebirth_profile_mismatch_refuses_before_front_birth(self):
        self.front.process = None
        self.front.profile["SharedScannerBridge"]["ScannerPid"] = 99
        with self.assertRaises(ValueError): self.caller.prepare_file_front_startup(self.life, self.normal, self.front, self.bridge, "x", "x")
        self.assertNotIn("front-start", self.events)
        self.assertTrue(self.bridge.cap.failed and self.bridge._failure)

    def test_missing_authority_refuses_before_file_birth(self):
        with self.assertRaises(self.caller.MissingOrdinaryFileAuthority): self.caller.start_authenticated_file(self.cap, self.bridge)
        self.assertEqual(self.normal.owned, [])


if __name__ == "__main__":
    unittest.main()
