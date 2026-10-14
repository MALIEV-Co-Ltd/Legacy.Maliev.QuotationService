"""Verify independently executed ordinary controller parity rows without count-only acceptance."""
import argparse
import json
from pathlib import Path
import uuid
import xml.etree.ElementTree as ET

MANIFEST = Path(__file__).with_name("quotation_pagination_parity_focus.json")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def require_canonical_guid(value):
    if not isinstance(value, str) or str(uuid.UUID(value)) != value:
        raise ValueError("Expected canonical lowercase hyphenated GUID")


def validate(raw, manifest):
    expected = manifest["names"]
    if manifest["expectedCount"] != 12 or len(expected) != 12 or len(set(expected)) != 12:
        raise ValueError("Invalid frozen 12-row manifest")
    bindings = manifest["testMethods"]
    if set(bindings) != set(expected):
        raise ValueError("Frozen method bindings differ from focused identities")
    for name in expected:
        binding = bindings[name]
        if name.split("(", 1)[0] != binding["className"] + "." + binding["name"] or binding["assembly"] != "Legacy.Maliev.QuotationService.Tests.dll":
            raise ValueError("Invalid frozen class or method binding")
    root = ET.fromstring(raw)
    for container in ("Results", "TestDefinitions", "TestEntries", "ResultSummary"):
        if len(root.findall("t:" + container, NS)) != 1:
            raise ValueError("Missing or ambiguous focused container")
    rows = root.findall("./t:Results/t:UnitTestResult", NS)
    definitions = root.findall("./t:TestDefinitions/t:UnitTest", NS)
    entries = root.findall("./t:TestEntries/t:TestEntry", NS)
    if len(rows) != 12 or len(definitions) != 12 or len(entries) != 12:
        raise ValueError("Expected 12 results, definitions and entries")
    if sorted(row.get("testName") for row in rows) != sorted(expected):
        raise ValueError("Focused identities differ from frozen manifest")
    ids = [row.get("testId") for row in rows]
    executions = [row.get("executionId") for row in rows]
    if len(set(ids)) != 12 or len(set(executions)) != 12:
        raise ValueError("Duplicate focused identities")
    by_id = {row.get("id"): row for row in definitions}
    pairs = {(row.get("testId"), row.get("executionId")) for row in entries}
    if len(by_id) != 12 or len(pairs) != 12:
        raise ValueError("Duplicate definitions or entries")
    for row in rows:
        require_canonical_guid(row.get("testId"))
        require_canonical_guid(row.get("executionId"))
        definition = by_id.get(row.get("testId"))
        if definition is None or definition.get("name") != row.get("testName"):
            raise ValueError("Definition identity mismatch")
        methods = definition.findall("t:TestMethod", NS)
        if len(methods) != 1:
            raise ValueError("Missing or ambiguous definition method")
        method = methods[0]
        binding = bindings[row.get("testName")]
        if method.get("className") != binding["className"] or method.get("name") != binding["name"] or Path(method.get("codeBase", "").replace("\\", "/")).name != binding["assembly"]:
            raise ValueError("Definition class, method or assembly mismatch")
        definition_executions = definition.findall("t:Execution", NS)
        if len(definition_executions) != 1:
            raise ValueError("Missing or ambiguous definition execution")
        execution = definition_executions[0]
        if execution.get("id") != row.get("executionId") or (row.get("testId"), row.get("executionId")) not in pairs:
            raise ValueError("Definition execution mismatch")
        if row.get("outcome") != "Passed" or row.find(".//t:ErrorInfo", NS) is not None:
            raise ValueError("Focused row did not pass")
    summary = root.find("t:ResultSummary", NS)
    summary_counters = summary.findall("t:Counters", NS)
    if summary.get("outcome") != "Completed" or len(summary_counters) != 1:
        raise ValueError("Incomplete focused summary")
    if summary.find(".//t:ErrorInfo", NS) is not None:
        raise ValueError("Focused summary contains error information")
    for run_info in summary.findall(".//t:RunInfo", NS):
        if run_info.get("outcome") != "Passed":
            raise ValueError("Focused run information is not passing")
    counters = summary_counters[0]
    passing = ("total", "executed", "passed")
    nonpassing = ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted", "disconnected", "warning", "notRunnable", "passedButRunAborted", "completed", "inProgress", "pending")
    if set(counters.attrib) != set(passing + nonpassing):
        raise ValueError("Focused summary requires exactly sixteen counters")
    for key in passing:
        if counters.get(key) != "12":
            raise ValueError("Focused counters mismatch")
    for key in nonpassing:
        if counters.get(key) != "0":
            raise ValueError("Missing or nonzero focused failure counter")
    for key, value in counters.attrib.items():
        if key not in ("total", "executed", "passed") and int(value) != 0:
            raise ValueError("Focused summary includes nonpassing results")
    return 12


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("trx", type=Path)
    args = parser.parse_args()
    count = validate(args.trx.read_bytes(), json.loads(MANIFEST.read_text(encoding="utf-8")))
    print(f"Quotation pagination parity: {count}/12 exact focused rows passed")


if __name__ == "__main__":
    main()
