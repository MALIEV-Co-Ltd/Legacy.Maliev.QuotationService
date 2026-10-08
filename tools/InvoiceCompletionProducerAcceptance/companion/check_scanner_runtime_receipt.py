"""Bounded strict scanner observations only; no financial or kernel enforcement proof."""
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import stat


MAXIMUM = 65536
PHASES = ("start", "current", "preStop", "preRemoval")
TOP_KEYS = frozenset(("schemaVersion", "runId", "imageReference", "genuineEightHostFinancialAccepted",
                      "resources", "scannerReady", "cleanupVerified", "hostedIdentity", "stage", "imageId",
                      "derivedImage", "network", "allocationIssuedUtc", "startupDiagnostic", "containerGeneration",
                      "portIsolation", "loopbackRelay", "runtimePolicy", "measurement", "version", "daemonProcess",
                      "scannerPlan", "parentAdmission", "endpoint", "controls", "counters", "counterScope",
                      "stoppedContainerObserved", "cleanupErrors", "runtimeBoundaryObservations", "startupRelayHandoff"))


class ReceiptRefused(ValueError):
    pass


def require(value):
    if not value:
        raise ReceiptRefused("Scanner runtime receipt refused")


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def object_keys(value, keys):
    require(type(value) is dict and set(value) == set(keys))


def isolation(value):
    object_keys(value, ("declaredPortBindings", "publishAllPorts", "actualPortBindings",
                        "dockerPortPublicationObserved", "absenceVerified"))
    require(value["declaredPortBindings"] is None or type(value["declaredPortBindings"]) is dict
            and value["declaredPortBindings"] == {})
    actual = value["actualPortBindings"]
    require(actual is None or type(actual) is dict and all(type(key) is str and re.fullmatch(r"[0-9]{1,5}/(?:tcp|udp)", key)
                                                        and item is None for key, item in actual.items()))
    require(value["publishAllPorts"] is False and value["dockerPortPublicationObserved"] is False
            and value["absenceVerified"] is True)


def policy(value):
    object_keys(value, ("memoryBytes", "nanoCpus", "capDrop", "capAdd", "readOnlyRoot",
                        "engineConfigurationObserved", "kernelEnforcementObserved"))
    require(type(value["memoryBytes"]) is int and value["memoryBytes"] == 1610612736)
    require(type(value["nanoCpus"]) is int and value["nanoCpus"] == 2000000000)
    require(type(value["capDrop"]) is list and value["capDrop"] == ["ALL"])
    require(value["capAdd"] is None or type(value["capAdd"]) is list and value["capAdd"] == [])
    require(value["readOnlyRoot"] is True and value["engineConfigurationObserved"] is True
            and value["kernelEnforcementObserved"] is False)


