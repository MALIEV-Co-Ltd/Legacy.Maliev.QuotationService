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


if __name__ == "__main__":
    unittest.main()
