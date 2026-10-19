"""Require the separately executed four ACK race cases and three settlement Facts."""
from collections import Counter
import hashlib
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from check_c821_focused_results import COUNTERS, MAX_BYTES, canonical_guid, case_hash

CLASS = "Legacy.Maliev.QuotationService.Tests.Analytics.QuotationTerminalLoggingPipelineTests"
RACE = "Program_ReclaimedLease_OnlyCurrentOwnerEmitsDurableTerminalFailure"
FACTS = (
    "DeliverySettlement_PrimaryFailureSurvivesIndependentReleaseCancelAndDeliveryFailures",
    "DeliverySettlement_TimeoutKeepsResourcesOwnedUntilActualLateTaskSettlement",
    "DeliverySettlement_ObserverReturnCannotHideActualFaultWhenBodySucceeded",
)
RACE_SPEC = {"className": CLASS, "methods": {RACE: {"parameters": [{"name": "staleStatus", "type": "int"}, {"name": "currentStatus", "type": "int"}], "cases": [case_hash(list(pair)) for pair in ((400, 204), (500, 204), (400, 400), (500, 500))]}}}
SETTLEMENT_SPEC = {"className": CLASS, "methods": {name: {"parameters": [], "cases": [case_hash([])]} for name in FACTS}}

def display_case(name, class_name, method, parameters):
    prefix = class_name + "." + method
    if not parameters:
        if name not in (prefix, prefix + "()"):
            raise ValueError("Wrong settlement Fact identity")
        return case_hash([])
    match = re.fullmatch(re.escape(prefix) + r"\(staleStatus: (400|500), currentStatus: (204|400|500)\)", name)
    if not match:
        raise ValueError("Exact named integer ACK arguments required")
    return case_hash([int(match[1]), int(match[2])])

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
    validate_bytes(data, specification, resolved.name)


def validate_bytes(data, specification, report_name="captured.trx"):
    """Validate exactly the captured TRX bytes; callers own path and descriptor checks."""
    if not isinstance(data, bytes) or len(data) > MAX_BYTES: raise ValueError("Oversized/nonbyte focal XML")
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
    print(f"{report_name}: {expected} exact source cases executed/passed; zero failed/skipped")

def main():
    repository = Path(__file__).resolve().parents[1]
    source = repository / "Legacy.Maliev.QuotationService.Tests/Analytics/QuotationTerminalLoggingPipelineTests.cs"
    if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != "4f5a748af28d42753c53c5628f5f919449a09ec35452b2a0dbaf5039f5647fee":
        raise ValueError("Reviewed ACK/settlement source changed")
    root = repository / "TestResults"
    validate(root / "AnalyticsLeaseReclaim/analytics-lease-reclaim.trx", RACE_SPEC, root)
    validate(root / "AnalyticsDeliverySettlement/analytics-delivery-settlement.trx", SETTLEMENT_SPEC, root)

if __name__ == "__main__":
    main()
