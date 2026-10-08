"""Fixed C# command-control receipt admission; no actors or inferred cleanup."""
import json
import hashlib
import os
import re
import stat
import sys

MAX_BYTES = 16384
CASES = ("natural", "nonzero-next-read", "cancel", "stdout-cap", "stderr-cap")
ROOT_KEYS = frozenset(("SchemaVersion", "SourceHead", "RunId", "Attempt", "Case", "Passed",
    "Stage", "Category", "Observations", "ActualRegistryRetainedOwner",
    "OuterDeadlineOrParentDeathCleanupAccepted", "ObservedLoadedRuntime",
    "ActualBusinessGraphAccepted", "KernelResourceCapsObserved", "AllFdCensusAccepted",
    "ParentDeathCleanupAccepted", "InjectedSyscallFaultCasesQualified",
    "InheritedWriterOrUncertainCloseCasesQualified"))
BOOL_KEYS = frozenset(("BirthAttempted", "OriginalExited", "StdoutEof", "StderrEof",
    "OriginalTasksTerminal", "StdoutClosed", "StderrClosed", "PidfdAllocated", "PidfdClosed",
    "ProcessClosed", "MetadataClosed", "CleanupBudgetsClosed", "KernelGenerationBound",
    "ActualSignalInvoked", "ActualSignalSucceeded", "PhysicalReleased", "CleanupFailureRecorded",
    "RetainedOwner", "AdmissionSticky"))
OBS_KEYS = BOOL_KEYS | {"ExitCode", "CleanupAttempts", "StdoutBytesRead", "StderrBytesRead"}
FALSE_SCOPE = ("ActualRegistryRetainedOwner", "OuterDeadlineOrParentDeathCleanupAccepted",
    "ActualBusinessGraphAccepted", "KernelResourceCapsObserved", "AllFdCensusAccepted",
    "ParentDeathCleanupAccepted", "InjectedSyscallFaultCasesQualified",
    "InheritedWriterOrUncertainCloseCasesQualified")
PHYSICAL = ("BirthAttempted", "OriginalExited", "StdoutEof", "StderrEof", "OriginalTasksTerminal",
    "StdoutClosed", "StderrClosed", "ProcessClosed", "MetadataClosed", "CleanupBudgetsClosed",
    "PhysicalReleased")
BOUND_SIGNAL = ("KernelGenerationBound", "PidfdAllocated", "ActualSignalInvoked", "ActualSignalSucceeded")
SOURCE_PINS = (
    ("tools/InvoiceCompletionProducerAcceptance/companion/storage-front/BoundedOwnedCommand.cs",
        22801, "8ee8d3ba7b127f827c16f52ab735aa8d6ba7f4bb852b57b06e8274a7b248e2b4"),
    ("tools/HostedStorageCommand.NativeControls/Program.cs",
        10360, "323278cf293336d909a6cfd343dc22d0169146426ec6916a4bd1f87d1090650e"),
    ("tools/HostedStorageCommand.NativeControls/StorageCommandNative.csproj",
        567, "75f8275c263bbd08634ed7ae2727f3992698b181e93a548331b4c2e6f0f70ca0"),
)


class ReceiptRefused(ValueError):
    pass


def require(value):
    if not value:
        raise ReceiptRefused("Storage command receipt refused.")


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def canonical(value, maximum):
    require(type(value) is str and 0 < len(value) <= 19
        and re.fullmatch(r"[1-9][0-9]*", value) is not None)
    require(int(value) <= maximum)


def observation(row):
    require(type(row) is dict and set(row) == OBS_KEYS)
    require(all(type(row[key]) is bool for key in BOOL_KEYS))
    require(row["ExitCode"] is None or type(row["ExitCode"]) is int
        and -(2**31) <= row["ExitCode"] < 2**31)
    require((row["ExitCode"] is not None) == row["OriginalExited"])
    require(type(row["CleanupAttempts"]) is int and row["CleanupAttempts"] in (1, 2))
    # Original read plus one drain in each of the two source-limited cleanup attempts.
    for key, maximum in (("StdoutBytesRead", 3 * (64 + 4096)),
                         ("StderrBytesRead", 3 * (16384 + 4096))):
        require(type(row[key]) is int and 0 <= row[key] <= maximum)
    require(not row["PidfdClosed"] or row["PidfdAllocated"])
    require(not row["KernelGenerationBound"] or row["PidfdAllocated"])
    require(not row["ActualSignalSucceeded"] or row["ActualSignalInvoked"])
    require(not row["ActualSignalInvoked"] or row["KernelGenerationBound"])
    require(row["CleanupFailureRecorded"] == row["AdmissionSticky"])
    require(not row["PhysicalReleased"] or not row["RetainedOwner"])


def physical(row):
    require(all(row[key] is True for key in PHYSICAL)
        and type(row["ExitCode"]) is int and row["RetainedOwner"] is False
        and (not row["PidfdAllocated"] or row["PidfdClosed"]))


def clean(row, exit_code, stdout_bytes):
    physical(row)
    require(row["ExitCode"] == exit_code and row["StdoutBytesRead"] == stdout_bytes
        and row["StderrBytesRead"] == 0 and row["CleanupAttempts"] == 1
        and not row["ActualSignalInvoked"] and not row["ActualSignalSucceeded"]
        and not row["CleanupFailureRecorded"] and not row["AdmissionSticky"])


