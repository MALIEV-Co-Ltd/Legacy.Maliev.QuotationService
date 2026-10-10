"""Synthetic TRX rejection controls; these never claim C# execution."""
import copy
from pathlib import Path
import sys
import unittest
import uuid
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).parents[1]))
import check_quotation_analytics_results as gate
import check_quotation_candidate_native as native


def fixture():
    names = [gate.CLASS + "." + method + (f"(row: {row})" if count > 1 else "")
             for method, count in gate.METHOD_COUNTS.items() for row in range(count)]
    root = ET.Element(native.NS + "TestRun")
    definitions = ET.SubElement(root, native.NS + "TestDefinitions")
    results = ET.SubElement(root, native.NS + "Results")
    entries = ET.SubElement(root, native.NS + "TestEntries")
    for name in names:
        identity, execution, listing = (str(uuid.uuid4()) for _ in range(3))
        case = ET.SubElement(definitions, native.NS + "UnitTest", id=identity, name=name)
        ET.SubElement(case, native.NS + "Execution", id=execution)
        ET.SubElement(case, native.NS + "TestMethod", codeBase=gate.ASSEMBLY + ".dll")
        ET.SubElement(results, native.NS + "UnitTestResult", testId=identity, executionId=execution, testName=name, outcome="Passed")
        ET.SubElement(entries, native.NS + "TestEntry", testId=identity, executionId=execution, testListId=listing)
    summary = ET.SubElement(root, native.NS + "ResultSummary", outcome="Completed")
    ET.SubElement(summary, native.NS + "Counters", **{key: "37" if key in ("total", "executed", "passed") else "0" for key in native.REQUIRED_COUNTERS})
    discovery = "The following Tests are available:\n" + "\n".join(names)
    return discovery, root


