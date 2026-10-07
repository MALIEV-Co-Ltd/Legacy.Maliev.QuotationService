import copy
import importlib.util
from pathlib import Path
import unittest
import uuid
import xml.etree.ElementTree as ET
import tempfile

spec = importlib.util.spec_from_file_location("native_gate", Path(__file__).parents[1] / "check_quotation_candidate_native.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class NativeEvidenceTests(unittest.TestCase):
    def test_actual_sparse_clean_audit_requires_independent_full_graph(self):
        path = Path("boundary.csproj").resolve()
        sparse = {"version": 1, "parameters": "--vulnerable --include-transitive",
                  "sources": ["https://api.nuget.org/v3/index.json"], "projects": [{"path": str(path)}]}
        graph = {"version": 1, "parameters": "--include-transitive", "projects": [{"path": str(path),
                 "frameworks": [{"framework": "net10.0", "topLevelPackages": [{"id": "Package", "resolvedVersion": "1.2.3"}]}]}]}
        EXPECTED = [path]
        with self.assertRaises(ValueError):
            gate.validate_audit(sparse, EXPECTED)
        gate.validate_audit(sparse, EXPECTED, resolved_graph=graph)
        for change in ("missing", "other", "duplicate", "framework", "null-framework", "warning", "boolean-version", "vulnerable-scope", "outdated-scope", "deprecated-scope", "malformed-package", "duplicate-package"):
            bad = copy.deepcopy(graph)
            if change == "missing": bad["projects"] = []
            elif change == "other": bad["projects"][0]["path"] = "other.csproj"
            elif change == "duplicate": bad["projects"] *= 2
            elif change == "framework": bad["projects"][0]["frameworks"][0]["framework"] = "net9.0"
            elif change == "null-framework": bad["projects"][0]["frameworks"] = None
            elif change == "warning": bad["warnings"] = ["NU1900"]
            elif change == "boolean-version": bad["version"] = True
            elif change == "vulnerable-scope": bad["parameters"] += " --vulnerable"
            elif change == "outdated-scope": bad["parameters"] += " --outdated"
            elif change == "deprecated-scope": bad["parameters"] += " --deprecated"
            elif change == "malformed-package": bad["projects"][0]["frameworks"][0]["topLevelPackages"] = [{}]
            elif change == "duplicate-package": bad["projects"][0]["frameworks"][0]["topLevelPackages"] *= 2
            with self.subTest(graphChange=change), self.assertRaises(ValueError):
                gate.validate_audit(sparse, EXPECTED, resolved_graph=bad)
        for key in ("warnings", "errors", "problems", "vulnerabilities", "topLevelPackages", "transitivePackages"):
            for value in (["unavailable"], {}, None, False, ""):
                bad = copy.deepcopy(sparse); bad[key] = value
                with self.subTest(auditKey=key, value=value), self.assertRaises(ValueError):
                    gate.validate_audit(bad, EXPECTED, resolved_graph=graph)
        for frameworks in ([], None, [{"framework": "net9.0"}]):
            bad = copy.deepcopy(sparse); bad["projects"][0]["frameworks"] = frameworks
            with self.subTest(auditFrameworks=frameworks), self.assertRaises(ValueError):
                gate.validate_audit(bad, EXPECTED, resolved_graph=graph)

    def test_actual_restore_feeds_must_match_reviewed_graph(self):
        feed = "https://api.nuget.org/v3/index.json"
        assets = {"project": {"restore": {"sources": {feed: {}}}}}
        gate.validate_restore_sources(assets, {feed})
        for value in ({}, None, [], {"https://other.invalid/index.json": {}}):
            bad = copy.deepcopy(assets)
            bad["project"]["restore"]["sources"] = value
            with self.subTest(sources=value), self.assertRaises(ValueError):
                gate.validate_restore_sources(bad, {feed})

    def test_audit_stderr_is_required_and_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "audit.stderr.log"
            with self.assertRaises(ValueError):
                gate.validate_empty_stderr(path)
            path.write_bytes(b"")
            gate.validate_empty_stderr(path)
            path.write_bytes(b"warning: vulnerability feed unavailable")
            with self.assertRaises(ValueError):
                gate.validate_empty_stderr(path)

    def test_exact_project_and_framework_audit(self):
        path = Path("exact.csproj").resolve()
        report = {"version": 1, "parameters": "--vulnerable --include-transitive", "sources": ["https://api.nuget.org/v3/index.json"],
                  "projects": [{"path": str(path), "frameworks": [{"framework": "net10.0"}]}]}
        gate.validate_audit(report, [path])
        for change in ("missing", "other", "duplicate", "framework", "problems", "vulnerabilities"):
            bad = copy.deepcopy(report)
            if change == "missing":
                bad["projects"] = []
            elif change == "other":
                bad["projects"][0]["path"] = "other.csproj"
            elif change == "duplicate":
                bad["projects"] *= 2
            elif change == "framework":
                bad["projects"][0]["frameworks"][0]["framework"] = "net9.0"
            else:
                bad[change] = ["unavailable or unsafe"]
            with self.subTest(change=change), self.assertRaises(ValueError):
                gate.validate_audit(bad, [path])
        for value in (True, False, "1", 1.0, None):
            bad = copy.deepcopy(report)
            bad["version"] = value
            with self.subTest(version=value), self.assertRaises(ValueError):
                gate.validate_audit(bad, [path])
        for key in ("topLevelPackages", "transitivePackages"):
            for value in ([{"id": "affected-package", "resolvedVersion": "1.0.0"}], {}, None, False, ""):
                bad = copy.deepcopy(report)
                bad["projects"][0]["frameworks"][0][key] = value
                with self.subTest(collection=key, value=value), self.assertRaises(ValueError):
                    gate.validate_audit(bad, [path])
        for location in ("root", "framework"):
            for value in (["NU1900 audit unavailable"], {}, None, False, ""):
                bad = copy.deepcopy(report)
                target = bad if location == "root" else bad["projects"][0]["frameworks"][0]
                target["warnings"] = value
                with self.subTest(warnings=location, value=value), self.assertRaises(ValueError):
                    gate.validate_audit(bad, [path])
        for value in ([], None, "https://api.nuget.org/v3/index.json", ["https://unreviewed.invalid/index.json"]):
            bad = copy.deepcopy(report)
            bad["sources"] = value
            with self.subTest(sources=value), self.assertRaises(ValueError):
                gate.validate_audit(bad, [path])
        for value in (None, "--include-transitive", "--vulnerable"):
            bad = copy.deepcopy(report)
            bad["parameters"] = value
            with self.subTest(parameters=value), self.assertRaises(ValueError):
                gate.validate_audit(bad, [path])

    def test_original_discovery_must_be_complete_and_unique(self):
        self.assertEqual({"A.Case", "B.Case"}, gate.discovery_names("The following Tests are available:\n  A.Case\n  B.Case\n"))
        for text in ("", "The following Tests are available:", "The following Tests are available:\n A\n A"):
            with self.assertRaises(ValueError):
                gate.discovery_names(text)

    def test_trx_names_ids_assembly_counts_and_outcomes(self):
        data = b'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results><UnitTestResult testId="11111111-1111-1111-1111-111111111111" executionId="22222222-2222-2222-2222-222222222222" testName="A.Case" outcome="Passed" /></Results>
<TestDefinitions><UnitTest id="11111111-1111-1111-1111-111111111111" name="A.Case"><Execution id="22222222-2222-2222-2222-222222222222" /><TestMethod codeBase="/native/Exact.dll" /></UnitTest></TestDefinitions>
<TestEntries><TestEntry testId="11111111-1111-1111-1111-111111111111" executionId="22222222-2222-2222-2222-222222222222" testListId="33333333-3333-3333-3333-333333333333" /></TestEntries><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary></TestRun>'''
        gate.validate_trx(data, {"A.Case"}, "Exact")
        for old, new in ((b'testId="11111111-1111-1111-1111-111111111111"', b'testId="44444444-4444-4444-4444-444444444444"'), (b'Exact.dll', b'Other.dll'),
                         (b'outcome="Passed"', b'outcome="NotExecuted"'), (b'passed="1"', b'passed="0"'),
                         (b'notExecuted="0"', b'notExecuted="1"')):
            with self.subTest(old=old), self.assertRaises(ValueError):
                gate.validate_trx(data.replace(old, new), {"A.Case"}, "Exact")
        with self.assertRaises(ValueError):
            gate.validate_trx(data, {"A.Case", "Missing.Case"}, "Exact")
        with self.assertRaises(ValueError):
            gate.validate_trx(b'<!DOCTYPE unsafe>' + data, {"A.Case"}, "Exact")
        with self.assertRaises(ValueError):
            gate.validate_trx(data.replace(b'<Execution id="22222222-2222-2222-2222-222222222222"',
                                          b'<Execution id="44444444-4444-4444-4444-444444444444"'), {"A.Case"}, "Exact")
        for change in ("failed-summary", "missing-summary", "duplicate-summary", "missing-exceptional-counter", "zero-guid"):
            root = ET.fromstring(data)
            summary = root.find(gate.NS + "ResultSummary")
            if change == "failed-summary":
                summary.set("outcome", "Failed")
            elif change == "missing-summary":
                root.remove(summary)
            elif change == "duplicate-summary":
                root.append(copy.deepcopy(summary))
            elif change == "missing-exceptional-counter":
                del summary.find(gate.NS + "Counters").attrib["error"]
            else:
                for node in root.iter():
                    for key in ("id", "testId", "executionId", "testListId"):
                        if key in node.attrib:
                            node.set(key, "00000000-0000-0000-0000-000000000000")
            with self.subTest(change=change), self.assertRaises(ValueError):
                gate.validate_trx(ET.tostring(root), {"A.Case"}, "Exact")

    def test_only_source_bound_delayed_theory_expansion_is_allowed(self):
        ns = gate.NS
        root = ET.Element(ns + "TestRun")
        definitions = ET.SubElement(root, ns + "TestDefinitions")
        results = ET.SubElement(root, ns + "Results")
        entries = ET.SubElement(root, ns + "TestEntries")
        for index in range(10):
            identity, execution = str(uuid.uuid4()), str(uuid.uuid4())
            name = "A.Theory(row: " + str(index) + ")"
            case = ET.SubElement(definitions, ns + "UnitTest", id=identity, name=name)
            ET.SubElement(case, ns + "Execution", id=execution)
            ET.SubElement(case, ns + "TestMethod", codeBase="/native/Exact.dll")
            ET.SubElement(results, ns + "UnitTestResult", testId=identity, executionId=execution, testName=name, outcome="Passed")
            ET.SubElement(entries, ns + "TestEntry", testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
        summary = ET.SubElement(root, ns + "ResultSummary", outcome="Completed")
        ET.SubElement(summary, ns + "Counters", **{key: "10" if key in ("total", "executed", "passed") else "0" for key in gate.REQUIRED_COUNTERS})
        data = ET.tostring(root)
        gate.validate_trx(data, {"A.Theory"}, "Exact", {"A.Theory": 10})
        with self.assertRaises(ValueError):
            gate.validate_trx(data, {"A.Theory"}, "Exact")
        with self.assertRaises(ValueError):
            gate.validate_trx(data, {"A.Theory"}, "Exact", {"A.Theory": 11})
        with self.assertRaises(ValueError):
            gate.validate_trx(data, {"Other.Theory"}, "Exact", {"A.Theory": 10})
        duplicate = ET.fromstring(data)
        for case in duplicate.findall(gate.NS + "TestDefinitions/" + gate.NS + "UnitTest"):
            case.set("name", "A.Theory(row: 0)")
        for result in duplicate.findall(gate.NS + "Results/" + gate.NS + "UnitTestResult"):
            result.set("testName", "A.Theory(row: 0)")
        with self.assertRaises(ValueError):
            gate.validate_trx(ET.tostring(duplicate), {"A.Theory"}, "Exact", {"A.Theory": 10})


if __name__ == "__main__":
    unittest.main()
