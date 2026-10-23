"""Two-phase configuration from original held objects. No allocations or authority synthesis."""
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import re
import time
from functools import wraps
from urllib.parse import urlsplit
from types import SimpleNamespace

import hosted_companion_resources as h
import held_host_lifetime as lifetime
import owned_normal_hosts as normal_source
import owned_storage_backend as storage
import hosted_scanner_readiness as scanner_source
from borrowed_scanner_bridge import BorrowedScannerBridge
from retained_file_front_consumers import RetainedFileFrontConsumers
from owned_front_host import admit_public_profile_shape
from actual_dotnet_start import observe_start
from regular_owned_files import verify_source


@dataclass(frozen=True)
class ObservedFileConfiguration:
    spec: object
    private_scanner_readiness: dict = field(repr=False)


def quarantine_configuration(function):
    @wraps(function)
    def guarded(capability, bridge, *args):
        try:
            return function(capability, bridge, *args)
        except BaseException:
            if type(capability) is RetainedFileFrontConsumers:
                capability.failed = True
            if type(bridge) is BorrowedScannerBridge:
                bridge._failure = True
            raise
    return guarded


def require_custody(capability, bridge):
    h.require(type(capability) is RetainedFileFrontConsumers and type(bridge) is BorrowedScannerBridge
              and type(capability.lifetime) is lifetime.HostLifetime
              and type(capability.normal) is lifetime.LifetimeNormalHosts
              and type(capability.front) is lifetime.LifetimeFrontHost,
              "Exact original retained owners required")
    h.require(bridge._retained_consumers() is capability and bridge.context is capability.context,
              "Original paired retention and context required")
    capability._custody()
    h.require(not capability.admitted and not capability.failed and not capability.released,
              "Preparatory retained phase required")
    capability._no_file_birth()


def observe_shared_declaration(bridge):
    """Actual source-owned observation, never caller-provided IDs or Engine snapshots."""
    h.require(type(bridge) is BorrowedScannerBridge and type(bridge._borrow) is storage.Lease,
              "Original borrowed storage lease required")
    lease = bridge._borrow
    h.require(lease.bind_owned_ipv4 is True, "Shared runtime requires owned private IPv4 bind opt-in")
    bridge.refresh()
    storage.observe(lease, bridge)
    backend = bridge._inspect("container", lease.container_id)
    scanning = bridge._inspect("container", lease.scanner_id)
    network = bridge._inspect("network", lease.network_id)
    bridge.scanner.validate_configured_network(network)
    configs = network["IPAM"]["Config"]
    h.require(type(configs) is list and len(configs) == 1, "One actual configured subnet required")
    attachments = scanning["NetworkSettings"]["Networks"]
    h.require(type(attachments) is dict and len(attachments) == 1, "One actual scanner bridge attachment required")
    attachment = next(iter(attachments.values()))
    h.require(attachment["NetworkID"] == lease.network_id, "Original scanner network differs")
    declaration = {"ScannerContainerId": scanning["Id"], "ScannerCreated": scanning["Created"],
        "ScannerStarted": scanning["State"]["StartedAt"], "ScannerPid": scanning["State"]["Pid"],
        "ScannerImageId": scanning["Image"], "ScannerBridgeIp": attachment["IPAddress"],
        "NetworkName": network["Name"], "Subnet": configs[0]["Subnet"], "Gateway": configs[0]["Gateway"]}
    public_backend = {"ContainerId": lease.container_id, "NetworkId": lease.network_id,
        "ImageReference": storage.IMAGE, "ImageId": storage.IMAGE_ID, "Created": lease.created,
        "Started": lease.started, "BridgeIp": lease.bridge_ip, "Port": lease.port,
        "ExecutablePath": "/bin/fake-gcs-server", "ExecutableSha256": lease.executable_sha,
        "Entrypoint": ["/bin/fake-gcs-server"], "Command": lease.arguments(), "RunId": lease.lease_id,
        "LeaseId": lease.lease_id, "GithubRunId": lease.run_id, "GithubAttempt": lease.attempt,
        "ExpiresUtc": lease.expires, "MemoryLimitBytes": backend["HostConfig"]["Memory"],
        "NanoCpus": backend["HostConfig"]["NanoCpus"], "KernelStartTicks": lease.kernel_ticks,
        "FileSourceSha": lease.file_sha, "ExpiresText": lease.expires,
        "NetworkCreated": lease.network_created, "IssuedUtc": lease.issued, "BindOwnedIpv4": True}
    bridge.refresh()
    return public_backend, declaration