class AnalyticsEvidenceTests(unittest.TestCase):
    def test_terminal_green_joins_exact_four_original_executions(self):
        discovery, root = lease_fixture()
        self.assertEqual(gate.LEASE_CASES, gate.validate_lease_focus(discovery, ET.tostring(root)))

    def test_terminal_red_requires_all_four_specific_assertion_failures_without_mutating_original(self):
        discovery, root = lease_fixture(red=True)
        original = ET.tostring(root)
        self.assertEqual(gate.LEASE_CASES, gate.validate_lease_focus(discovery, original, red=True))
        self.assertEqual(original, ET.tostring(root))

    def test_terminal_red_rejects_unrelated_failure_partial_pass_skip_identity_and_counter_defects(self):
        discovery, root = lease_fixture(red=True)
        for mutation in ("setup", "timeout", "wrong-assertion", "wrong-stack", "missing-error", "passed", "skipped",
                         "missing", "duplicate-execution", "wrong-entry", "wrong-definition", "assembly", "counters", "summary"):
            bad = copy.deepcopy(root)
            results = bad.find(native.NS + "Results")
            error = results[0].find("./" + native.NS + "Output/" + native.NS + "ErrorInfo")
            if mutation == "setup": error.find(native.NS + "Message").text = "NpgsqlException: fixture failed"
            elif mutation == "timeout": error.find(native.NS + "Message").text = "OperationCanceledException"
            elif mutation == "wrong-assertion": error.find(native.NS + "Message").text = "Assert.Equal() Failure"
            elif mutation == "wrong-stack": error.find(native.NS + "StackTrace").text = "Foreign.Case"
            elif mutation == "missing-error": results[0].remove(results[0].find(native.NS + "Output"))
            elif mutation == "passed": results[0].set("outcome", "Passed")
            elif mutation == "skipped": results[0].set("outcome", "NotExecuted")
            elif mutation == "missing": results.remove(results[0])
            elif mutation == "duplicate-execution": results[1].set("executionId", results[0].get("executionId"))
            elif mutation == "wrong-entry": bad.find(native.NS + "TestEntries")[0].set("testId", str(uuid.uuid4()))
            elif mutation == "wrong-definition": bad.find(native.NS + "TestDefinitions")[0].set("name", "Foreign.Case")
            elif mutation == "assembly": bad.find(".//" + native.NS + "TestMethod").set("codeBase", "Foreign.dll")
            elif mutation == "counters": bad.find(".//" + native.NS + "Counters").set("timeout", "1")
            else: bad.find(native.NS + "ResultSummary").set("outcome", "Completed")
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                gate.validate_lease_focus(discovery, ET.tostring(bad), red=True)

    def test_terminal_profiles_deny_missing_extra_duplicate_or_different_discovery(self):
        for red in (True, False):
            discovery, root = lease_fixture(red=red)
            names = discovery.splitlines()
            for changed in ("\n".join(names[:-1]), discovery + "\nForeign.Case", discovery + "\n" + names[-1],
                            discovery.replace("staleStatus: 400", "staleStatus: 403")):
                with self.subTest(red=red), self.assertRaises(ValueError):
                    gate.validate_lease_focus(changed, ET.tostring(root), red=red)

    def test_terminal_green_denies_red_skipped_or_partial_original_results(self):
        discovery, root = lease_fixture()
        for mutation in ("failed", "skipped", "missing"):
            bad = copy.deepcopy(root)
            results = bad.find(native.NS + "Results")
            if mutation == "missing": results.remove(results[0])
            else: results[0].set("outcome", "Failed" if mutation == "failed" else "NotExecuted")
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                gate.validate_lease_focus(discovery, ET.tostring(bad))

    def test_coverage_requires_all_focus_cases_and_complete_passing_trx(self):
        discovery, root = fixture()
        names = gate.validate_focus(discovery, ET.tostring(root))
        gate.validate_coverage(names, discovery, ET.tostring(root))
        with self.assertRaisesRegex(ValueError, "omits"):
            gate.validate_coverage(names, "\n".join(discovery.splitlines()[:-1]), ET.tostring(root))
        root.find(native.NS + "Results")[0].set("outcome", "Failed")
        with self.assertRaises(ValueError):
            gate.validate_coverage(names, discovery, ET.tostring(root))

    def test_complete_mocked_focus_join(self):
        discovery, root = fixture()
        self.assertEqual(37, len(gate.validate_focus(discovery, ET.tostring(root))))

    def test_missing_extra_duplicate_and_foreign_discovery_are_denied(self):
        discovery, root = fixture()
        rows = discovery.splitlines()
        mutations = ("\n".join(rows[:-1]), discovery + "\nForeign.Case", discovery + "\n" + rows[-1],
                     discovery.replace(gate.CLASS, "Foreign.Class"), discovery.replace("Classification_", "Different_"))
        for value in mutations:
            with self.subTest(value=value[:60]), self.assertRaises(ValueError):
                gate.validate_focus(value, ET.tostring(root))

    def test_failed_skipped_missing_duplicate_execution_and_wrong_assembly_are_denied(self):
        discovery, root = fixture()
        for mutation in ("failed", "skipped", "missing", "duplicate", "assembly", "counters"):
            bad = copy.deepcopy(root); results = bad.find(native.NS + "Results")
            if mutation == "failed": results[0].set("outcome", "Failed")
            elif mutation == "skipped": results[0].set("outcome", "NotExecuted")
            elif mutation == "missing": results.remove(results[0])
            elif mutation == "duplicate": results[1].set("executionId", results[0].get("executionId"))
            elif mutation == "assembly": bad.find(".//" + native.NS + "TestMethod").set("codeBase", "Foreign.dll")
            else: bad.find(".//" + native.NS + "Counters").set("passed", "36")
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                gate.validate_focus(discovery, ET.tostring(bad))


def lease_fixture(red=False):
    _, root = fixture()
    names = sorted(gate.LEASE_CASES)
    definitions = root.find(native.NS + "TestDefinitions")
    results = root.find(native.NS + "Results")
    entries = root.find(native.NS + "TestEntries")
    for node in (definitions, results, entries):
        for child in list(node)[4:]: node.remove(child)
    for definition, result, name in zip(definitions, results, names):
        definition.set("name", name)
        result.set("testName", name)
        if red:
            result.set("outcome", "Failed")
            error = ET.SubElement(ET.SubElement(result, native.NS + "Output"), native.NS + "ErrorInfo")
            ET.SubElement(error, native.NS + "Message").text = (
                "Assert.Empty() Failure: Collection was not empty" if name.endswith("currentStatus: 204)")
                else "Assert.Single() Failure: The collection contained 2 items")
            ET.SubElement(error, native.NS + "StackTrace").text = "at " + gate.LEASE_METHOD + " in fixture.cs:line 1"
    summary = root.find(native.NS + "ResultSummary")
    summary.set("outcome", "Failed" if red else "Completed")
    counters = summary.find(native.NS + "Counters")
    for key in native.REQUIRED_COUNTERS:
        counters.set(key, "4" if key in ("total", "executed", "failed" if red else "passed") else "0")
    return "The following Tests are available:\n" + "\n".join(names), root


if __name__ == "__main__":
    unittest.main()
