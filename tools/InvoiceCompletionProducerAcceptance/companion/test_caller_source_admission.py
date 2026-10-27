"""Actor-free controls for exact caller admission; no runtime receipt creation."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent


class Controls(unittest.TestCase):
    def setUp(self):
        path = ROOT / 'scanner-shared-bridge/qualify_hosted_pair.py'
        spec = importlib.util.spec_from_file_location('caller_source_admission_control', path)
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.manifest_path = ROOT / 'file-front-caller-source-manifest.json'
        self.manifest = self.manifest_path.read_bytes()
        self.root = ROOT.parents[2]
        self.rows = json.loads(self.manifest)['modules']
        self.data = {str(self.root / row['path']): (self.root / row['path']).read_bytes() for row in self.rows}
        self.data.update({str(self.root / row['path']): (self.root / row['path']).read_bytes() for row in json.loads(self.manifest)['controls']})
        self.data[str(self.manifest_path)] = self.manifest
        self.reads = []

    def read(self, path, limit):
        self.reads.append(str(path))
        data = self.data[str(path)]
        self.assertLessEqual(len(data), limit)
        return data

    def test_all_exact_sources_held_before_import(self):
        with patch.object(self.module, 'bounded_read', self.read):
            held = self.module.admit_caller_sources(self.root)
        self.assertEqual(set(held), set(self.module.CALLER_MODULE_PATHS))
        self.assertEqual(len(self.reads), len(held) + len(self.module.CALLER_CONTROL_PATHS) + 1)
        self.assertTrue(all(hashlib.sha256(held[row['name']]).hexdigest() == row['sha256'] for row in self.rows))

    def test_replaced_dependency_refuses_before_import(self):
        for row in self.rows:
            with self.subTest(name=row['name']):
                key = str(self.root / row['path'])
                old = self.data[key]
                self.data[key] = b'foreign = True\n'
                with patch.object(self.module, 'bounded_read', self.read), self.assertRaises(self.module.Refused):
                    self.module.admit_caller_sources(self.root)
                self.data[key] = old

    def make_caller(self):
        with patch.object(self.module, 'bounded_read', self.read):
            held = self.module.admit_caller_sources(self.root)
        owner = self.module.CallerHeldImports(self.root, held)
        caller = types.ModuleType('run_eight_host_financial_acceptance')
        caller.__spec__ = importlib.util.spec_from_loader(caller.__name__, owner)
        caller.__file__ = str(self.root / self.module.CALLER_MODULE_PATHS[caller.__name__])
        # Explicit model of an original completed module birth; no runtime proof.
        owner._caller_births[caller.__name__] = (caller, caller.__dict__, True)
        return caller, owner

    def test_replacement_after_import_blocks_front_birth(self):
        caller, owner = self.make_caller()
        born = []
        caller.prepare_file_front_startup = lambda *args: born.append(True)
        row = next(row for row in self.rows if row['name'] == 'owned_front_host')
        self.data[str(self.root / row['path'])] = b'foreign = True\n'
        with patch.dict(sys.modules, {caller.__name__: caller}), patch.object(self.module, 'bounded_read', self.read):
            with self.assertRaises(self.module.Refused):
                self.module.prepare_qualified_file_front_startup(caller)
        self.assertEqual(born, [])

    def test_foreign_import_blocks_front_birth(self):
        caller, owner = self.make_caller()
        born = []
        caller.prepare_file_front_startup = lambda *args: born.append(True)
        foreign = types.ModuleType('owned_front_host')
        foreign.__spec__ = importlib.util.spec_from_loader(foreign.__name__, object())
        with patch.dict(sys.modules, {caller.__name__: caller, foreign.__name__: foreign}), patch.object(self.module, 'bounded_read', self.read):
            with self.assertRaises(self.module.Refused):
                self.module.prepare_qualified_file_front_startup(caller)
        self.assertEqual(born, [])

    def test_forged_original_loader_metadata_blocks_front_birth(self):
        caller, owner = self.make_caller()
        born = []
        foreign = types.ModuleType(caller.__name__)
        foreign.__spec__ = caller.__spec__
        foreign.__file__ = caller.__file__
        foreign.prepare_file_front_startup = lambda *args: born.append(True)
        with patch.dict(sys.modules, {caller.__name__: foreign}), patch.object(self.module, 'bounded_read', self.read):
            with self.assertRaises(self.module.Refused):
                self.module.prepare_qualified_file_front_startup(foreign)
        self.assertEqual(born, [])

    def test_repeated_and_partial_module_exec_retain_original_birth(self):
        for source in (b'value = 1\n', b'raise RuntimeError()\n'):
            held = {'owned_front_host': source}
            owner = self.module.CallerHeldImports(self.root, held)
            original = types.ModuleType('owned_front_host')
            if b'raise' in source:
                with self.assertRaises(RuntimeError):
                    owner.exec_module(original)
                self.assertFalse(owner._caller_births[original.__name__][2])
            else:
                owner.exec_module(original)
                self.assertTrue(owner._caller_births[original.__name__][2])
            with self.assertRaises(self.module.Refused):
                owner.exec_module(types.ModuleType(original.__name__))
            self.assertIs(owner._caller_births[original.__name__][0], original)

    def test_original_authority_refusal_remains(self):
        raw = (ROOT / 'run_eight_host_financial_acceptance.py').read_text()
        import ast
        tree = ast.parse(raw)
        for name in ('main', 'start_authenticated_file'):
            fn = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == name)
            raises = [node for node in ast.walk(fn) if isinstance(node, ast.Raise)]
            self.assertEqual(len(raises), 1)
            self.assertEqual(raises[0].exc.func.id, 'MissingOrdinaryFileAuthority')


if __name__ == '__main__':
    unittest.main()
