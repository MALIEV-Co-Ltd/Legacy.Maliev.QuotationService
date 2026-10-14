import importlib.util
from pathlib import Path
import unittest
import sys
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("c6_results", Path(__file__).parents[1] / "check_c6_source_index_results.py")
module = importlib.util.module_from_spec(spec)
sys.path.insert(0, str(Path(__file__).parents[1]))
try:
    spec.loader.exec_module(module)
finally:
    sys.path.pop(0)
TAG = module.TAG


def document():
    root = ET.Element(TAG + "TestRun")
    summary = ET.SubElement(root, TAG + "ResultSummary", outcome="Completed")
    counts = {key: "0" for key in module.COUNTERS}
    counts.update(total="4", executed="4", passed="4")
    ET.SubElement(summary, TAG + "Counters", **counts)
    definitions = ET.SubElement(root, TAG + "TestDefinitions")
    results = ET.SubElement(root, TAG + "Results")
    entries = ET.SubElement(root, TAG + "TestEntries")
    for class_name, method in sorted(module.EXPECTED):
        identity, execution = str(uuid.uuid4()), str(uuid.uuid4())
        definition = ET.SubElement(definitions, TAG + "UnitTest", id=identity)
        ET.SubElement(definition, TAG + "TestMethod", className=class_name, name=method)
        ET.SubElement(definition, TAG + "Execution", id=execution)
        ET.SubElement(results, TAG + "UnitTestResult", testId=identity, executionId=execution, outcome="Passed", testName=class_name + "." + method)
        ET.SubElement(entries, TAG + "TestEntry", testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
    return root


class C6FocusedResults(unittest.TestCase):
    def reject(self, mutate):
        root = document()
        mutate(root)
        with self.assertRaises(ValueError):
            module.validate_bytes(ET.tostring(root, encoding="utf-8"))

    def test_exact_four_facts_pass(self):
        self.assertEqual(len(module.validate_bytes(ET.tostring(document(), encoding="utf-8"))), 4)

    def test_full_suite_is_not_focused_evidence(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary/" + TAG + "Counters").set("total", "1387"))

    def test_missing_physical_case_fails(self):
        self.reject(lambda root: root.find(TAG + "Results").remove(root.findall(TAG + "Results/" + TAG + "UnitTestResult")[-1]))

    def test_wrong_class_fails(self):
        self.reject(lambda root: root.find(TAG + "TestDefinitions/" + TAG + "UnitTest/" + TAG + "TestMethod").set("className", "Wrong.Class"))

    def test_skipped_case_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary/" + TAG + "Counters").set("notExecuted", "1"))

    def test_failed_result_fails(self):
        self.reject(lambda root: root.find(TAG + "Results/" + TAG + "UnitTestResult").set("outcome", "Failed"))

    def test_repeated_execution_fails(self):
        self.reject(lambda root: root.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("executionId", root.find(TAG + "Results/" + TAG + "UnitTestResult").get("executionId")))

    def test_shared_fact_definition_fails(self):
        self.reject(lambda root: root.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("testId", root.find(TAG + "Results/" + TAG + "UnitTestResult").get("testId")))

    def test_entry_join_failure_fails(self):
        self.reject(lambda root: root.find(TAG + "TestEntries/" + TAG + "TestEntry").set("executionId", str(uuid.uuid4())))

    def test_missing_counter_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary/" + TAG + "Counters").attrib.pop("pending"))

    def test_incomplete_run_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary").set("outcome", "Failed"))

    def test_dtd_fails(self):
        with self.assertRaises(ValueError):
            module.validate_bytes(b'<!DOCTYPE TestRun [<!ENTITY x "no">]>' + ET.tostring(document(), encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
