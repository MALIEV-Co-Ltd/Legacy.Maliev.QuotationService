import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
import xml.etree.ElementTree as ET

SCRIPTS = Path(__file__).parents[1]
spec = importlib.util.spec_from_file_location('inspection_results', SCRIPTS / 'check_docker_context_inspection_results.py')
module = importlib.util.module_from_spec(spec)
sys.path.insert(0, str(SCRIPTS))
try:
    spec.loader.exec_module(module)
    from check_c821_focused_results import COUNTERS
finally:
    sys.path.pop(0)
INVENTORY = json.loads((SCRIPTS / 'docker_context_inspection_focus.json').read_bytes())
TAG = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'

def document():
    root = ET.Element(TAG + 'TestRun')
    summary = ET.SubElement(root, TAG + 'ResultSummary', outcome='Completed')
    counters = dict.fromkeys(COUNTERS, '0')
    counters.update(total='23', executed='23', passed='23')
    ET.SubElement(summary, TAG + 'Counters', **counters)
    definitions = ET.SubElement(root, TAG + 'TestDefinitions')
    results = ET.SubElement(root, TAG + 'Results')
    entries = ET.SubElement(root, TAG + 'TestEntries')
    for name, row in INVENTORY['methods'].items():
        if not row['parameters']: arguments = [None]
        elif row['parameters'][0]['type'] == 'string': arguments = ['"pid"', '"birth"', '"executable"', '"unavailable"']
        else: arguments = ['True', 'False']
        identity = str(uuid.uuid4())
        for index, argument in enumerate(arguments):
            execution = str(uuid.uuid4())
            if index == 0:
                definition = ET.SubElement(definitions, TAG + 'UnitTest', id=identity)
                ET.SubElement(definition, TAG + 'TestMethod', className=INVENTORY['className'], name=name,
                              codeBase='/tests/Legacy.Maliev.QuotationService.Tests.dll')
                ET.SubElement(definition, TAG + 'Execution', id=execution)
            display = INVENTORY['className'] + '.' + name
            if argument is not None: display += '(' + row['parameters'][0]['name'] + ': ' + argument + ')'
            ET.SubElement(results, TAG + 'UnitTestResult', testId=identity, executionId=execution, outcome='Passed', testName=display)
            ET.SubElement(entries, TAG + 'TestEntry', testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
    return root

class InspectionResultsTests(unittest.TestCase):
    def check(self, root): module.validate_inspection_bytes(ET.tostring(root), INVENTORY)
    def reject(self, mutate):
        root = document()
        mutate(root)
        with self.assertRaises(ValueError): self.check(root)
    def test_exact_23_cases_with_shared_theory_definitions_pass(self): self.check(document())
    def test_actual_linux_case_cannot_be_replaced_by_other_passing_fact(self):
        def mutate(root):
            row = next(row for row in root.findall(TAG + 'Results/' + TAG + 'UnitTestResult') if 'ActualInspectionTimeout_' in row.get('testName'))
            row.set('testName', INVENTORY['className'] + '.HealthyExitedProcess_ReturnsOnlyTheCanonicalLocalEndpoint')
        self.reject(mutate)
    def test_duplicate_identity_mutation_case_cannot_hide_missing_case(self):
        def mutate(root):
            rows = [row for row in root.findall(TAG + 'Results/' + TAG + 'UnitTestResult') if 'UnprovedRetainedIdentity_' in row.get('testName')]
            rows[1].set('testName', rows[0].get('testName'))
        self.reject(mutate)
    def test_pending_callback_bool_case_must_be_exact(self):
        def mutate(root):
            rows = [row for row in root.findall(TAG + 'Results/' + TAG + 'UnitTestResult') if 'PendingCancellationCallbacks_' in row.get('testName')]
            rows[1].set('testName', rows[0].get('testName'))
        self.reject(mutate)
    def test_wrong_assembly_fails(self):
        self.reject(lambda root: root.find('.//' + TAG + 'TestMethod').set('codeBase', '/other/Unreviewed.dll'))
    def test_error_info_under_passing_result_fails(self):
        self.reject(lambda root: ET.SubElement(root.find(TAG + 'Results/' + TAG + 'UnitTestResult'), TAG + 'ErrorInfo'))
    def test_skipped_actual_case_fails(self):
        self.reject(lambda root: root.find(TAG + 'ResultSummary/' + TAG + 'Counters').set('notExecuted', '1'))
    def test_changed_source_pin_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            repository = Path(directory)
            (repository / 'scripts').mkdir()
            altered = copy.deepcopy(INVENTORY)
            altered['sources'] = {'source.cs': '0' * 64}
            (repository / 'source.cs').write_bytes(b'changed')
            (repository / 'scripts/docker_context_inspection_focus.json').write_bytes(json.dumps(altered).encode())
            with self.assertRaises(ValueError): module.load_inventory(repository)
    def test_non_linux_cli_refuses_before_reading_artifacts(self):
        from unittest.mock import patch
        with patch.object(module.sys, 'platform', 'win32'):
            with self.assertRaisesRegex(ValueError, 'hosted Linux'): module.main()

if __name__ == '__main__': unittest.main()
