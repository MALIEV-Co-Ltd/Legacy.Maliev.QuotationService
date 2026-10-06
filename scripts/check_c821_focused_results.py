"""Bounded, identity- and inventory-bound hosted c821 evidence gate; prints no arguments."""
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import uuid
import xml.etree.ElementTree as ET

COUNTERS = ("total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive",
            "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "completed", "inProgress", "pending")
MAX_BYTES = 4 * 1024 * 1024
ENUMS = {"OrderDecisionResult": {"Completed": 0, "Conflict": 1, "NotFound": 2, "Unavailable": 3},
         "HttpStatusCode": {"OK": 200, "Forbidden": 403, "Conflict": 409, "ServiceUnavailable": 503}}


def case_hash(arguments):
    return hashlib.sha256(json.dumps(arguments, separators=(",", ":"), ensure_ascii=True).encode()).hexdigest()


def canonical_guid(value):
    if not isinstance(value, str) or str(uuid.UUID(value)) != value or uuid.UUID(value).int == 0:
        raise ValueError("Missing/noncanonical evidence identity")
    return value


def normalize(token, kind):
    token = token.strip()
    if kind in ("bool", "bool?"):
        if token.lower() == "true": return True
        if token.lower() == "false": return False
        if kind == "bool?" and token.lower() == "null": return None
    elif kind == "string":
        value = json.loads(token)
        if isinstance(value, str): return value
    elif kind in ENUMS:
        name = token.rsplit(".", 1)[-1]
        if name in ENUMS[kind]: return name
        if re.fullmatch(r"[0-9]+", token):
            names = [name for name, number in ENUMS[kind].items() if number == int(token)]
            if len(names) == 1: return names[0]
    raise ValueError("Invalid case argument shape")


def display_case(test_name, class_name, method, parameters):
    prefix = class_name + "." + method
    if test_name == prefix or test_name == prefix + "()":
        if parameters: raise ValueError("Missing theory case identity")
        return case_hash([])
    if not test_name.startswith(prefix + "(") or not test_name.endswith(")"):
        raise ValueError("Wrong class/method identity")
    raw = test_name[len(prefix) + 1:-1]
    # Current reviewed fixtures contain only bounded scalar named arguments, never JWTs or object graphs.
    tokens = re.findall(r'(?:"(?:\\.|[^"\\])*"|[^,])+', raw)
    if len(tokens) != len(parameters): raise ValueError("Wrong argument inventory")
    arguments = []
    for token, parameter in zip(tokens, parameters):
        name, separator, value = token.strip().partition(":")
        if not separator or name != parameter["name"]: raise ValueError("Wrong named argument identity")
        arguments.append(normalize(value, parameter["type"]))
    return case_hash(arguments)


def load_inventory(repo):
    inventory = json.loads((repo / "scripts/c821-focused-inventory.json").read_text())
    for specification in inventory.values():
        source = repo / specification["source"]
        normalized = source.read_bytes().replace(b"\r\n", b"\n")
        if hashlib.sha256(normalized).hexdigest() != specification["normalizedSourceSha256"]:
            raise ValueError("Reviewed focal source inventory changed")
    return inventory


def validate(path, specification, evidence_root):
    # Inspect the caller's lexical root before resolution, including when path is already resolved.
    for entry in (evidence_root.absolute(), *evidence_root.absolute().parents):
        if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
            raise ValueError("Linked owned evidence root/ancestor denied")
    root = evidence_root.resolve(strict=True)
    if path.is_symlink(): raise ValueError("Symlink evidence denied")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(root) or not resolved.is_file(): raise ValueError("Evidence path escaped owned root")
    for parent in path.absolute().parents:
        if parent == root: break
        if parent.is_symlink(): raise ValueError("Symlink evidence parent denied")
    with resolved.open("rb") as stream:
        data = stream.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES: raise ValueError("Oversized focal XML")
    text = data.decode("utf-8-sig")
    if "\x00" in text or re.search(r"<!\s*(?:DOCTYPE|ENTITY)\b", text, re.I):
        raise ValueError("DTD/entity/unsupported XML encoding denied")
    report = ET.fromstring(text)
    namespace = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    if report.tag != namespace + "TestRun": raise ValueError("Wrong TRX root/namespace")
    summaries = report.findall(namespace + "ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed": raise ValueError("Run did not complete")
    counters = summaries[0].findall(namespace + "Counters")
    if len(counters) != 1 or set(counters[0].attrib) != set(COUNTERS): raise ValueError("Incomplete counter schema")
    if any(not re.fullmatch(r"[0-9]{1,9}", value) for value in counters[0].attrib.values()): raise ValueError("Malformed counters")
    values = {key: int(counters[0].get(key)) for key in COUNTERS}
    expected = sum(len(method["cases"]) for method in specification["methods"].values())
    if any(values[key] != expected for key in ("total", "executed", "passed")) or any(values[key] for key in COUNTERS[3:]):
        raise ValueError("Nonpassing or incomplete focal execution")
    definitions = {}
    for definition in report.findall(namespace + "TestDefinitions/" + namespace + "UnitTest"):
        identity = canonical_guid(definition.get("id"))
        methods = definition.findall(namespace + "TestMethod")
        executions = definition.findall(namespace + "Execution")
        if identity in definitions or len(methods) != 1 or len(executions) != 1: raise ValueError("Ambiguous test definition")
        method = methods[0].get("name")
        if methods[0].get("className") != specification["className"] or method not in specification["methods"]:
            raise ValueError("Wrong focal definition group")
        definitions[identity] = (method, canonical_guid(executions[0].get("id")))
    results = report.findall(namespace + "Results/" + namespace + "UnitTestResult")
    if len(results) != expected: raise ValueError("Incomplete per-case results")
    executions = {}
    observed = Counter()
    used_definitions = set()
    for result in results:
        execution = canonical_guid(result.get("executionId"))
        identity = canonical_guid(result.get("testId"))
        if execution in executions or identity not in definitions or result.get("outcome") != "Passed":
            raise ValueError("Repeated execution or inconsistent definition/result")
        method = definitions[identity][0]
        name = result.get("testName", "")
        if len(name) > 1024: raise ValueError("Oversized case name")
        digest = display_case(name, specification["className"], method, specification["methods"][method]["parameters"])
        if digest not in specification["methods"][method]["cases"] or observed[(method, digest)]:
            raise ValueError("Wrong or repeated source case")
        observed[(method, digest)] += 1
        executions[execution] = identity
        used_definitions.add(identity)
    required = Counter((method, digest) for method, item in specification["methods"].items() for digest in item["cases"])
    if observed != required or used_definitions != set(definitions): raise ValueError("Source case inventory incomplete")
    # Shared theory definitions are valid; their definition execution must reference one actual case for that testId.
    if any(executions.get(execution) != identity for identity, (_, execution) in definitions.items()):
        raise ValueError("Definition execution is not an observed case")
    entries = report.findall(namespace + "TestEntries/" + namespace + "TestEntry")
    entry_map = {}
    for entry in entries:
        execution = canonical_guid(entry.get("executionId"))
        identity = canonical_guid(entry.get("testId"))
        canonical_guid(entry.get("testListId"))
        if execution in entry_map: raise ValueError("Repeated test entry")
        entry_map[execution] = identity
    if entry_map != executions: raise ValueError("Test entries disagree with executions")
    print(f"{resolved.name}: {expected} exact source cases executed/passed; zero failed/skipped")


if __name__ == "__main__":
    repository = Path(__file__).resolve().parents[1]
    inventory = load_inventory(repository)
    validate(repository / "TestResults/C821Verifier/c821-verifier.trx", inventory["verifier"], repository / "TestResults")
    validate(repository / "TestResults/C821Recipient/c821-recipient.trx", inventory["recipient"], repository / "TestResults")
