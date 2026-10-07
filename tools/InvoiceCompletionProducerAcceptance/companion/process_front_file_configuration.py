"""Successor File composition for a held process front and independent scanner network.

Preserves the older container-endpoint builder. Does not create resources or claim
bootstrap, quarantine absence, side-effect counts or joined financial acceptance.
"""
from datetime import datetime, timezone
from urllib.parse import urlsplit
import time

import hosted_companion_resources as h
from actual_dotnet_start import observe_start
from regular_owned_files import verify_source


def build_process_front_configuration(front, file_spec, scanner, scanner_plan,
                                      observer_dll, observer_sha256):
    context = front.context
    environment = front.launcher_environment
    now = datetime.now(timezone.utc)
    context.validate(environment, now)
    h.require(scanner.role == "scanner", "Actual owned scanner required")
    h.require(file_spec.source_sha == context.file_sha, "Selected File source differs from run admission")
    source_deadline = time.monotonic() + min(10, (h.instant(context.expires_utc) - now).total_seconds())
    verify_source(file_spec, source_deadline)
    source = {"fileSourceSha": file_spec.source_sha, "fileSourceTree": file_spec.source_tree,
              "fileExecutableSha256": file_spec.executable_sha256}
    before = front.observe_listener()
    parsed = urlsplit(front.profile["FrontOrigin"])
    h.require(scanner.host_ip == parsed.hostname and scanner.host_port != parsed.port,
              "Independent scanner and front endpoints required")
    # File's SDK endpoint is the held front process, never the unpublished bridge backend.
    h.require(front.profile["Backend"]["NetworkId"] != scanner.network_id
              and front.profile["Backend"]["ContainerId"] != scanner.container_id,
              "Private backend and scanner must have separate ownership/network identities")
    started = observe_start("Front", front.process, front.ticks, front.spec, front.actual_environment,
                            context, environment, front.profile, observer_dll, observer_sha256,
                            front.spec.profile_path)
    scanner_identity = h.observe(context, scanner, environment, now)
    database_identity, readiness = h.scanner_readiness(context, scanner, environment, now, scanner_plan)
    completed = datetime.now(timezone.utc)
    context.validate(environment, completed)
    h.observe(context, scanner, environment, completed)
    after = front.observe_listener()
    h.require(before == after, "Actual front changed during File composition")
    host = parsed.hostname if parsed.hostname == "127.0.0.1" else "[::1]"
    storage_origin = f"http://{host}:{parsed.port}"
    h.origin(storage_origin)
    endpoint = {"kind": "process", "hostIp": parsed.hostname, "hostPort": parsed.port,
                "pid": front.process.pid, "startedUtc": started["StartedUtc"],
                "executableAbsolutePath": front.spec.dotnet_executable,
                "executableSha256": front.spec.dotnet_sha256}
    admission = {"schemaVersion": 1, "runId": context.run_id, "runAttempt": context.attempt,
                 "fileSourceSha": context.file_sha, "issuedUtc": context.issued_utc, "expiresUtc": context.expires_utc,
                 "storageOrigin": storage_origin, "storageEndpointIdentity": endpoint,
                 "scannerHost": scanner.host_ip, "scannerPort": scanner.host_port,
                 "scannerContainerId": scanner.container_id,
                 "scannerImageDigest": scanner.image_reference.rsplit("@", 1)[1],
                 "scannerDatabaseIdentity": database_identity, "resourceLeaseId": context.lease_id}
    configuration = {h.SECTION: {"Enabled": True, "Admission": admission}}
    flattened = h.configuration_environment(configuration)
    h.require(flattened[h.SECTION + "__Admission__storageEndpointIdentity__kind"] == "process",
              "Actual process endpoint configuration was not preserved")
    front_lease = {"Pid": front.process.pid, "StartedUtc": started["StartedUtc"],
                   "KernelStartTicks": front.ticks, "ProfilePath": front.spec.profile_path,
                   "ProfileSha256": front.profile_hash, "DotnetExecutable": front.spec.dotnet_executable,
                   "DotnetSha256": front.spec.dotnet_sha256}
    receipt = {"sourceBuild": source, "frontIdentity": after, "frontReadLease": front_lease,
               "scannerIdentity": scanner_identity, "scannerReadiness": readiness,
               "observedUtc": completed.isoformat(), "genuineEightHostFinancialAccepted": False,
               "notProven": ["public File bootstrap", "File startup", "signed bytes",
                             "quarantine absence", "actual side-effect counters", "resource cleanup"]}
    return configuration, flattened, receipt
