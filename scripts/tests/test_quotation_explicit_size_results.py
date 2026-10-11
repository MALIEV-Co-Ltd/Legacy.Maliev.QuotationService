import importlib.util
import json
from pathlib import Path
import unittest
import uuid
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parents[1] / "check_quotation_explicit_size_results.py"
SPEC = importlib.util.spec_from_file_location("explicit_size_results", SCRIPT)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)
NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def tag(name):
    return "{" + NAMESPACE + "}" + name


class ExplicitSizeParityResultsTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads(gate.MANIFEST.read_text(encoding="utf-8"))
        self.root = ET.Element(tag("TestRun"))
        self.results = ET.SubElement(self.root, tag("Results"))
        self.definitions = ET.SubElement(self.root, tag("TestDefinitions"))
        self.entries = ET.SubElement(self.root, tag("TestEntries"))
        for name in self.manifest["names"]:
            test_id, execution_id = str(uuid.uuid4()), str(uuid.uuid4())
            ET.SubElement(self.results, tag("UnitTestResult"), testId=test_id, executionId=execution_id, testName=name, outcome="Passed")
            definition = ET.SubElement(self.definitions, tag("UnitTest"), id=test_id, name=name)
            ET.SubElement(definition, tag("Execution"), id=execution_id)
            binding = self.manifest["testMethods"][name]
            ET.SubElement(definition, tag("TestMethod"), className=binding["className"], name=binding["name"], codeBase="/synthetic/bin/Release/net10.0/" + binding["assembly"])
            ET.SubElement(self.entries, tag("TestEntry"), testId=test_id, executionId=execution_id)
        self.summary = ET.SubElement(self.root, tag("ResultSummary"), outcome="Completed")
        self.counters = ET.SubElement(self.summary, tag("Counters"), total="15", executed="15", passed="15", failed="0", error="0", timeout="0", aborted="0", inconclusive="0", notExecuted="0", disconnected="0", warning="0", notRunnable="0", passedButRunAborted="0", completed="0", inProgress="0", pending="0")

    def verify(self):
        return gate.validate(ET.tostring(self.root), self.manifest)

    def test_accepts_exact_fifteen_joined_passing_rows(self):
        self.assertEqual(15, self.verify())

    def test_refuses_missing_passed_but_run_aborted(self):
        del self.counters.attrib["passedButRunAborted"]
        with self.assertRaises(ValueError): self.verify()

    def test_refuses_missing_completed(self):
        del self.counters.attrib["completed"]
        with self.assertRaises(ValueError): self.verify()

    def test_refuses_missing_in_progress(self):
        del self.counters.attrib["inProgress"]
        with self.assertRaises(ValueError): self.verify()

    def test_refuses_missing_pending(self):
        del self.counters.attrib["pending"]
        with self.assertRaises(ValueError): self.verify()

    def test_refuses_unknown_counter_even_when_zero(self):
        self.counters.set("unreviewed", "0")
        with self.assertRaises(ValueError): self.verify()

    def test_refuses_nonzero_completion_counters(self):
        for key in ("passedButRunAborted", "completed", "inProgress", "pending"):
            with self.subTest(key=key):
                self.counters.set(key, "1")
                with self.assertRaises(ValueError): self.verify()
                self.counters.set(key, "0")

    def test_refuses_missing_and_extra_rows(self):
        self.results.remove(self.results[-1])
        with self.assertRaises(ValueError):
            self.verify()
        self.results.append(ET.Element(tag("UnitTestResult")))
        self.results.append(ET.Element(tag("UnitTestResult")))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_replaced_identity_with_unchanged_count(self):
        self.results[0].set("testName", "unrelated passing row")
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_duplicate_execution(self):
        self.results[0].set("executionId", self.results[1].get("executionId"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_definition_and_entry_mismatches(self):
        self.definitions[0].set("name", "wrong definition")
        with self.assertRaises(ValueError):
            self.verify()
        self.definitions[0].set("name", self.results[0].get("testName"))
        self.entries[0].set("executionId", str(uuid.uuid4()))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_failure_and_hidden_error(self):
        self.results[0].set("outcome", "Failed")
        with self.assertRaises(ValueError):
            self.verify()
        self.results[0].set("outcome", "Passed")
        output = ET.SubElement(self.results[0], tag("Output"))
        ET.SubElement(output, tag("ErrorInfo"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_forged_or_incomplete_summary(self):
        self.counters.set("notExecuted", "1")
        with self.assertRaises(ValueError):
            self.verify()
        self.counters.set("notExecuted", "0")
        self.summary.set("outcome", "Aborted")
        with self.assertRaises(ValueError):
            self.verify()
        self.summary.set("outcome", "Completed")
        del self.counters.attrib["failed"]
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_malformed_guid_and_manifest(self):
        self.results[0].set("executionId", "not-a-guid")
        with self.assertRaises(ValueError):
            self.verify()
        self.manifest["expectedCount"] = 13
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_summary_error_with_passing_counters(self):
        output = ET.SubElement(self.summary, tag("Output"))
        ET.SubElement(output, tag("ErrorInfo"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_nonpassing_or_missing_run_info_outcomes(self):
        infos = ET.SubElement(self.summary, tag("RunInfos"))
        info = ET.SubElement(infos, tag("RunInfo"))
        for outcome in (None, "Failed", "Error", "Warning", "Aborted"):
            with self.subTest(outcome=outcome):
                info.attrib.clear()
                if outcome is not None:
                    info.set("outcome", outcome)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_refuses_error_inside_passing_run_info(self):
        infos = ET.SubElement(self.summary, tag("RunInfos"))
        info = ET.SubElement(infos, tag("RunInfo"), outcome="Passed")
        ET.SubElement(info, tag("ErrorInfo"))
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_duplicate_equal_or_conflicting_definition_executions(self):
        execution = ET.SubElement(self.definitions[0], tag("Execution"))
        for value in (self.results[0].get("executionId"), str(uuid.uuid4())):
            with self.subTest(value=value):
                execution.set("id", value)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_refuses_noncanonical_guid_shapes_even_with_consistent_joins(self):
        for attribute in ("testId", "executionId"):
            original = self.results[0].get(attribute)
            for value in (original.replace("-", ""), "{" + original + "}", "urn:uuid:" + original, original.upper()):
                with self.subTest(attribute=attribute, value=value):
                    self.results[0].set(attribute, value)
                    self.entries[0].set(attribute, value)
                    if attribute == "testId":
                        self.definitions[0].set("id", value)
                    else:
                        self.definitions[0].find(tag("Execution")).set("id", value)
                    with self.assertRaises(ValueError):
                        self.verify()
            self.results[0].set(attribute, original)
            self.entries[0].set(attribute, original)
            if attribute == "testId":
                self.definitions[0].set("id", original)
            else:
                self.definitions[0].find(tag("Execution")).set("id", original)

    def test_refuses_ambiguous_summary_and_counters(self):
        extra_summary = ET.SubElement(self.root, tag("ResultSummary"), outcome="Completed")
        with self.assertRaises(ValueError):
            self.verify()
        self.root.remove(extra_summary)
        ET.SubElement(self.summary, tag("Counters"), **self.counters.attrib)
        with self.assertRaises(ValueError):
            self.verify()


    def test_refuses_missing_or_multiple_methods(self):
        method = self.definitions[0].find(tag("TestMethod"))
        self.definitions[0].remove(method)
        with self.assertRaises(ValueError):
            self.verify()
        self.definitions[0].append(method)
        ET.SubElement(self.definitions[0], tag("TestMethod"), **method.attrib)
        with self.assertRaises(ValueError):
            self.verify()

    def test_refuses_mismatched_class_method_and_assembly(self):
        method = self.definitions[0].find(tag("TestMethod"))
        for attribute in ("className", "name", "codeBase"):
            with self.subTest(attribute=attribute):
                original = method.get(attribute)
                method.set(attribute, "unrelated")
                with self.assertRaises(ValueError):
                    self.verify()
                method.set(attribute, original)

    def test_refuses_incomplete_or_forged_manifest_method_bindings(self):
        name = self.manifest["names"][0]
        binding = self.manifest["testMethods"].pop(name)
        with self.assertRaises(ValueError):
            self.verify()
        self.manifest["testMethods"][name] = binding
        binding["name"] = "unrelated"
        with self.assertRaises(ValueError):
            self.verify()


if __name__ == "__main__":
    unittest.main()
