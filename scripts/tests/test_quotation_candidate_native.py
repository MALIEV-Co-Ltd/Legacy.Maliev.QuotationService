import copy
import importlib.util
from pathlib import Path
import unittest
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("native_gate", Path(__file__).parents[1] / "check_quotation_candidate_native.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class NativeEvidenceTests(unittest.TestCase):
    def test_exact_project_and_framework_audit(self):
        path = Path("exact.csproj").resolve()
        report = {"version": 1, "projects": [{"path": str(path), "frameworks": [{"framework": "net10.0"}]}]}
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
