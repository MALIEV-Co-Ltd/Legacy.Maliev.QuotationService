"""Require each original suite report and the exact solution audit graph."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import uuid
import xml.etree.ElementTree as ET

TEST_PROJECTS = (
    "Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj",
    "tools/HostedStorageFront.Tests/HostedStorageFront.Tests.csproj",
    "tools/FinancialCompletionEvidence.Tests/FinancialCompletionEvidence.Tests.csproj",
    "tools/HostedProcessStartObserver.Tests/HostedProcessStartObserver.Tests.csproj",
)
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
REQUIRED_COUNTERS = {"total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive",
                     "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning",
                     "completed", "inProgress", "pending"}


def guid(value):
    if not value or str(uuid.UUID(value)) != value.lower() or uuid.UUID(value).int == 0:
        raise ValueError("noncanonical suite execution identity")
    return value.lower()


def xml(data):
    if len(data) > 16 * 1024 * 1024 or re.search(br"<!\s*(?:DOCTYPE|ENTITY)\b", data, re.I):
        raise ValueError("unsafe or oversized XML evidence")
    return ET.fromstring(data)


def discovery_names(text):
    marker = "The following Tests are available:"
    if text.count(marker) != 1:
        raise ValueError("missing/ambiguous original discovery evidence")
    names = [line.strip() for line in text.split(marker, 1)[1].splitlines() if line.strip()]
    if not names or len(names) != len(set(names)):
        raise ValueError("empty/duplicate discovered cases")
    return set(names)


def validate_trx(data, discovered, assembly, delayed=None):
    delayed = delayed or {}
    root = xml(data)
    if root.tag != NS + "TestRun":
        raise ValueError("unexpected suite document root")
    summaries = root.findall(NS + "ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise ValueError("missing/failed/duplicate direct result summary")
    counters = summaries[0].findall(NS + "Counters")
    if len(counters) != 1 or len(root.findall(".//" + NS + "Counters")) != 1:
        raise ValueError("missing/duplicate suite counters")
    if set(counters[0].attrib) != REQUIRED_COUNTERS:
        raise ValueError("missing/unknown suite counter")
    counts = {k: int(v) for k, v in counters[0].attrib.items()}
    expanded = {method: count for method, count in delayed.items() if method in discovered}
    expected = len(discovered) + sum(count - 1 for count in expanded.values())
    if any(counts.get(k) != expected for k in ("total", "executed", "passed")):
        raise ValueError("suite discovery/result count mismatch")
    if any(v for k, v in counts.items() if k not in ("total", "executed", "passed")):
        raise ValueError("suite includes failed/skipped/unavailable results")
    definitions = root.findall("./" + NS + "TestDefinitions/" + NS + "UnitTest")
    results = root.findall("./" + NS + "Results/" + NS + "UnitTestResult")
    if len(definitions) != expected or len(results) != expected:
        raise ValueError("suite definitions/results incomplete")
    identities = {}
    definition_executions = {}
    for case in definitions:
        method = case.find(NS + "TestMethod")
        if method is None or Path(method.get("codeBase", "")).name != assembly + ".dll":
            raise ValueError("suite assembly identity mismatch")
        identity = guid(case.get("id"))
        if not identity or identity in identities:
            raise ValueError("duplicate/missing definition identity")
        identities[identity] = case.get("name")
        execution = case.find(NS + "Execution")
        if execution is None:
            raise ValueError("missing definition execution identity")
        definition_executions[identity] = guid(execution.get("id"))
    names = []
    seen = set()
    executions = {}
    for result in results:
        identity = guid(result.get("testId"))
        execution = guid(result.get("executionId"))
        name = result.get("testName")
        if identity not in identities or identity in seen or name != identities[identity] or result.get("outcome") != "Passed":
            raise ValueError("suite result identity/outcome mismatch")
        if definition_executions[identity] != execution:
            raise ValueError("definition/result execution mismatch")
        seen.add(identity)
        if execution in executions:
            raise ValueError("duplicate suite execution")
        executions[execution] = identity
        names.append(name)
    ordinary = []
    if len(set(names)) != expected:
        raise ValueError("duplicate suite case names, including delayed rows")
    for name in names:
        matching = [method for method in expanded if name.startswith(method + "(")]
        if not matching:
            ordinary.append(name)
    for method, count in expanded.items():
        if sum(name.startswith(method + "(") for name in names) != count:
            raise ValueError("source-bound delayed theory expansion incomplete")
    if len(set(ordinary)) != len(ordinary) or set(ordinary) != discovered - expanded.keys():
        raise ValueError("suite cases differ from original compiled discovery")
    for method, count in delayed.items():
        if sum(name.startswith(method + "(") for name in names) != count:
            raise ValueError("reviewed theory row count differs from source")
    entries = {}
    for entry in root.findall("./" + NS + "TestEntries/" + NS + "TestEntry"):
        execution = guid(entry.get("executionId"))
        identity = guid(entry.get("testId"))
        guid(entry.get("testListId"))
        if execution in entries:
            raise ValueError("duplicate suite entry")
        entries[execution] = identity
    if entries != executions:
        raise ValueError("suite entries/results identity mismatch")


def validate_resolved_graph(report, expected_projects):
    if (not isinstance(report, dict) or type(report.get("version")) is not int or report["version"] != 1
            or not isinstance(report.get("parameters"), str)
            or report["parameters"].split() != ["--include-transitive"]
            or not isinstance(report.get("projects"), list)):
        raise ValueError("missing original full transitive package graph")
    actual = []
    for project in report["projects"]:
        if not isinstance(project, dict) or not isinstance(project.get("path"), str) or not project["path"]:
            raise ValueError("resolved graph project identity missing")
        frameworks = project.get("frameworks")
        if (not isinstance(frameworks, list) or len(frameworks) != 1
                or not isinstance(frameworks[0], dict) or frameworks[0].get("framework") != "net10.0"):
            raise ValueError("resolved graph framework identity differs")
        for key in ("topLevelPackages", "transitivePackages"):
            if key not in frameworks[0]:
                continue
            rows = frameworks[0][key]
            if (not isinstance(rows, list) or any(not isinstance(row, dict)
                    or any(not isinstance(row.get(field), str) or not row[field]
                           for field in ("id", "resolvedVersion")) for row in rows)):
                raise ValueError("malformed resolved package rows")
            if len({row["id"].lower() for row in rows}) != len(rows):
                raise ValueError("duplicate resolved package identity")
        actual.append(Path(project["path"]).resolve())
    if len(actual) != len(set(actual)) or set(actual) != set(expected_projects):
        raise ValueError("resolved graph project set differs")
    def check(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key in ("vulnerabilities", "errors", "problems", "warnings") and (not isinstance(value, list) or value):
                    raise ValueError("resolved graph contains unavailable or unsafe evidence")
                check(value)
        elif isinstance(node, list):
            for child in node:
                check(child)
    check(report)
    return set(actual)


def validate_audit(report, expected_projects, allowed_sources=None, resolved_graph=None):
    allowed_sources = allowed_sources or {"https://api.nuget.org/v3/index.json"}
    if type(report.get("version")) is not int or report["version"] != 1 or not isinstance(report.get("projects"), list):
        raise ValueError("missing resolved audit graph")
    parameters = report.get("parameters")
    if not isinstance(parameters, str) or not {"--vulnerable", "--include-transitive"} <= set(shlex.split(parameters)):
        raise ValueError("audit must include vulnerable transitive package scope")
    sources = report.get("sources")
    if not isinstance(sources, list) or not sources or any(not isinstance(source, str) or not source for source in sources):
        raise ValueError("missing/malformed actual audit sources")
    sources = [source.rstrip("/") for source in sources]
    if len(sources) != len(set(sources)) or not set(sources) <= allowed_sources or "https://api.nuget.org/v3/index.json" not in sources:
        raise ValueError("unexpected or unavailable vulnerability feed")
    def check(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key in ("topLevelPackages", "transitivePackages", "vulnerabilities", "errors", "problems", "warnings"):
                    if not isinstance(value, list) or value:
                        raise ValueError("audit has affected rows, malformed collections or unavailable evidence")
                check(value)
        elif isinstance(node, list):
            for child in node:
                check(child)
    check(report)
    resolved = validate_resolved_graph(resolved_graph, expected_projects) if resolved_graph is not None else set()
    actual = []
    for project in report["projects"]:
        if not isinstance(project, dict):
            raise ValueError("malformed audit project")
        path = project.get("path")
        frameworks = project.get("frameworks")
        if not isinstance(path, str) or not path:
            raise ValueError("audit project identity missing")
        if "frameworks" not in project:
            if Path(path).resolve() not in resolved:
                raise ValueError("sparse clean audit requires original full graph")
            actual.append(Path(path).resolve())
            continue
        if not isinstance(frameworks, list) or len(frameworks) != 1:
            raise ValueError("audit framework malformed")
        if not isinstance(frameworks[0], dict) or frameworks[0].get("framework") != "net10.0":
            raise ValueError("audit framework identity mismatch")
        actual.append(Path(path).resolve())
    if len(actual) != len(set(actual)) or set(actual) != set(expected_projects):
        raise ValueError("audit project graph differs from exact solution")


def validate_restore_sources(assets, expected):
    sources = assets.get("project", {}).get("restore", {}).get("sources")
    if not isinstance(sources, dict) or {source.rstrip("/") for source in sources} != expected:
        raise ValueError("resolved restore feed set differs from reviewed source graph")


def validate_empty_stderr(path):
    if not path.is_file() or path.stat().st_size:
        raise ValueError("missing or nonempty audit stderr evidence")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--phase", choices=("suite", "audit", "catalogue-audit"), required=True)
    args = parser.parse_args()
    root = args.candidate.resolve(strict=True)
    if args.phase == "suite":
        inventory = json.loads((Path(__file__).parent / "quotation-candidate-delayed-theories.json").read_text())
        delayed = {}
        for case in inventory:
            source = (root / case["source"]).read_bytes().replace(b"\r\n", b"\n")
            if hashlib.sha256(source).hexdigest() != case["normalizedSourceSha256"]:
                raise ValueError("reviewed delayed theory source changed")
            delayed.setdefault(case["assembly"], {})[case["method"]] = case["rows"]
        directory = root / "TestResults/CandidateNative/Suite"
        expected = {Path(p).stem for p in TEST_PROJECTS}
        if {p.stem for p in directory.glob("*.trx")} != expected:
            raise ValueError("missing/extra original test project reports")
        for project in TEST_PROJECTS:
            name = Path(project).stem
            discovery = discovery_names((directory / (name + ".discovery.log")).read_text())
            validate_trx((directory / (name + ".trx")).read_bytes(), discovery, name, delayed.get(name))
        print("All four actual test projects match original compiled discovery and passed completely.")
    elif args.phase == "audit":
        solution = xml((root / "Legacy.Maliev.QuotationService.slnx").read_bytes())
        expected = [(root / p.attrib["Path"]).resolve() for p in solution.findall("Project")]
        stderr = root / "TestResults/CandidateNative/package-audit.stderr.log"
        validate_empty_stderr(stderr)
        for project in expected:
            assets = json.loads((project.parent / "obj/project.assets.json").read_text())
            validate_restore_sources(assets, {"https://api.nuget.org/v3/index.json"})
        validate_empty_stderr(root / "TestResults/CandidateNative/package-graph.stderr.log")
        graph = json.loads((root / "TestResults/CandidateNative/package-graph.json").read_text())
        validate_audit(json.loads((root / "TestResults/CandidateNative/package-audit.json").read_text()), expected, resolved_graph=graph)
        print("Exact solution project/net10.0 vulnerability graph has no findings or unavailable evidence.")
    else:
        project = root / "tooling/Quotation.PermissionRegistration.Tests/Quotation.PermissionRegistration.Tests.csproj"
        local_feed = str(root / ".catalogue-build/packages")
        assets = json.loads((project.parent / "obj/project.assets.json").read_text())
        allowed = {"https://api.nuget.org/v3/index.json", local_feed}
        validate_restore_sources(assets, allowed)
        directory = root / "TestResults/QuotationCatalogue"
        stderr = directory / "trusted-vulnerability-audit.stderr.log"
        validate_empty_stderr(stderr)
        validate_empty_stderr(directory / "trusted-package-graph.stderr.log")
        graph = json.loads((directory / "trusted-package-graph.json").read_text())
        validate_audit(json.loads((directory / "trusted-vulnerability-audit.json").read_text()), [project.resolve()], allowed, graph)
        print("Exact catalogue project/net10.0 audit and genuine restore feeds have no affected rows or unavailable evidence.")


if __name__ == "__main__":
    main()