def observe_scanner_database(bridge):
    """Actual original scanner protocol/measurement APIs; no historical readiness borrowing."""
    scanner = bridge.scanner
    h.require(type(scanner) is scanner_source.Scanner, "Exact original scanner required")
    bridge.refresh()
    before = scanner.measure()
    h.require(before == scanner.receipt["measurement"], "Immutable scanner measurements differ")
    version = scanner.command(b"VERSION", timeout=3)
    engine, loaded = h.parse_version(version)
    rows = before["databases"]
    files = {Path(row["path"]).name: row["sha256"] for row in rows}
    h.require(len(files) == len(rows) and 2 <= len(files) <= 3
              and {"main", "daily"} <= {Path(name).stem for name in files}
              and all(re.fullmatch(r"(?:main|daily|bytecode)\.(?:cvd|cld)", name)
                      and h.HEX64.fullmatch(value) for name, value in files.items()),
              "Exact actual database file set required")
    plan = h.ScannerPlan(before["executable"], before["executableSha256"],
                         (before["executable"], "--foreground", "--config-file=/etc/clamav/acceptance.conf"),
                         "/etc/clamav/acceptance.conf", before["configSha256"], files,
                         "/tmp/acceptance-clamd.log")
    plan.validate()
    observer_lease = SimpleNamespace(container_id=scanner.container_id, container_port=3310)
    daemon_before = h.scanner_process(observer_lease, plan)
    h.require(scanner.scan(scanner_source.CLEAN) == "clean", "Actual benign scanner control failed")
    benign = scanner.last_scan_reply
    h.require(benign == "stream: OK", "Exact actual benign scanner reply required")
    h.require(scanner.scan(scanner_source.EICAR) == "infected", "Actual EICAR scanner control failed")
    infected = scanner.last_scan_reply
    h.require(type(infected) is str and (
        re.fullmatch(r"stream: [^\r\n\x00]{1,160}Eicar[^\r\n\x00]{0,160} FOUND", infected, re.I) is not None
        or re.fullmatch(r"stream: Eicar[^\r\n\x00]{0,160} FOUND", infected, re.I) is not None),
        "Exact current EICAR scanner reply required")
    daily = next(row["path"] for row in rows if Path(row["path"]).stem == "daily")
    info = scanner.docker("exec", scanner.container_id, "sigtool", "--info", daily, timeout=3)
    h.require(re.findall(r"^Version: ([0-9]+)$", info, re.M) == [loaded], "Loaded daily version differs")
    h.require(scanner.measure() == before, "Scanner measurements changed during controls")
    daemon_after = h.scanner_process(observer_lease, plan)
    h.require(daemon_after == daemon_before, "Current original daemon ownership changed during controls")
    bridge.refresh()
    context = bridge.context
    observed = datetime.now(timezone.utc).isoformat()
    evidence = {"runId": context.run_id, "runAttempt": context.attempt, "fileSourceSha": context.file_sha,
        "resourceLeaseId": context.lease_id, "scannerContainerId": scanner.container_id,
        "scannerImageDigest": scanner.image_id, "versionReply": version, "benignReply": benign,
        "eicarReply": infected, "daemonProcessBefore": daemon_before, "daemonProcessAfter": daemon_after,
        "measurementBefore": before, "measurementAfter": scanner.measure(), "observedUtc": observed}
    h.require(evidence["measurementAfter"] == before, "Final scanner measurement changed")
    bridge.refresh()
    digest = hashlib.sha256(json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    identity = {"engineVersion": engine, "loadedDatabaseVersion": loaded,
                "databaseFilesSha256": files, "observedUtc": observed, "readinessReceiptSha256": digest}
    return identity, evidence


@quarantine_configuration
def install_observed_file_configuration(capability, bridge, observer_dll, observer_sha256):
    """Replace only File environment after original timer/front admission and before File birth."""
    require_custody(capability, bridge)
    normal, front, context = capability.normal, capability.front, capability.context
    h.require(normal.timer_owned and not normal.closed and not normal.cleanup_failures,
              "Existing original finite timer owner required")
    bridge.refresh()  # Source phase API performs original first listener binding.
    backend, shared = observe_shared_declaration(bridge)
    admit_public_profile_shape(front.profile)
    h.require(front.profile.get("SharedScannerBridge") == shared and front.profile["Backend"] == backend,
              "Actual shared declaration differs from retained front profile")
    selected = [spec for spec in normal.specs if spec.owner == "File"]
    h.require(len(selected) == 1 and len(normal.specs) == 8
              and {spec.owner for spec in normal.specs} == normal_source.OWNERS,
              "Original eight-host base graph required")
    spec = selected[0]
    h.require(type(spec) is normal_source.HostSpec and spec.source_sha == context.file_sha,
              "Exact qualified File source required")
    deadline = time.monotonic() + min(10, (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds())
    verify_source(spec, deadline)
    before = front.observe_listener()
    started = observe_start("Front", front.process, front.ticks, front.spec, front.actual_environment,
                            context, front.launcher_environment, front.profile, observer_dll,
                            observer_sha256, front.spec.profile_path)
    database, private_readiness = observe_scanner_database(bridge)
    parsed = urlsplit(front.profile["FrontOrigin"])
    relay = bridge.scanner.relay
    h.require(relay.endpoint[0] == "127.0.0.1" and relay.endpoint[1] == bridge.scanner.port
              and relay.endpoint != (parsed.hostname, parsed.port), "Distinct actual loopback endpoints required")
    origin = front.profile["FrontOrigin"].rstrip("/")
    h.origin(origin)
    admission = {"schemaVersion": 1, "runId": context.run_id, "runAttempt": context.attempt,
        "fileSourceSha": context.file_sha, "issuedUtc": context.issued_utc, "expiresUtc": context.expires_utc,
        "storageOrigin": origin, "storageEndpointIdentity": {"kind": "process", "hostIp": parsed.hostname,
            "hostPort": parsed.port, "pid": front.process.pid, "startedUtc": started["StartedUtc"],
            "executableAbsolutePath": front.spec.dotnet_executable, "executableSha256": front.spec.dotnet_sha256},
        "scannerHost": relay.endpoint[0], "scannerPort": relay.endpoint[1],
        "scannerContainerId": bridge.scanner_container_id, "scannerImageDigest": bridge.scanner.image_id,
        "scannerDatabaseIdentity": database, "resourceLeaseId": context.lease_id}
    flattened = h.configuration_environment({h.SECTION: {"Enabled": True, "Admission": admission}})
    prefix = (h.SECTION + "__").lower()
    existing = {key: value for key, value in spec.environment.items()
                if key.replace(":", "__").lower().startswith(prefix)}
    expected = {h.SECTION + "__Enabled": "true", h.SECTION + "__Admission__fileSourceSha": context.file_sha}
    h.require(existing == expected, "Only source-qualified base File admission may precede observed configuration")
    environment = dict(spec.environment)
    environment.update(flattened)
    updated = replace(spec, environment=environment)
    new_specs = tuple(updated if item is spec else item for item in normal.specs)
    normal_source.validate_specs(new_specs, context, normal.minimum_guard, normal_source.available_memory_bytes())
    h.require(front.observe_listener() == before, "Original front changed during composition")
    require_custody(capability, bridge)
    bridge.refresh()
    # Atomic publication occurs only after all source/live predicates pass.
    normal.specs = new_specs
    return ObservedFileConfiguration(updated, private_readiness)
