import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("c821_results", Path(__file__).parents[1] / "check_c821_focused_results.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
TAG = "{" + NS + "}"
INVENTORY = {"className": "Fixture.Class", "methods": {"Allowed": {"parameters": [{"name": "flag", "type": "bool"}],
             "cases": [module.case_hash([False]), module.case_hash([True])]}}}


def document(shared=False):
    root = ET.Element(TAG + "TestRun")
    summary = ET.SubElement(root, TAG + "ResultSummary", outcome="Completed")
    counts = {key: "0" for key in module.COUNTERS}
    counts.update(total="2", executed="2", passed="2")
    ET.SubElement(summary, TAG + "Counters", **counts)
    definitions = ET.SubElement(root, TAG + "TestDefinitions")
    results = ET.SubElement(root, TAG + "Results")
    entries = ET.SubElement(root, TAG + "TestEntries")
    identity = str(uuid.uuid4())
    for index, flag in enumerate([False, True]):
        execution = str(uuid.uuid4())
        if not shared: identity = str(uuid.uuid4())
        if not shared or index == 0:
            definition = ET.SubElement(definitions, TAG + "UnitTest", id=identity)
            ET.SubElement(definition, TAG + "TestMethod", className="Fixture.Class", name="Allowed")
            ET.SubElement(definition, TAG + "Execution", id=execution)
        ET.SubElement(results, TAG + "UnitTestResult", testId=identity, executionId=execution,
                      outcome="Passed", testName=f"Fixture.Class.Allowed(flag: {flag})")
        ET.SubElement(entries, TAG + "TestEntry", testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
    return root


class FocusedReceiptTests(unittest.TestCase):
    def check(self, root, transform=None):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "synthetic.trx"
            data = ET.tostring(root, encoding="utf-8")
            if transform: data = transform(data)
            report.write_bytes(data)
            module.validate(report, INVENTORY, Path(directory))

    def reject(self, mutate):
        root = document()
        mutate(root)
        with self.assertRaises(ValueError): self.check(root)

    def test_exact_executed_cases_pass(self): self.check(document())
    def test_shared_theory_definition_passes(self): self.check(document(shared=True))
    def test_missing_counter_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary/" + TAG + "Counters").attrib.pop("pending"))
    def test_noncompleted_summary_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary").set("outcome", "Failed"))
    def test_skipped_case_fails(self):
        self.reject(lambda root: root.find(TAG + "ResultSummary/" + TAG + "Counters").set("notExecuted", "1"))
    def test_wrong_group_fails(self):
        self.reject(lambda root: root.find(TAG + "TestDefinitions/" + TAG + "UnitTest/" + TAG + "TestMethod").set("className", "Wrong.Class"))
    def test_duplicated_execution_fails(self):
        self.reject(lambda root: root.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("executionId",
                    root.findall(TAG + "Results/" + TAG + "UnitTestResult")[0].get("executionId")))
    def test_duplicated_case_with_distinct_execution_fails(self):
        self.reject(lambda root: root.findall(TAG + "Results/" + TAG + "UnitTestResult")[1].set("testName", "Fixture.Class.Allowed(flag: False)"))
    def test_missing_definition_fails(self):
        self.reject(lambda root: root.find(TAG + "TestDefinitions").remove(root.find(TAG + "TestDefinitions/" + TAG + "UnitTest")))
    def test_unknown_result_identity_fails(self):
        self.reject(lambda root: root.find(TAG + "Results/" + TAG + "UnitTestResult").set("testId", str(uuid.uuid4())))
    def test_definition_execution_mismatch_fails(self):
        self.reject(lambda root: root.find(TAG + "TestDefinitions/" + TAG + "UnitTest/" + TAG + "Execution").set("id", str(uuid.uuid4())))
    def test_entry_execution_mismatch_fails(self):
        self.reject(lambda root: root.find(TAG + "TestEntries/" + TAG + "TestEntry").set("executionId", str(uuid.uuid4())))
    def test_noncanonical_guid_fails(self):
        self.reject(lambda root: root.find(TAG + "Results/" + TAG + "UnitTestResult").set("executionId", "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"))
    def test_incomplete_per_case_evidence_fails(self):
        self.reject(lambda root: root.find(TAG + "Results").remove(root.find(TAG + "Results/" + TAG + "UnitTestResult")))
    def test_dtd_fails(self):
        with self.assertRaises(ValueError): self.check(document(), lambda data: b'<!DOCTYPE TestRun [<!ENTITY x "x">]>' + data)
    def test_oversized_xml_fails(self):
        with self.assertRaises(ValueError): self.check(document(), lambda data: data + b" " * module.MAX_BYTES)
    def test_path_outside_owned_evidence_root_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "owned"
            root.mkdir()
            report = Path(directory) / "foreign.trx"
            report.write_bytes(ET.tostring(document()))
            with self.assertRaises(ValueError): module.validate(report, INVENTORY, root)
    def test_changed_source_inventory_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "scripts").mkdir()
            (root / "source.cs").write_text("changed")
            (root / "scripts/c821-focused-inventory.json").write_text('{"group":{"source":"source.cs","normalizedSourceSha256":"wrong"}}')
            with self.assertRaises(ValueError): module.load_inventory(root)

    def test_already_resolved_file_with_linked_root_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "synthetic.trx"
            report.write_bytes(ET.tostring(document()))
            linked = root.absolute()
            with patch.object(Path, "is_symlink", lambda entry: entry == linked):
                with self.assertRaisesRegex(ValueError, "Linked owned evidence root"):
                    module.validate(report.resolve(), INVENTORY, root)

    def test_already_resolved_file_with_linked_root_ancestor_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "owned"
            root.mkdir()
            report = root / "synthetic.trx"
            report.write_bytes(ET.tostring(document()))
            linked = root.absolute().parent
            resolved = report.resolve()
            with patch.object(Path, "is_symlink", lambda entry: entry == linked):
                with self.assertRaisesRegex(ValueError, "Linked owned evidence root"):
                    module.validate(resolved, INVENTORY, root)

    def test_linked_evidence_root_junction_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "synthetic.trx"
            report.write_bytes(ET.tostring(document()))
            resolved = report.resolve()
            with patch.object(Path, "is_junction", lambda entry: entry == root.absolute(), create=True):
                with self.assertRaisesRegex(ValueError, "Linked owned evidence root"):
                    module.validate(resolved, INVENTORY, root)
