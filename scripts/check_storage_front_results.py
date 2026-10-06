"""Source-bound native front controls; deliberately not genuine eight-host evidence."""
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from check_c821_focused_results import COUNTERS, canonical_guid, case_hash

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
MAXIMUM = 4 * 1024 * 1024


def display_digest(name, class_name, method, parameters):
    prefix = class_name + "." + method
    if not parameters and name in (prefix, prefix + "()"):
        return case_hash([])
    if not name.startswith(prefix + "(") or not name.endswith(")") or len(name) > 1024:
        raise ValueError("Wrong front case identity")
    tokens = re.findall(r'(?:"(?:\\.|[^"\\])*"|[^,])+', name[len(prefix) + 1:-1])
    if len(tokens) != len(parameters):
        raise ValueError("Wrong front argument inventory")
    arguments = []
    for token, parameter in zip(tokens, parameters):
        key, separator, raw = token.strip().partition(":")
        if not separator or key != parameter["name"]:
            raise ValueError("Wrong front named argument")
        raw = raw.strip()
        value = json.loads(raw)
        kind = parameter["type"]
        if not ((kind == "string" and type(value) is str)
                or (kind == "bool" and type(value) is bool)
                or (kind in ("int", "long") and type(value) is int)):
            raise ValueError("Wrong front scalar argument shape")
        arguments.append(value)
    return case_hash(arguments)


def validate_bytes(data, inventory):
    if len(data) > MAXIMUM:
        raise ValueError("Oversized front native report")
    text = data.decode("utf-8-sig")
    if "\x00" in text or re.search(r"<!\s*(?:DOCTYPE|ENTITY)\b", text, re.I):
        raise ValueError("DTD/entity/unsupported encoding denied")
    root = ET.fromstring(text)
    if root.tag != NS + "TestRun":
        raise ValueError("Wrong front TRX namespace")
    required = Counter((class_name, method, digest)
                       for class_name, group in inventory.items()
                       for method, item in group["methods"].items() for digest in item["cases"])
    if not required or any(count != 1 for count in required.values()):
        raise ValueError("Ambiguous reviewed front inventory")
    expected = sum(required.values())
    summaries = root.findall(NS + "ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise ValueError("Front native run incomplete")
    counters = summaries[0].findall(NS + "Counters")
    if len(counters) != 1 or set(counters[0].attrib) != set(COUNTERS):
        raise ValueError("Incomplete native counter schema")
    if any(not re.fullmatch(r"[0-9]{1,9}", value) for value in counters[0].attrib.values()):
        raise ValueError("Malformed native counters")
    values = {key: int(counters[0].get(key)) for key in COUNTERS}
    if any(values[key] != expected for key in COUNTERS[:3]) or any(values[key] for key in COUNTERS[3:]):
        raise ValueError("Nonpassing or missing front executions")
    definitions = {}
    for definition in root.findall(NS + "TestDefinitions/" + NS + "UnitTest"):
        identity = canonical_guid(definition.get("id"))
        methods = definition.findall(NS + "TestMethod")
        executions = definition.findall(NS + "Execution")
        if identity in definitions or len(methods) != 1 or len(executions) != 1:
            raise ValueError("Ambiguous front definition")
        class_name, method = methods[0].get("className"), methods[0].get("name")
        if class_name not in inventory or method not in inventory[class_name]["methods"]:
            raise ValueError("Wrong front class/method")
        definitions[identity] = (class_name, method, canonical_guid(executions[0].get("id")))
    observed, executions = Counter(), {}
    for result in root.findall(NS + "Results/" + NS + "UnitTestResult"):
        execution, identity = canonical_guid(result.get("executionId")), canonical_guid(result.get("testId"))
        if execution in executions or identity not in definitions or result.get("outcome") != "Passed":
            raise ValueError("Repeated or inconsistent front result")
        class_name, method, _ = definitions[identity]
        specification = inventory[class_name]["methods"][method]
        digest = display_digest(result.get("testName", ""), class_name, method, specification["parameters"])
        observed[(class_name, method, digest)] += 1
        executions[execution] = identity
    if observed != required or set(executions.values()) != set(definitions):
        raise ValueError("Front source case inventory differs")
    if any(executions.get(execution) != identity for identity, (_, _, execution) in definitions.items()):
        raise ValueError("Definition execution is not an actual front case")
    entries = {}
    for entry in root.findall(NS + "TestEntries/" + NS + "TestEntry"):
        execution, identity = canonical_guid(entry.get("executionId")), canonical_guid(entry.get("testId"))
        canonical_guid(entry.get("testListId"))
        if execution in entries:
            raise ValueError("Duplicate front test entry")
        entries[execution] = identity
    if entries != executions:
        raise ValueError("Front entries/results differ")
    return expected


def main():
    repository = Path(__file__).resolve().parents[1]
    inventory = json.loads((repository / "scripts/storage-front-inventory.json").read_text())
    for group in inventory.values():
        source = repository / group["source"]
        if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != group["normalizedSourceSha256"]:
            raise ValueError("Reviewed front native source changed")
    path = repository / "TestResults/StorageFront/storage-front.trx"
    for entry in (path, *path.parents):
        if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
            raise ValueError("Redirected front evidence denied")
    with path.open("rb") as stream:
        count = validate_bytes(stream.read(MAXIMUM + 1), inventory)
    print(f"storage-front.trx: {count} exact native front cases passed; genuine eight-host proof not established")


if __name__ == "__main__":
    main()
