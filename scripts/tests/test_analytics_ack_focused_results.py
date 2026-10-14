import importlib.util
from pathlib import Path
import sys
import unittest
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("ack_results", Path(__file__).parents[1] / "check_analytics_ack_focused_results.py")
module = importlib.util.module_from_spec(spec)
sys.path.insert(0, str(Path(__file__).parents[1]))
try:
    spec.loader.exec_module(module)
finally:
    sys.path.pop(0)
TAG = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def document(race=True):
    root = ET.Element(TAG + "TestRun")
    summary = ET.SubElement(root, TAG + "ResultSummary", outcome="Completed")
    counters = {key: "0" for key in module.COUNTERS}
    count = 4 if race else 3
    counters.update(total=str(count), executed=str(count), passed=str(count))
    ET.SubElement(summary, TAG + "Counters", **counters)
    definitions = ET.SubElement(root, TAG + "TestDefinitions")
    results = ET.SubElement(root, TAG + "Results")
    entries = ET.SubElement(root, TAG + "TestEntries")
    cases = [(module.RACE, f"(staleStatus: {left}, currentStatus: {right})") for left, right in ((400, 204), (500, 204), (400, 400), (500, 500))] if race else [(method, "") for method in module.FACTS]
    for method, arguments in cases:
        identity, execution = str(uuid.uuid4()), str(uuid.uuid4())
        definition = ET.SubElement(definitions, TAG + "UnitTest", id=identity)
        ET.SubElement(definition, TAG + "TestMethod", className=module.CLASS, name=method)
        ET.SubElement(definition, TAG + "Execution", id=execution)
        ET.SubElement(results, TAG + "UnitTestResult", testId=identity, executionId=execution, outcome="Passed", testName=module.CLASS + "." + method + arguments)
        ET.SubElement(entries, TAG + "TestEntry", testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
    return root


class AnalyticsAckFocusedResults(unittest.TestCase):
    def reject(self, mutate, race=True):
        root = document(race)
        mutate(root)
        with self.assertRaises(ValueError):
            module.validate_bytes(ET.tostring(root), module.RACE_SPEC if race else module.SETTLEMENT_SPEC)

    def test_exact_four_integer_race_cases_pass(self):
        module.validate_bytes(ET.tostring(document()), module.RACE_SPEC)

    def test_exact_three_settlement_facts_pass(self):
        module.validate_bytes(ET.tostring(document(False)), module.SETTLEMENT_SPEC)

    def test_zero_discovery_fails(self):
        self.reject(lambda r: r.find(TAG + "ResultSummary/" + TAG + "Counters").set("total", "0"))

    def test_wrong_named_integer_argument_fails(self):
        self.reject(lambda r: r.find(TAG + "Results/" + TAG + "UnitTestResult").set("testName", module.CLASS + "." + module.RACE + "(staleStatus: BadRequest, currentStatus: 204)"))

    def test_duplicate_theory_case_fails(self):
        self.reject(lambda r: r.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("testName", r.find(TAG + "Results/" + TAG + "UnitTestResult").get("testName")))

    def test_wrong_class_fails(self):
        self.reject(lambda r: r.find(TAG + "TestDefinitions/" + TAG + "UnitTest/" + TAG + "TestMethod").set("className", "Wrong.Class"))

    def test_missing_entry_fails(self):
        self.reject(lambda r: r.find(TAG + "TestEntries").remove(r.find(TAG + "TestEntries/" + TAG + "TestEntry")))

    def test_skipped_settlement_fails(self):
        self.reject(lambda r: r.find(TAG + "Results/" + TAG + "UnitTestResult").set("outcome", "NotExecuted"), race=False)

    def test_repeated_execution_fails(self):
        self.reject(lambda r: r.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("executionId", r.find(TAG + "Results/" + TAG + "UnitTestResult").get("executionId")))


if __name__ == "__main__":
    unittest.main()
