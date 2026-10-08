"""Join actual opt-in serializer bytes to a protected raw-candidate source capsule."""
import argparse
from contextlib import contextmanager
import hashlib
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET

import materialize_quotation_candidate as transport
from check_c821_focused_results import case_hash, validate_bytes

SOURCE_PATHS = (
    "Legacy.Maliev.QuotationService.Application/Models/QuotationModels.cs",
    "Legacy.Maliev.QuotationService.Api/Controllers/QuotationRequestsController.cs",
    "Legacy.Maliev.QuotationService.Api/Program.cs",
    "tools/QualificationOutcomeWireSource/QualificationOutcomeWire.cs",
    "tools/QualificationOutcomeWireSource/Program.cs",
    "tools/QualificationOutcomeWireSource/ChildStartObservation.cs",
    "Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj",
    "Legacy.Maliev.QuotationService.Tests/Controllers/QualificationOutcomeWireSourceTests.cs",
)
TEST_SOURCE = SOURCE_PATHS[-1]
TEST_ASSEMBLY = "Legacy.Maliev.QuotationService.Tests"
CLASS = TEST_ASSEMBLY + ".Controllers.QualificationOutcomeWireSourceTests"
METHOD = "ActualControllerSerializer_EmitsReviewedSyntheticWire"
ASSEMBLIES = {
    "Legacy.Maliev.QuotationService.Application": "Legacy.Maliev.QuotationService.Application",
    "Legacy.Maliev.QuotationService.Api": "Legacy.Maliev.QuotationService.Api",
    TEST_ASSEMBLY: TEST_ASSEMBLY,
}
BOUNDARY = "actual DTO + actual controller JsonResult + MVC executor; synthetic service; no routing/authentication/database proof"
OUTPUT_FILES = {"empty.json", "mixed.json", "empty.metadata.json", "mixed.metadata.json", "qualification-wire.trx"}
RECEIPT_NAME = "qualification-candidate-wire-receipt.json"
POLICY_FIELDS = {"qualificationScope", "acceptedBase", "sourcePins", "sourceFiles", "manifestSha256"}
MATERIALIZATION_FIELDS = {"manifestSha256", "manifestBlob", "capsuleBlob", "acceptedBase", "sourcePins", "sourceFiles", "transportCommit", "nativeValidated"}
METADATA_FIELDS = {"caseName", "statusCode", "contentType", "camelCase", "ignoreCondition", "actualDtoType", "actualMvcExecutorType", "actualHarnessType", "actualHarnessAssembly", "assemblies"}
POLICY_NAMES = {"quotation-admission-race-policy.json", "quotation-fixture-corrected-policy.json"}
WIRE_SCOPES = {"admission-race-wire", "fixture-residual-wire"}
WIRE_FINGERPRINTS = {
    "empty": (79, "579594414026a97d4068e93ecd2cd84504aa4df60bf356cd094b02948cea1c69"),
    "mixed": (432, "cc634575857b86f39ef4931931c009014a6a62e0e2a989db7f172dbcea4be7c1"),
}
MAX_JSON = 1024 * 1024


def digest(data):
    return hashlib.sha256(data).hexdigest()


def checked_path(path, root=None):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.is_symlink() or getattr(item, "is_junction", lambda: False)():
            raise ValueError("linked evidence/source path denied")
    resolved = path.resolve(strict=True)
    if root is not None and not resolved.is_relative_to(root):
        raise ValueError("evidence/source path escaped owned root")
    return resolved


def identity(info):
    return info.st_dev, info.st_ino


@contextmanager
def anchored_parent(path):
    """Retain no-follow directory descriptors and witness every absolute ancestor."""
    path = Path(path).absolute()
    descriptors, witnesses = [], []
    try:
        if sys.platform == "linux":
            flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_NONBLOCK | getattr(os, "O_CLOEXEC", 0)
            current = Path(path.anchor)
            descriptor = os.open(str(current), flags)
            descriptors.append(descriptor)
            witnesses.append((current, identity(os.fstat(descriptor))))
            for component in path.parent.parts[1:]:
                descriptor = os.open(component, flags, dir_fd=descriptor)
                descriptors.append(descriptor)
                current = current / component
                witnesses.append((current, identity(os.fstat(descriptor))))
            parent_fd = descriptor
        else:
            # Portable test/development fallback; production CLI requires Linux anchored traversal.
            parent_fd = None
            for current in reversed(path.parent.parents):
                witnesses.append((current, identity(current.lstat())))
            witnesses.append((path.parent, identity(path.parent.lstat())))

        def verify_chain():
            for current, expected in witnesses:
                info = current.lstat()
                if (not stat.S_ISDIR(info.st_mode) or current.is_symlink()
                        or getattr(current, "is_junction", lambda: False)() or identity(info) != expected):
                    raise ValueError("owned evidence ancestor changed")
        verify_chain()
        yield parent_fd, verify_chain
        verify_chain()
    except OSError as error:
        raise ValueError("owned evidence descriptor/path unavailable") from error
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)



