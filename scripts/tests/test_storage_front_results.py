"""Reject forged native front evidence without activating any host or backend."""
import copy
import importlib.util
from pathlib import Path
import sys
import unittest
import uuid
import xml.etree.ElementTree as ET

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location("storage_front_gate", SCRIPTS / "check_storage_front_results.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class FrontEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.inventory = {
            "Front.Pure": {"methods": {"Accept": {"parameters": [], "cases": [gate.case_hash([])]}}},
            "Front.Http": {"methods": {"Probe": {"parameters": [{"name": "size", "type": "int"}],
                                                "cases": [gate.case_hash([0])]}}},
        }
        self.root = ET.Element(gate.NS + "TestRun")
        summary = ET.SubElement(self.root, gate.NS + "ResultSummary", outcome="Completed")
        ET.SubElement(summary, gate.NS + "Counters", **{key: "2" if key in gate.COUNTERS[:3] else "0" for key in gate.COUNTERS})
        definitions = ET.SubElement(self.root, gate.NS + "TestDefinitions")
        results = ET.SubElement(self.root, gate.NS + "Results")
        entries = ET.SubElement(self.root, gate.NS + "TestEntries")
        for class_name, method, suffix in [("Front.Pure", "Accept", ""), ("Front.Http", "Probe", "(size: 0)")]:
            identity, execution, test_list = [str(uuid.uuid4()) for _ in range(3)]
            definition = ET.SubElement(definitions, gate.NS + "UnitTest", id=identity)
            ET.SubElement(definition, gate.NS + "TestMethod", className=class_name, name=method)
            ET.SubElement(definition, gate.NS + "Execution", id=execution)
            ET.SubElement(results, gate.NS + "UnitTestResult", executionId=execution, testId=identity,
                          testName=class_name + "." + method + suffix, outcome="Passed")
            ET.SubElement(entries, gate.NS + "TestEntry", executionId=execution, testId=identity, testListId=test_list)

    def validate(self):
        return gate.validate_bytes(ET.tostring(self.root), self.inventory)

    def test_exact_multi_class_report_is_accepted(self):
        self.assertEqual(2, self.validate())

    def test_actual_clr_boolean_display_names_preserve_case_identity(self):
        parameters = [{"name": "allowed", "type": "bool"}]
        for raw, expected in (("True", True), ("False", False), ("true", True), ("false", False)):
            with self.subTest(raw=raw):
                self.assertEqual(gate.case_hash([expected]), gate.display_digest(
                    "Front.Http.Socket(allowed: " + raw + ")", "Front.Http", "Socket", parameters))

    def test_nonboolean_display_values_cannot_gain_boolean_authority(self):
        parameters = [{"name": "allowed", "type": "bool"}]
        for raw in ("1", "0", '"True"', "null"):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                gate.display_digest("Front.Http.Socket(allowed: " + raw + ")", "Front.Http", "Socket", parameters)

    def test_counter_success_does_not_hide_a_missing_case(self):
        results = self.root.find(gate.NS + "Results")
        results.remove(results[1])
        with self.assertRaises(ValueError):
            self.validate()

    def test_duplicate_execution_is_rejected(self):
        results = self.root.find(gate.NS + "Results")
        results.append(copy.deepcopy(results[0]))
        with self.assertRaises(ValueError):
            self.validate()

    def test_substituted_scalar_case_is_rejected(self):
        result = self.root.find(gate.NS + "Results")[1]
        result.set("testName", "Front.Http.Probe(size: 1)")
        with self.assertRaises(ValueError):
            self.validate()

    def test_foreign_class_definition_is_rejected(self):
        self.root.find(gate.NS + "TestDefinitions")[1].find(gate.NS + "TestMethod").set("className", "Other.Http")
        with self.assertRaises(ValueError):
            self.validate()

    def test_definition_execution_must_join_an_actual_case(self):
        self.root.find(gate.NS + "TestDefinitions")[0].find(gate.NS + "Execution").set("id", str(uuid.uuid4()))
        with self.assertRaises(ValueError):
            self.validate()

    def test_entries_must_join_actual_executions(self):
        self.root.find(gate.NS + "TestEntries")[0].set("executionId", str(uuid.uuid4()))
        with self.assertRaises(ValueError):
            self.validate()

    def test_failed_or_skipped_case_cannot_pass(self):
        result = self.root.find(gate.NS + "Results")[0]
        for outcome in ("Failed", "NotExecuted"):
            with self.subTest(outcome=outcome):
                result.set("outcome", outcome)
                with self.assertRaises(ValueError):
                    self.validate()

    def test_dtd_or_oversized_report_is_rejected(self):
        for data in (b'<!DOCTYPE TestRun><TestRun/>', b" " * (gate.MAXIMUM + 1)):
            with self.assertRaises(ValueError):
                gate.validate_bytes(data, self.inventory)


if __name__ == "__main__":
    unittest.main()