def receipt_shapes(value):
    shapes = {
        "derivedImage": ("owned", "runLabel", "dockerfileSha256", "imageId", "baseImageId", "buildCompleted", "cleanupVerified"),
        "network": ("name", "owned", "allocationIssuedUtc", "networkId", "cleanupVerified"),
        "startupDiagnostic": ("containerId", "observedUtc", "state", "createdUtc", "declaredPortBindings",
                              "actualPortBindings", "networkMode", "networkIds", "observed", "runtimeBoundaryObserved",
                              "startupLogTail", "startupLogTailSha256", "startupLogTailTruncated"),
        "runtimePolicy": ("readOnlyRoot", "persistentVolumes", "directEntrypoint", "memoryBytes"),
        "measurement": ("executable", "executableSha256", "configSha256", "databases"),
        "daemonProcess": ("pid", "kernelStartTicks", "listenerInode", "executable", "executableSha256", "argv",
                          "configurationSha256", "databaseDirectory"),
        "scannerPlan": ("executable", "executable_sha256", "argv", "configuration", "configuration_sha256",
                        "database_files_sha256", "startup_log"),
        "loopbackRelay": ("host", "port", "ownerPid", "listenerFd", "maximumConnectionSeconds", "maximumPayloadBytes",
                          "maximumConcurrentConnections", "listenerSocket", "ownerKernelStartTicks", "backend",
                          "portIsolation", "ownedInProcess", "cleanupVerified", "listenerClosed", "ownedListenerInodeAbsent",
                          "connectionFailureTypes", "acceptorStopped"),
    }
    for key, fields in shapes.items():
        object_keys(value[key], fields)
    object_keys(value["startupDiagnostic"]["state"], ("Status", "Running", "Paused", "Restarting", "OOMKilled", "Dead",
                                                    "Pid", "ExitCode", "Error", "StartedAt", "FinishedAt"))
    object_keys(value["loopbackRelay"]["backend"], ("containerId", "networkId", "endpointId", "host", "port", "createdUtc", "startedUtc"))
    require(type(value["startupDiagnostic"]["networkIds"]) is dict
            and set(value["startupDiagnostic"]["networkIds"]) == {value["network"]["name"]}
            and value["startupDiagnostic"]["networkIds"][value["network"]["name"]] == value["network"]["networkId"])
    state = value["startupDiagnostic"]["state"]
    require(state["OOMKilled"] is False and state["Dead"] is False and state["Error"] == ""
            and type(state["Pid"]) is int and state["Pid"] > 0 and type(state["ExitCode"]) is int and state["ExitCode"] == 0)
    diagnostic = value["startupDiagnostic"]
    require(type(diagnostic["startupLogTail"]) is str and len(diagnostic["startupLogTail"]) <= 8192
            and type(diagnostic["startupLogTailTruncated"]) is bool
            and type(diagnostic["startupLogTailSha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", diagnostic["startupLogTailSha256"]))
    if diagnostic["startupLogTailTruncated"] is False:
        require(hashlib.sha256(diagnostic["startupLogTail"].encode()).hexdigest() == diagnostic["startupLogTailSha256"])
    require(value["runtimePolicy"]["readOnlyRoot"] is True and type(value["runtimePolicy"]["persistentVolumes"]) is int
            and value["runtimePolicy"]["persistentVolumes"] == 0 and value["runtimePolicy"]["directEntrypoint"] == "/usr/sbin/clamd"
            and type(value["runtimePolicy"]["memoryBytes"]) is int and value["runtimePolicy"]["memoryBytes"] == 1610612736)
    rows = value["measurement"]["databases"]
    require(type(rows) is list and 1 <= len(rows) <= 16)
    hashes = {}
    for row in rows:
        object_keys(row, ("path", "sha256"))
        require(type(row["path"]) is str and re.fullmatch(r"/var/lib/clamav/[A-Za-z0-9._-]{1,80}\.(?:cvd|cld)", row["path"])
                and type(row["sha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", row["sha256"]))
        name = row["path"].rsplit("/", 1)[1]
        require(name not in hashes)
        hashes[name] = row["sha256"]
    require(type(value["scannerPlan"]["database_files_sha256"]) is dict and value["scannerPlan"]["database_files_sha256"] == hashes)
    for group, keys in (("daemonProcess", ("pid", "kernelStartTicks")), ("loopbackRelay", ("ownerPid", "ownerKernelStartTicks"))):
        require(all(type(value[group][key]) is int and value[group][key] > 0 for key in keys))
    require(type(value["loopbackRelay"]["listenerFd"]) is int and value["loopbackRelay"]["listenerFd"] >= 0
            and type(value["loopbackRelay"]["listenerSocket"]) is str and re.fullmatch(r"socket:\[[0-9]{1,20}\]", value["loopbackRelay"]["listenerSocket"]))
    require(type(value["daemonProcess"]["listenerInode"]) is str and re.fullmatch(r"[0-9]{1,20}", value["daemonProcess"]["listenerInode"]))
    for key in ("executableSha256", "configSha256"):
        require(type(value["measurement"][key]) is str and re.fullmatch(r"[0-9a-f]{64}", value["measurement"][key]))
    require(value["measurement"]["executable"] == value["daemonProcess"]["executable"] == value["scannerPlan"]["executable"] == "/usr/sbin/clamd")
    require(value["measurement"]["executableSha256"] == value["daemonProcess"]["executableSha256"] == value["scannerPlan"]["executable_sha256"])
    require(value["measurement"]["configSha256"] == value["daemonProcess"]["configurationSha256"] == value["scannerPlan"]["configuration_sha256"])
    require(value["daemonProcess"]["argv"] == value["scannerPlan"]["argv"] == ["/usr/sbin/clamd", "--foreground", "--config-file=/etc/clamav/acceptance.conf"])
    require(value["daemonProcess"]["databaseDirectory"] == "/var/lib/clamav" and value["scannerPlan"]["configuration"] == "/etc/clamav/acceptance.conf"
            and value["scannerPlan"]["startup_log"] == "/tmp/acceptance-clamd.log")
    require(type(value["loopbackRelay"]["connectionFailureTypes"]) is list and len(value["loopbackRelay"]["connectionFailureTypes"]) <= 256
            and all(type(item) is str and re.fullmatch(r"[A-Za-z][A-Za-z0-9]{0,79}", item) for item in value["loopbackRelay"]["connectionFailureTypes"]))


def startup_handoff(value):
    handoff = value["startupRelayHandoff"]
    object_keys(handoff, ("originalStartupAttempts", "originalSettledConnectRefusals",
                          "originalStartupPongObserved", "originalStartupWorkersSettled"))
    attempts = handoff["originalStartupAttempts"]
    refusals = handoff["originalSettledConnectRefusals"]
    require(type(attempts) is int and 1 <= attempts <= 16)
    require(type(refusals) is int and 0 <= refusals <= 15 and attempts == refusals + 1)
    require(handoff["originalStartupPongObserved"] is True and handoff["originalStartupWorkersSettled"] is True)
    # Startup rows are retained unchanged. The separate, required post-stop
    # probe reaches the original stopped-container validator and adds ValueError.
    # These observations do not qualify caller graphs or physical kernel caps.
    require(value["loopbackRelay"]["connectionFailureTypes"]
            == ["ConnectionRefusedError"] * refusals + ["ValueError"])


def validate_bytes(raw, run, attempt, head, event_sha):
    require(type(raw) is bytes and 0 < len(raw) <= MAXIMUM)
    require(type(run) is str and re.fullmatch(r"[1-9][0-9]{0,19}", run) and int(run) <= 2**63 - 1)
    require(type(attempt) is str and re.fullmatch(r"[1-9][0-9]{0,9}", attempt) and int(attempt) <= 2**31 - 1)
    require(type(head) is str and re.fullmatch(r"[0-9a-f]{40}", head))
    require(type(event_sha) is str and re.fullmatch(r"[0-9a-f]{40}", event_sha))
    def nonfinite(_):
        raise ReceiptRefused("Scanner runtime receipt refused")
    value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs, parse_constant=nonfinite)
    require(type(value) is dict and (set(value) == TOP_KEYS or set(value) == TOP_KEYS | {"runtimeBoundaryFailures"}))
    require(value.get("runtimeBoundaryFailures", []) == [] and type(value.get("runtimeBoundaryFailures", [])) is list)
    require(type(value["schemaVersion"]) is int and value["schemaVersion"] == 1)
    object_keys(value["hostedIdentity"], ("runId", "attempt", "head", "eventSha"))
    require(value["hostedIdentity"] == {"runId": run, "attempt": attempt, "head": head, "eventSha": event_sha})
    receipt_shapes(value)
    startup_handoff(value)
    require(value["imageReference"] == "clamav/clamav@sha256:7659dcb0db47941d3cf8336af84bbb63c7e70b76fc00601774358412b42ed186")
    require(value["counterScope"] == "Actual readiness control INSTREAM calls; financial File runtime counters remain unobserved")
    require(value["stage"] == "actual-scanner-controls-complete" and value["scannerReady"] is True)
    require(value["genuineEightHostFinancialAccepted"] is False and value["parentAdmission"] is False)
    require(value["cleanupVerified"] is True and type(value["cleanupErrors"]) is list and value["cleanupErrors"] == [])
    require(value["stoppedContainerObserved"] is True)
    object_keys(value["controls"], ("clean", "eicar", "unavailableAfterStop"))
    require(all(flag is True for flag in value["controls"].values()))
    object_keys(value["counters"], ("attempted", "clean", "infected"))
    require(all(type(item) is int for item in value["counters"].values()))
    require(value["counters"] == {"attempted": 2, "clean": 1, "infected": 1})
    require(type(value["runId"]) is str and re.fullmatch(r"[a-zA-Z0-9-]{1,64}", value["runId"]))
    resources = value["resources"]
    require(type(resources) is list and len(resources) == 1)
    object_keys(resources[0], ("kind", "name", "owned", "containerId"))
    owner = resources[0]
    require(owner["kind"] == "container" and owner["owned"] is True
            and owner["name"] == "financial-scanner-" + value["runId"])
    require(type(owner["containerId"]) is str and re.fullmatch(r"[0-9a-f]{64}", owner["containerId"]))
    derived = value["derivedImage"]
    require(type(derived) is dict and derived.get("owned") is True and derived.get("buildCompleted") is True
            and derived.get("cleanupVerified") is True and derived.get("runLabel") == value["runId"])
    require(type(derived.get("imageId")) is str and re.fullmatch(r"sha256:[0-9a-f]{64}", derived["imageId"]))
    require(derived.get("baseImageId") == value["imageId"])
    generation = value["containerGeneration"]
    object_keys(generation, ("createdUtc", "startedUtc"))
    require(all(type(item) is str and 0 < len(item) <= 64 for item in generation.values()))
    observations = value["runtimeBoundaryObservations"]
    object_keys(observations, PHASES)
    for phase in PHASES:
        snapshot = observations[phase]
        object_keys(snapshot, ("containerId", "imageId", "runLabel", "createdUtc", "startedUtc", "policy", "portIsolation"))
        require(snapshot["containerId"] == owner["containerId"] and snapshot["imageId"] == derived["imageId"]
                and snapshot["runLabel"] == value["runId"] and snapshot["createdUtc"] == generation["createdUtc"]
                and snapshot["startedUtc"] == generation["startedUtc"])
        policy(snapshot["policy"])
        isolation(snapshot["portIsolation"])
    isolation(value["portIsolation"])
    diagnostic = value["startupDiagnostic"]
    require(type(diagnostic) is dict and diagnostic.get("observed") is True
            and diagnostic.get("runtimeBoundaryObserved") is True and diagnostic.get("containerId") == owner["containerId"]
            and diagnostic.get("createdUtc") == generation["createdUtc"])
    require(not any(key.endswith("ErrorType") for key in diagnostic))
    state = diagnostic.get("state", {})
    require(state.get("Running") is True and state.get("Paused") is False and state.get("Restarting") is False
            and state.get("StartedAt") == generation["startedUtc"])
    require(diagnostic.get("declaredPortBindings") == observations["start"]["portIsolation"]["declaredPortBindings"]
            and diagnostic.get("actualPortBindings") == observations["start"]["portIsolation"]["actualPortBindings"])
    network = value["network"]
    require(type(network) is dict and network.get("owned") is True and network.get("cleanupVerified") is True
            and network.get("name") == owner["name"] + "-network")
    require(type(network.get("networkId")) is str and re.fullmatch(r"[0-9a-f]{64}", network["networkId"]))
    relay = value["loopbackRelay"]
    require(type(relay) is dict and relay.get("host") == "127.0.0.1" and relay.get("ownedInProcess") is True)
    require(all(relay.get(key) is True for key in ("cleanupVerified", "listenerClosed", "ownedListenerInodeAbsent", "acceptorStopped")))
    require(type(relay.get("port")) is int and 1 <= relay["port"] <= 65535)
    require(relay.get("maximumConnectionSeconds") == 10 and type(relay.get("maximumConnectionSeconds")) is int
            and relay.get("maximumConcurrentConnections") == 4 and type(relay.get("maximumConcurrentConnections")) is int
            and relay.get("maximumPayloadBytes") == 209715200 and type(relay.get("maximumPayloadBytes")) is int)
    backend = relay.get("backend", {})
    require(backend.get("containerId") == owner["containerId"] and backend.get("networkId") == network["networkId"]
            and backend.get("createdUtc") == generation["createdUtc"] and backend.get("startedUtc") == generation["startedUtc"])
    isolation(relay.get("portIsolation"))
    object_keys(value["endpoint"], ("host", "port"))
    require(value["endpoint"] == {"host": "127.0.0.1", "port": relay["port"]})
    return {"ScannerRuntimeObservedPhases": 4, "ScannerControlsPassed": 3, "OwnedCleanupVerified": True,
            "KernelEnforcementObserved": False, "GenuineEightHostFinancialAccepted": False}


def bounded_read(stream):
    raw = stream.read(MAXIMUM + 1)
    require(type(raw) is bytes and 0 < len(raw) <= MAXIMUM)
    return raw


def held_receipt_read(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and 0 < before.st_size <= MAXIMUM)
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            raw = bounded_read(stream)
        after = os.fstat(descriptor)
        require(stat.S_ISREG(after.st_mode) and len(raw) == before.st_size)
        require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns)
                == (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns))
        return raw
    finally:
        os.close(descriptor)


def main():
    try:
        path = Path("TestResults/HostedScanner/scanner-readiness.json")
        require(not any(item.is_symlink() or getattr(item, "is_junction", lambda: False)() for item in (path, *path.parents)))
        raw = held_receipt_read(path)
        result = validate_bytes(raw, os.environ.get("GITHUB_RUN_ID"), os.environ.get("GITHUB_RUN_ATTEMPT"),
                                os.environ.get("FINANCIAL_SOURCE_HEAD"), os.environ.get("GITHUB_SHA"))
        print(json.dumps(result, sort_keys=True))
        return 0
    except Exception:
        print("ScannerRuntimeReceiptRefused", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