def read_file(path, root, limit=MAX_JSON):
    path = checked_path(path, root)
    flags = os.O_RDONLY | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_BINARY", 0)
    with anchored_parent(path) as (parent_fd, verify_chain):
        descriptor = None
        try:
            descriptor = os.open(path.name if parent_fd is not None else str(path), flags,
                                 **({"dir_fd": parent_fd} if parent_fd is not None else {}))
            before = os.fstat(descriptor)
            if not stat.S_ISREG(before.st_mode) or before.st_size > limit:
                raise ValueError("nonregular/oversized opened evidence")
            data = bytearray()
            while len(data) <= limit:
                chunk = os.read(descriptor, min(65536, limit + 1 - len(data)))
                if not chunk:
                    break
                data.extend(chunk)
            after = os.fstat(descriptor)
            verify_chain()
            linked = path.lstat()
            if (len(data) > limit or identity(before) != identity(linked) or not stat.S_ISREG(linked.st_mode)
                    or (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns)):
                raise ValueError("opened evidence changed or exceeded bound")
            return bytes(data)
        except OSError as error:
            raise ValueError("opened evidence unavailable") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)


def create_receipt(path, data):
    if len(data) > MAX_JSON:
        raise ValueError("candidate wire receipt exceeds bound")
    path = Path(path).absolute()
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    with anchored_parent(path) as (parent_fd, verify_chain):
        descriptor = None
        try:
            descriptor = os.open(path.name if parent_fd is not None else str(path), flags, 0o600,
                                 **({"dir_fd": parent_fd} if parent_fd is not None else {}))
            opened = os.fstat(descriptor)
            if not stat.S_ISREG(opened.st_mode):
                raise ValueError("receipt destination is not a regular file")
            verify_chain()
            offset = 0
            while offset < len(data):
                written = os.write(descriptor, data[offset:])
                if not written:
                    raise ValueError("candidate receipt write incomplete")
                offset += written
            verify_chain()
            if identity(path.lstat()) != identity(opened):
                raise ValueError("candidate receipt path changed")
        except OSError as error:
            raise ValueError("fresh candidate receipt creation failed") from error
        finally:
            if descriptor is not None:
                os.close(descriptor)


def object_file(path, root):
    data = read_file(path, root)
    value = transport.parse_json(data)
    if not isinstance(value, dict):
        raise ValueError("JSON object evidence required")
    return value, data


def git(root, *arguments):
    return subprocess.check_output(["git", "-C", str(root), *arguments], timeout=10)