def validate_bytes(raw, head, run, attempt, case):
    require(type(raw) is bytes and 0 < len(raw) <= MAX_BYTES)
    require(type(head) is str and re.fullmatch(r"[0-9a-f]{40}", head) is not None)
    canonical(run, 2**63 - 1)
    canonical(attempt, 2**31 - 1)
    require(type(case) is str and case in CASES)
    try:
        row = json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=unique_pairs,
            parse_constant=lambda _: require(False))
    except (UnicodeError, ValueError, RecursionError) as error:
        raise ReceiptRefused("Storage command receipt refused.") from error
    require(type(row) is dict and set(row) == ROOT_KEYS)
    require(type(row["SchemaVersion"]) is int and row["SchemaVersion"] == 1
        and row["SourceHead"] == head and row["RunId"] == run and row["Attempt"] == attempt
        and row["Case"] == case and row["Passed"] is True
        and row["Stage"] == "completed" and row["Category"] == "None")
    require(all(row[key] is False for key in FALSE_SCOPE))
    require(type(row["ObservedLoadedRuntime"]) is str
        and re.fullmatch(r"10\.0\.[0-9]{1,6}(?:\.[0-9]{1,6})?", row["ObservedLoadedRuntime"]) is not None)
    records = row["Observations"]
    require(type(records) is list and 1 <= len(records) <= 2)
    for record in records:
        observation(record)
    if case == "natural":
        require(len(records) == 1)
        clean(records[0], 0, 10)
    elif case == "nonzero-next-read":
        require(len(records) == 2)
        clean(records[0], 7, 0)
        clean(records[1], 0, 10)
    else:
        require(all(record[key] is True for record in records for key in BOUND_SIGNAL))
        first, last = records[0], records[-1]
        require(first["CleanupAttempts"] == 1)
        if case == "cancel":
            require(all(record["StdoutBytesRead"] == 0 and record["StderrBytesRead"] == 0
                for record in records))
        elif case == "stdout-cap":
            require(first["StdoutBytesRead"] > 64
                and all(record["StderrBytesRead"] == 0 for record in records))
        else:
            require(first["StderrBytesRead"] > 16384
                and all(record["StdoutBytesRead"] == 0 for record in records))
        physical(last)
        if len(records) == 1:
            require(first["PhysicalReleased"] is True)
        else:
            require(first["PhysicalReleased"] is False and first["RetainedOwner"] is True
                and first["CleanupFailureRecorded"] is True and first["AdmissionSticky"] is True
                and last["CleanupAttempts"] == 2 and last["CleanupFailureRecorded"] is True
                and last["AdmissionSticky"] is True)
            for key in ("PidfdAllocated", "KernelGenerationBound", "ActualSignalInvoked", "ActualSignalSucceeded"):
                require(first[key] == last[key])
            for key in ("OriginalExited", "StdoutEof", "StderrEof", "OriginalTasksTerminal",
                        "StdoutClosed", "StderrClosed", "PidfdClosed", "ProcessClosed", "MetadataClosed"):
                require(not first[key] or last[key])
            require(first["ExitCode"] is None or first["ExitCode"] == last["ExitCode"])
            require(last["StdoutBytesRead"] >= first["StdoutBytesRead"]
                and last["StderrBytesRead"] >= first["StderrBytesRead"])
    return {"StorageCommandCaseAccepted": case, "ObservedManagedOriginalSettlement": True,
        "HistoricalCleanupFailureRetained": records[-1]["CleanupFailureRecorded"],
        "AllFdCensusAccepted": False, "ParentDeathCleanupAccepted": False,
        "ActualBusinessGraphAccepted": False}


def held_bytes(path, maximum=MAX_BYTES):
    require(type(maximum) is int and 0 < maximum <= 65536)
    descriptor = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and 0 < before.st_size <= maximum)
        data = bytearray()
        while len(data) <= maximum:
            block = os.read(descriptor, maximum + 1 - len(data))
            if not block:
                break
            data.extend(block)
        after = os.fstat(descriptor)
        require(len(data) == before.st_size and len(data) <= maximum)
        require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns)
            == (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns))
        return bytes(data)
    finally:
        os.close(descriptor)


def verify_sources(root):
    require(type(root) is str and 0 < len(root) <= 4096)
    for relative, length, digest in SOURCE_PINS:
        raw = held_bytes(os.path.join(root, *relative.split("/")), length)
        require(len(raw) == length and hashlib.sha256(raw).hexdigest() == digest)
    return {"FrozenCSharpSourcePinsVerified": 3, "ActualNativeExecutionAccepted": False}


def main():
    try:
        if len(sys.argv) == 3 and sys.argv[1] == "--sources":
            print(json.dumps(verify_sources(sys.argv[2]), separators=(",", ":")))
            return 0
        require(len(sys.argv) == 6)
        result = validate_bytes(held_bytes(sys.argv[1]), *sys.argv[2:])
        print(json.dumps(result, separators=(",", ":")))
        return 0
    except (ReceiptRefused, OSError, AttributeError, OverflowError):
        print("STORAGE_COMMAND_RECEIPT_REFUSED")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
