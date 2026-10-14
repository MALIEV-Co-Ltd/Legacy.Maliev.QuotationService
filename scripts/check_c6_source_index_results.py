"""Require four source-bound C6 Facts in a separately retained native TRX."""
import hashlib
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from check_c821_focused_results import COUNTERS, MAX_BYTES, canonical_guid

ANALYTICS = "Legacy.Maliev.QuotationService.Tests.Analytics.QuotationAnalyticsSourceContractTests"
PHYSICAL = "Legacy.Maliev.QuotationService.Tests.Data.QuotationPostgresMigrationTests"
EXPECTED = {
    (ANALYTICS, "Payload_WithoutSource_OmitsSourceReconciliationFields"),
    (ANALYTICS, "Outbox_Model_PreservesNullableSourceColumnsAndPartialIndexes"),
    (ANALYTICS, "SourceIndexMigration_OnlyReplacesTheTwoIndexesAndRestoresNames"),
    (PHYSICAL, "AnalyticsSourceIndexMigration_GeneratesScriptsAndPreservesRowsAcrossUpgradeRollbackAndReupgrade"),
}
SOURCES = {
    "Legacy.Maliev.QuotationService.Tests/Analytics/QuotationAnalyticsSourceContractTests.cs": "811898fecb8054f628fc5fb8173049b548a3e02b3b3c228d55dd6940b7c2283d",
    "Legacy.Maliev.QuotationService.Tests/Data/QuotationPostgresMigrationTests.cs": "88a5480a01d8b3a90861577f89f3e9759e60fa4f6c21e743db42a3afa5629782",
}
TAG = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def validate_bytes(data):
    if not isinstance(data, bytes) or len(data) > MAX_BYTES:
        raise ValueError("Bounded native TRX bytes required")
    text = data.decode("utf-8-sig")
    if "\x00" in text or re.search(r"<!\s*(?:DOCTYPE|ENTITY)\b", text, re.I):
        raise ValueError("Unsupported XML declaration")
    report = ET.fromstring(text)
    if report.tag != TAG + "TestRun":
        raise ValueError("Exact TRX namespace required")
    summaries = report.findall(TAG + "ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise ValueError("Native run must complete")
    counters = summaries[0].findall(TAG + "Counters")
    if len(counters) != 1 or set(counters[0].attrib) != set(COUNTERS):
        raise ValueError("Exact counter schema required")
    if any(not re.fullmatch(r"[0-9]{1,9}", value) for value in counters[0].attrib.values()):
        raise ValueError("Invalid counters")
    counts = {key: int(counters[0].get(key)) for key in COUNTERS}
    if any(counts[key] != 4 for key in COUNTERS[:3]) or any(counts[key] for key in COUNTERS[3:]):
        raise ValueError("Exactly four passing, executed Facts required")
    definitions = {}
    for definition in report.findall(TAG + "TestDefinitions/" + TAG + "UnitTest"):
        identity = canonical_guid(definition.get("id"))
        methods = definition.findall(TAG + "TestMethod")
        executions = definition.findall(TAG + "Execution")
        if identity in definitions or len(methods) != 1 or len(executions) != 1:
            raise ValueError("Unique source definition required")
        method = (methods[0].get("className"), methods[0].get("name"))
        if method not in EXPECTED:
            raise ValueError("Wrong C6 source method")
        definitions[identity] = (method, canonical_guid(executions[0].get("id")))
    if len(definitions) != 4:
        raise ValueError("Four individual Fact definitions required")
    observed = set()
    pairs = set()
    for result in report.findall(TAG + "Results/" + TAG + "UnitTestResult"):
        identity = canonical_guid(result.get("testId"))
        execution = canonical_guid(result.get("executionId"))
        if identity not in definitions or result.get("outcome") != "Passed":
            raise ValueError("Native result/definition join failed")
        method, defined_execution = definitions[identity]
        if method in observed or execution != defined_execution or result.get("testName") not in (".".join(method), ".".join(method) + "()"):
            raise ValueError("Repeated or unidentified C6 Fact")
        observed.add(method)
        pairs.add((identity, execution))
    if observed != EXPECTED or len(pairs) != 4 or len({execution for identity, execution in pairs}) != 4:
        raise ValueError("Incomplete native C6 inventory")
    entries = []
    for entry in report.findall(TAG + "TestEntries/" + TAG + "TestEntry"):
        canonical_guid(entry.get("testListId"))
        entries.append((canonical_guid(entry.get("testId")), canonical_guid(entry.get("executionId"))))
    if len(entries) != 4 or set(entries) != pairs:
        raise ValueError("Native entry/result join failed")
    return pairs


def main():
    repository = Path(__file__).resolve().parents[1]
    for path, expected in SOURCES.items():
        if hashlib.sha256((repository / path).read_bytes().replace(b"\r\n", b"\n")).hexdigest() != expected:
            raise ValueError("Reviewed C6 Fact source changed")
    root = repository / "TestResults"
    path = root / "C6SourceIndex/c6-source-index.trx"
    for entry in (path, *path.absolute().parents):
        if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
            raise ValueError("Linked native evidence denied")
    resolved_root = root.resolve(strict=True)
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(resolved_root) or not resolved.is_file():
        raise ValueError("Native evidence escaped owned root")
    with resolved.open("rb") as stream:
        data = stream.read(MAX_BYTES + 1)
    validate_bytes(data)
    print("c6-source-index.trx: four source-bound native Facts passed with individual definition/result/entry identities")


if __name__ == "__main__":
    main()