def oid(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("invalid source/transport identity")
    return value


def hosted_context(root, env):
    if checked_path(env.get("GITHUB_WORKSPACE", "")) != root:
        raise ValueError("outer hosted workspace differs")
    sha = oid(env.get("GITHUB_SHA"))
    if git(root, "rev-parse", "HEAD").decode().strip() != sha:
        raise ValueError("actual protected transport HEAD differs")
    run, attempt = env.get("GITHUB_RUN_ID", ""), env.get("GITHUB_RUN_ATTEMPT", "")
    if not re.fullmatch(r"[1-9][0-9]{0,19}", run) or not re.fullmatch(r"[1-9][0-9]{0,8}", attempt):
        raise ValueError("actual hosted run/attempt missing")
    if env.get("GITHUB_REPOSITORY") != transport.REPOSITORY:
        raise ValueError("hosted repository differs")
    return sha, run, int(attempt)


def validate_metadata(value, case_name):
    if set(value) != METADATA_FIELDS or value.get("caseName") != case_name:
        raise ValueError("unexpected serializer metadata schema/case")
    if (type(value["statusCode"]) is not int or value["statusCode"] != 200
            or value["contentType"] != "application/json; charset=utf-8"
            or value["camelCase"] is not True or value["ignoreCondition"] != "WhenWritingNull"
            or value["actualDtoType"] != "Legacy.Maliev.QuotationService.Application.Models.QualificationOutcomeReadback"
            or value["actualMvcExecutorType"] != "Microsoft.AspNetCore.Mvc.Infrastructure.SystemTextJsonResultExecutor"
            or value["actualHarnessType"] != "QualificationOutcomeWireSource.QualificationOutcomeWire"
            or value["actualHarnessAssembly"] != TEST_ASSEMBLY):
        raise ValueError("actual controller serializer metadata differs")
    assemblies = value["assemblies"]
    if (not isinstance(assemblies, list) or len(assemblies) != len(ASSEMBLIES)
            or any(not isinstance(row, dict) or set(row) != {"name", "sha256"}
                   or not isinstance(row["name"], str) or row["name"] not in ASSEMBLIES or not isinstance(row["sha256"], str)
                   or not re.fullmatch(r"[0-9a-f]{64}", row["sha256"]) for row in assemblies)
            or len({row["name"] for row in assemblies}) != len(ASSEMBLIES)):
        raise ValueError("actual loaded assembly inventory differs")
    return {row["name"]: row["sha256"] for row in assemblies}


def verify(candidate, policy_path, materialization_path, env=None):
    env = os.environ if env is None else env
    lexical_policy = Path(policy_path).absolute()
    root = checked_path(lexical_policy.parents[1])
    if lexical_policy.parent != root / "scripts" or lexical_policy.name not in POLICY_NAMES:
        raise ValueError("protected policy path differs")
    if Path(candidate).absolute() != root / "candidate" or Path(materialization_path).absolute() != root / "evidence/source-materialization.json":
        raise ValueError("candidate/materialization path differs")
    candidate = checked_path(candidate, root)
    head, run, attempt = hosted_context(root, env)
    policy, policy_bytes = object_file(policy_path, root)
    materialization, materialization_bytes = object_file(materialization_path, root)
    if set(policy) != POLICY_FIELDS or policy["qualificationScope"] not in WIRE_SCOPES:
        raise ValueError("protected policy schema/scope differs")
    oid(policy["acceptedBase"])
    if (set(materialization) != MATERIALIZATION_FIELDS or materialization["nativeValidated"] is not False
            or materialization["transportCommit"] != head
            or any(materialization[key] != policy[key] for key in ("manifestSha256", "acceptedBase", "sourcePins", "sourceFiles"))):
        raise ValueError("materialization receipt differs from protected policy")
    # Obtain the actual immutable blobs again; the earlier receipt alone is not a capsule witness.
    manifest_bytes = transport.fetch_blob(oid(materialization["manifestBlob"]))
    capsule_bytes = transport.fetch_blob(oid(materialization["capsuleBlob"]))
    _, capsule_files = transport.validate_capsule(manifest_bytes, capsule_bytes, policy)
    transport.verify_source(candidate, policy)
    overrides = {row["path"]: row for row in policy["sourceFiles"]}
    if TEST_SOURCE not in overrides:
        raise ValueError("opt-in serializer test source is not explicitly reviewed")
    sources = []
    snapshots = {}
    for path in SOURCE_PATHS:
        data = read_file(candidate / path, candidate, transport.MAX_FILE_BYTES)
        if path in overrides:
            if data != capsule_files[path]:
                raise ValueError("raw override source differs from capsule")
            kind = "raw-override"
        else:
            base = git(candidate, "show", policy["acceptedBase"] + ":" + path)
            if data != base:
                raise ValueError("inherited emission source differs from accepted base")
            kind = "inherited-base"
        snapshots[candidate / path] = data
        sources.append({"path": path, "sha256": digest(data), "bytes": len(data), "kind": kind})
    output = checked_path(candidate / "TestResults/QualificationWire", candidate)
    if {p.name for p in output.iterdir()} != OUTPUT_FILES:
        raise ValueError("missing/stale/extra candidate wire outputs")
    trx_path = output / "qualification-wire.trx"
    trx_bytes = read_file(trx_path, candidate, 4 * MAX_JSON)
    specification = {"className": CLASS, "methods": {METHOD: {"parameters": [{"name": "caseName", "type": "string"}],
                    "cases": [case_hash(["empty"]), case_hash(["mixed"])]}}}
    validate_bytes(trx_bytes, specification, trx_path.name)
    ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    for method in ET.fromstring(trx_bytes).findall(ns + "TestDefinitions/" + ns + "UnitTest/" + ns + "TestMethod"):
        if (Path(method.get("codeBase", "")).absolute() != candidate / TEST_ASSEMBLY / "bin/Release/net10.0" / (TEST_ASSEMBLY + ".dll")
                or method.get("adapterTypeName") != "executor://xunit/VsTestRunner3/netcore/"):
            raise ValueError("actual native test assembly/adapter differs")
    snapshots[trx_path] = trx_bytes
    cases = []
    assembly_map = None
    for name in ("empty", "mixed"):
        data = read_file(output / (name + ".json"), candidate, 64 * 1024)
        # Bytes are retained from the actual C# emission; Python never reconstructs serializer bytes.
        parsed = transport.parse_json(data)
        if (len(data), digest(data)) != WIRE_FINGERPRINTS[name]:
            raise ValueError("actual emitted bytes differ from reviewed serializer assertions")
        if not isinstance(parsed, dict) or not data:
            raise ValueError("actual emitted JSON unavailable")
        metadata, metadata_bytes = object_file(output / (name + ".metadata.json"), candidate)
        observed = validate_metadata(metadata, name)
        if assembly_map is not None and observed != assembly_map:
            raise ValueError("serializer cases loaded different assemblies")
        assembly_map = observed
        snapshots[output / (name + ".json")] = data
        snapshots[output / (name + ".metadata.json")] = metadata_bytes
        cases.append({"caseName": name, "path": name + ".json", "sha256": digest(data), "bytes": len(data),
                      "metadataPath": name + ".metadata.json", "metadataSha256": digest(metadata_bytes),
                      **{key: value for key, value in metadata.items() if key not in ("caseName", "assemblies")}})
    assemblies = []
    for name, project in ASSEMBLIES.items():
        paths = sorted({TEST_ASSEMBLY + "/bin/Release/net10.0/" + name + ".dll", project + "/bin/Release/net10.0/" + name + ".dll"})
        for path in paths:
            data = read_file(candidate / path, candidate, 64 * MAX_JSON)
            if digest(data) != assembly_map[name]:
                raise ValueError("loaded assembly hash differs from actual Release binaries")
            snapshots[candidate / path] = data
        assemblies.append({"name": name, "sha256": assembly_map[name], "binaryWitnessPaths": paths})
    trusted_sources = []
    trusted_snapshots = {}
    workflow = (".github/workflows/quotation-admission-race-qualification.yml" if lexical_policy.name == "quotation-admission-race-policy.json"
                else ".github/workflows/quotation-fixture-corrected-qualification.yml")
    for relative in ("scripts/check_quotation_candidate_wire.py", "scripts/materialize_quotation_candidate.py", "scripts/check_c821_focused_results.py",
                     "scripts/run_quotation_candidate_native.sh", workflow, "scripts/" + lexical_policy.name):
        path = root / relative
        data = read_file(path, root)
        if git(root, "show", head + ":" + relative) != data:
            raise ValueError("trusted verifier/helper/policy differs from protected transport")
        trusted_snapshots[path] = data
        trusted_sources.append({"path": relative, "sha256": digest(data)})
    checker_path = root / "scripts/check_quotation_candidate_wire.py"
    checker_bytes = trusted_snapshots[checker_path]
    transport.verify_source(candidate, policy)
    hosted_context(root, env)
    for path, before in snapshots.items():
        if read_file(path, candidate, 64 * MAX_JSON) != before:
            raise ValueError("source/runtime evidence changed during verification")
    if ({p.name for p in output.iterdir()} != OUTPUT_FILES
            or read_file(policy_path, root) != policy_bytes
            or read_file(materialization_path, root) != materialization_bytes
            or any(read_file(path, root) != data for path, data in trusted_snapshots.items())):
        raise ValueError("trusted inputs/outputs changed during verification")
    base_tree = oid(git(candidate, "rev-parse", policy["acceptedBase"] + "^{tree}").decode().strip())
    return output / RECEIPT_NAME, {
        "schemaVersion": 1, "producer": "Legacy.Maliev.QuotationService", "mode": "raw-candidate",
        "qualificationScope": policy["qualificationScope"], "transportCommit": head, "runId": run, "runAttempt": attempt,
        "acceptedBase": policy["acceptedBase"], "acceptedBaseTree": base_tree, "treeMeaning": "accepted base only; raw overrides are uncommitted",
        "policySha256": digest(policy_bytes), "manifestBlob": materialization["manifestBlob"], "manifestSha256": digest(manifest_bytes),
        "capsuleBlob": materialization["capsuleBlob"], "capsuleSha256": digest(capsule_bytes), "capsuleBytes": len(capsule_bytes),
        "materializationReceiptSha256": digest(materialization_bytes), "sourcePins": policy["sourcePins"], "sourceFiles": policy["sourceFiles"],
        "sources": sources, "assemblies": assemblies, "trustedSources": trusted_sources, "checker": {"path": "scripts/check_quotation_candidate_wire.py", "sha256": digest(checker_bytes)},
        "nativeResults": {"path": "qualification-wire.trx", "sha256": digest(trx_bytes), "cases": ["empty", "mixed"]},
        "cases": cases, "boundary": BOUNDARY,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--materialization", type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != "linux":
        raise ValueError("hosted Linux candidate evidence required")
    path, receipt = verify(args.candidate, args.policy, args.materialization)
    # Exclusive creation is anchored to the retained owned output directory.
    import json
    create_receipt(path, (json.dumps(receipt, indent=2) + "\n").encode("utf-8"))
    print("Actual raw-candidate serializer wire retained; two source-bound native cases passed.")


if __name__ == "__main__":
    main()
