"""Pure held-source and failure-fence controls; no actors or descriptor probes."""
import hashlib
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import qualify_hosted_pair as harness

# Workflow injects trusted held bytes before running these pure models.
HELD_DIAGNOSTIC_SOURCE = None
HELD_OWNER_SOURCES = None
HELD_H_SOURCE = None
HELD_BRIDGE_SOURCE = None
HELD_STORAGE_SOURCES = None
HELD_OBSERVER_SOURCES = None
ORIGINAL_RELAY_PREFIX = "def _relay(self):\n    relay = self.scanner.relay\n    require(relay is not None and not relay.stop.is_set() and relay.acceptor.is_alive()\n            and relay.ready_history_valid() and relay.listener.fileno() >= 0\n            and relay.listener.getsockname() == relay.endpoint\n            and relay.endpoint == ('127.0.0.1', self.scanner.port), 'Held relay unavailable')\n"


class HostedPairSourceControls(unittest.TestCase):
    def diagnostic_model(self):
        import types
        source = HELD_DIAGNOSTIC_SOURCE
        module = types.ModuleType('diagnostic_model')
        exec(compile(source, '<held-diagnostic-model>', 'exec'), module.__dict__)
        return module

    def test_actual_owner_guard_refusals_preserve_original_custody_and_quarantine(self):
        import types
        d = self.diagnostic_model()
        class ModelBridgeRefused(ValueError): pass
        borrowed = types.ModuleType('borrowed_scanner_bridge')
        borrowed.BridgeRefused = ModelBridgeRefused
        borrowed.BorrowedScannerBridge = object
        command = types.ModuleType('storage_owner_command')
        command.storage_command = lambda *args: self.fail('unexpected actor callback')
        quarantine = [object()]
        mocked = {'pair_failure_diagnostic': d, 'borrowed_scanner_bridge': borrowed,
                  'storage_owner_command': command,
                  'scanner_docker_command': SimpleNamespace(_OWNERS=quarantine)}
        with patch.dict(harness.sys.modules, mocked):
            for name, stage in (('pinned_image_oracle', 'oracle-acquire'), ('held_pair_launcher', 'pair-acquire')):
                d.FIRST = None
                d.stage(stage)
                module = types.ModuleType(name)
                exec(compile(HELD_OWNER_SOURCES[name], '<held-owner-model>', 'exec'), module.__dict__)
                cls = module.PinnedImageOracle if name == 'pinned_image_oracle' else module.HeldPairLauncher
                owner = object.__new__(cls)
                owner.failure = False
                owner.finished = name == 'held_pair_launcher'
                if name == 'pinned_image_oracle':
                    owner.create_attempted = True
                    module.ORACLE = owner
                    operation = owner.qualify
                else:
                    owner.bridge = SimpleNamespace(_failure=False)
                    module.OWNER = owner
                    operation = owner.start
                with self.assertRaises(ModelBridgeRefused): operation()
                self.assertTrue(owner.failure)
                self.assertIs(getattr(module, 'ORACLE' if name == 'pinned_image_oracle' else 'OWNER'), owner)
                self.assertEqual(d.FIRST[0], stage)
                self.assertEqual(d.FIRST[1], 'source-guard')
                self.assertIs(harness.sys.modules['scanner_docker_command']._OWNERS, quarantine)

    def test_first_source_guard_survives_cleanup_stage_and_private_exception(self):
        d = self.diagnostic_model()
        d.bind('a'*40, '1', 1, 'b'*64, 'pair')
        d.stage('oracle-image-metadata')
        d.guard('Immutable volume-free image differs')
        first = d.FIRST
        d.stage('oracle-release')
        d.failure(RuntimeError('PRIVATE argv PID IP payload'))
        self.assertEqual(d.FIRST, first)
        proof = d.projection()
        self.assertEqual(proof['firstStage'], 'oracle-image-metadata')
        self.assertNotIn('PRIVATE', str(proof))
        self.assertFalse(proof['cleanupAccepted'])
        self.assertFalse(proof['pairAccepted'])

    def test_diagnostic_unknown_or_unbound_values_never_become_public_fields(self):
        d = self.diagnostic_model()
        with self.assertRaises(ValueError): d.projection()
        with self.assertRaises(ValueError): d.stage('PRIVATE')
        with self.assertRaises(ValueError): d.record('PRIVATE', 'PRIVATE')
        d.bind('a'*40, '1', 1, 'b'*64, 'pair')
        d.failure(RuntimeError('PRIVATE'))
        self.assertEqual(d.projection()['firstCategory'], 'unclassified')
        self.assertEqual(d.projection()['firstDenialClause'], 'unclassified-source-clause')

    def test_diagnostic_write_failure_does_not_drop_retained_original_owners(self):
        from unittest.mock import Mock
        owner = object()
        d = self.diagnostic_model()
        d.bind('a'*40, '1', 1, 'b'*64, 'pair')
        modules = {'pair_failure_diagnostic': d, 'pinned_image_oracle': SimpleNamespace(ORACLE=owner)}
        with patch.dict(harness.sys.modules, modules), patch.object(harness.Path, 'cwd', side_effect=KeyboardInterrupt):
            harness.write_failure_diagnostic(RuntimeError('PRIVATE'))
            self.assertTrue(harness.owners_retained())
            self.assertIs(harness.sys.modules['pinned_image_oracle'].ORACLE, owner)
        self.assertIsNotNone(d.FIRST)

    def source_resource_model(self):
        import types
        module = types.ModuleType('modeled_qualified_resources')
        with patch.dict(harness.sys.modules, {module.__name__: module}):
            exec(compile(HELD_H_SOURCE, '<held-model-resource>', 'exec'), module.__dict__)
        return module

    def test_original_bridge_guard_is_distinct_from_scanner_start_and_foreign_clone(self):
        import types
        bridge=types.ModuleType('modeled_bridge')
        exec(compile(HELD_BRIDGE_SOURCE,'<held-model-bridge>','exec'),bridge.__dict__)
        d=self.diagnostic_model();d.capture_bridge_sites(bridge,HELD_BRIDGE_SOURCE)
        d.bind('a'*40,'1',1,'b'*64,'pair');d.stage('scanner-bridge-acquire')
        try:bridge.validate_image_chain({}, {}, {}, 'model', 'model', 'model')
        except bridge.BridgeRefused as error:d.failure(error)
        self.assertEqual(d.projection()['firstStage'],'scanner-bridge-acquire')
        self.assertTrue(d.projection()['firstDenialClause'].startswith('bridge-guard-'))
        other=types.ModuleType('other_bridge')
        exec(compile(HELD_BRIDGE_SOURCE,'<same-lines-other-code>','exec'),other.__dict__)
        other.BridgeRefused=bridge.BridgeRefused  # Same class/site lines, cloned foreign code.
        try:other.validate_image_chain({}, {}, {}, 'model', 'model', 'model')
        except other.BridgeRefused as error:self.assertEqual(d.owner_clause(error),'unclassified-source-clause')

    def test_original_require_code_and_callsite_emit_only_fixed_clause(self):
        resources = self.source_resource_model()
        d = self.diagnostic_model()
        d.capture_h_sites(resources, HELD_H_SOURCE)
        d.bind('a'*40, '1', 1, 'b'*64, 'pair')
        d.stage('scanner-start')
        try:
            resources.ScannerPlan('', 'a'*64, (), '', 'b'*64, {}, '').validate()
        except resources.AdmissionError as error:
            d.failure(error)
        proof=d.projection()
        expected=next(row[3] for row in d.H_SITES if row[0]=='ScannerPlan.validate' and row[4]=='require-call')
        self.assertEqual(proof['firstDenialClause'], expected)
        self.assertEqual(proof['firstCategory'], 'owner-refusal')
        self.assertFalse(proof['cleanupAccepted'])

    def test_foreign_code_same_lines_direct_unknown_and_caller_attributes_refuse(self):
        original=self.source_resource_model()
        foreign=self.source_resource_model()
        foreign.AdmissionError=original.AdmissionError  # Same class, different original code identities.
        for kind in ('foreign','nonrequire','attributes'):
            d=self.diagnostic_model();d.capture_h_sites(original, HELD_H_SOURCE)
            try:
                if kind=='foreign':
                    # Exact same text and source line numbers, different originals.
                    foreign.ScannerPlan('', 'a'*64, (), '', 'b'*64, {}, '').validate()
                else:
                    error=original.AdmissionError('PRIVATE original text must not be read')
                    error.diagnosticCode=next(row[3] for row in d.H_SITES)
                    raise error
            except (original.AdmissionError,foreign.AdmissionError) as error:
                self.assertEqual(d.owner_clause(error),'unclassified-source-clause')

    def test_capped_trace_and_wrong_source_never_acquire_an_owner_clause(self):
        resources=self.source_resource_model();d=self.diagnostic_model()
        with self.assertRaises(ValueError):d.capture_h_sites(resources,b'wrong source')
        self.assertIsNone(d.H_CAPTURE)
        d.capture_h_sites(resources,HELD_H_SOURCE)
        def wrap(n):
            if n: return wrap(n-1)
            resources.ScannerPlan('', 'a'*64, (), '', 'b'*64, {}, '').validate()
        try:wrap(40)
        except resources.AdmissionError as error:
            self.assertEqual(d.owner_clause(error),'unclassified-source-clause')
        with self.assertRaises(ValueError):d.capture_h_sites(resources,HELD_H_SOURCE)

    def test_precapture_same_line_foreign_body_and_namespace_are_refused(self):
        import types
        d=self.diagnostic_model()
        self.assertTrue(d.same_constant(slice(1,9,2),slice(1,9,2)))
        self.assertFalse(d.same_constant(slice(1,9,2),slice(1,9,3)))
        for family in ('h','bridge'):
            for fault in ('body','globals'):
                if family=='h':
                    resources=self.source_resource_model();held=HELD_H_SOURCE
                else:
                    resources=types.ModuleType('model_bridge_precapture')
                    exec(compile(HELD_BRIDGE_SOURCE,'<held-model-bridge>','exec'),resources.__dict__)
                    held=HELD_BRIDGE_SOURCE
                original=resources.require
                if fault=='body':
                    namespace={}
                    text='\n'*(original.__code__.co_firstlineno-1)+"def require(value,message):\n    if True: raise RuntimeError('PRIVATE')\n"
                    exec(compile(text,'<foreign-same-first-line>','exec'),namespace)
                    original.__code__=namespace['require'].__code__
                    self.assertEqual(original.__code__.co_firstlineno,namespace['require'].__code__.co_firstlineno)
                else:
                    resources.require=types.FunctionType(original.__code__,{'foreign':True})
                d=self.diagnostic_model()
                capture=d.capture_h_sites if family=='h' else d.capture_bridge_sites
                with self.assertRaises(ValueError):capture(resources,held)
                self.assertIsNone(d.H_CAPTURE)
                self.assertIsNone(d.B_CAPTURE)

    def relay_prefix_models(self):
        import ast,types
        original=ast.parse(ORIGINAL_RELAY_PREFIX).body[0]
        source=ast.parse(HELD_BRIDGE_SOURCE)
        klass=next(n for n in source.body if isinstance(n,ast.ClassDef) and n.name=='BorrowedScannerBridge')
        split=next(n for n in klass.body if isinstance(n,ast.FunctionDef) and n.name=='_relay')
        split.body=split.body[:8]
        models=[]
        class Refused(ValueError):pass
        def require(value,message):
            if not value:raise Refused(message)
        for function in (original,split):
            scope={'require':require}
            exec(compile(ast.fix_missing_locations(ast.Module(body=[function],type_ignores=[])),'<pure-relay-prefix>','exec'),scope)
            models.append(scope['_relay'])
        return models,Refused

    def relay_probe(self,failed=None,error=None):
        import types
        trace=[]
        class Probe:
            def __init__(self,fields):object.__setattr__(self,'fields',fields)
            def __getattribute__(self,name):
                if name=='fields':return object.__getattribute__(self,name)
                trace.append(name)
                if error==name:raise RuntimeError('PRIVATE')
                return object.__getattribute__(self,'fields')[name]
        def method(name,value):
            def result():
                trace.append(name+'()')
                if error==name+'()':raise RuntimeError('PRIVATE')
                return value
            return result
        endpoint=('127.0.0.1',7)
        listener=Probe({'fileno':method('fileno',-1 if failed==4 else 8),
                        'getsockname':method('getsockname',('127.0.0.1',8) if failed==5 else endpoint)})
        relay=Probe({'stop':Probe({'is_set':method('is_set',failed==1)}),
                     'acceptor':Probe({'is_alive':method('is_alive',failed!=2)}),
                     'failures':['PRIVATE'] if failed==3 else [],'ready_history_valid':method('ready_history_valid',failed!=3),'listener':listener,'endpoint':endpoint})
        scanner=Probe({'relay':None if failed==0 else relay,'port':8 if failed==6 else 7})
        return types.SimpleNamespace(scanner=scanner),trace

    def test_all_seven_first_failed_conjuncts_preserve_short_circuit(self):
        models,Refused=self.relay_prefix_models()
        for failed in list(range(7))+[None]:
            observations=[]
            for model in models:
                owner,trace=self.relay_probe(failed)
                try:model(owner);outcome=('success',)
                except Refused as error:outcome=(type(error),str(error))
                observations.append((outcome,trace))
            self.assertEqual(observations[0],observations[1])
            if failed is not None:self.assertIs(observations[0][0][0],Refused)

    def test_conjunct_getter_and_method_errors_preserve_order_and_type(self):
        models,_=self.relay_prefix_models()
        for fault in ('relay','stop','is_set','is_set()','acceptor','is_alive','is_alive()',
                      'ready_history_valid','ready_history_valid()','listener','fileno','fileno()','getsockname','getsockname()','endpoint','port'):
            observations=[]
            for model in models:
                owner,trace=self.relay_probe(error=fault)
                with self.assertRaises(RuntimeError) as caught:model(owner)
                observations.append((type(caught.exception),trace))
            self.assertEqual(observations[0],observations[1])

    def test_split_original_sites_emit_seven_fixed_codes_without_private_values(self):
        import types
        bridge=types.ModuleType('split_bridge_model')
        exec(compile(HELD_BRIDGE_SOURCE,'<held-split-bridge>','exec'),bridge.__dict__)
        d=self.diagnostic_model();d.capture_bridge_sites(bridge,HELD_BRIDGE_SOURCE)
        emitted=[]
        for failed in range(7):
            owner,_=self.relay_probe(failed)
            try:bridge.BorrowedScannerBridge._relay(owner)
            except bridge.BridgeRefused as error:
                emitted.append(d.owner_clause(error))
        sites=[row[3] for row in d.B_SITES if row[0]=='BorrowedScannerBridge._relay' and row[4]=='require-call'][:7]
        self.assertEqual(emitted,sites);self.assertEqual(len(set(emitted)),7)
        self.assertTrue(all(value.startswith('bridge-guard-') for value in emitted))

    def test_startup_control_source_is_held_and_tampering_refuses(self):
        manifest,data=self.manifest()
        with patch.object(harness,'bounded_read',return_value=data) as read:
            self.assertEqual(harness.hold_startup_controls(Path.cwd(),manifest),data)
            read.assert_called_once()
        with patch.object(harness,'bounded_read',return_value=b'changed'):
            with self.assertRaises(harness.Refused):harness.hold_startup_controls(Path.cwd(),manifest)
        manifest['startupControls']['path']='foreign.py'
        with patch.object(harness,'bounded_read') as read:
            with self.assertRaises(harness.Refused):harness.hold_startup_controls(Path.cwd(),manifest)
            read.assert_not_called()

    def test_invalid_metadata_refuses_before_either_actor_callback(self):
        from unittest.mock import Mock
        manifest, _ = self.manifest()
        invalid = ({}, {'GITHUB_RUN_ID': '01', 'GITHUB_RUN_ATTEMPT': '1'},
                   {'GITHUB_RUN_ID': str(2**63), 'GITHUB_RUN_ATTEMPT': '1'},
                   {'GITHUB_RUN_ID': '1' * 21, 'GITHUB_RUN_ATTEMPT': '1'},
                   {'GITHUB_RUN_ID': '1'}, {'GITHUB_RUN_ID': '1', 'GITHUB_RUN_ATTEMPT': '01'},
                   {'GITHUB_RUN_ID': '1', 'GITHUB_RUN_ATTEMPT': str(2**31)},
                   {'GITHUB_RUN_ID': '1', 'GITHUB_RUN_ATTEMPT': '0'})
        for environment in invalid:
            for operation in (harness.main, harness.raw_git_main):
                with self.subTest(environment=environment, operation=operation.__name__):
                    resources = SimpleNamespace(Context=Mock(side_effect=AssertionError('context created before canonical metadata')))
                    qualifier = SimpleNamespace(qualify_pair=Mock(side_effect=AssertionError('pair/oracle actor called')))
                    runner = SimpleNamespace(run_git_bytes=Mock(side_effect=AssertionError('Git actor called')))
                    admitted = (Path.cwd(), 'a' * 40, manifest, b'held-model', resources, qualifier, runner)
                    with patch.dict(harness.os.environ, environment, clear=True), \
                            patch.object(harness, 'admit_and_load', return_value=admitted):
                        with self.assertRaises(harness.Refused):
                            operation()
                    resources.Context.assert_not_called()
                    qualifier.qualify_pair.assert_not_called()
                    runner.run_git_bytes.assert_not_called()

    def test_acquisition_is_nonblocking_before_nonregular_type_refusal(self):
        import os
        import stat
        def acquire(path, flags):
            self.assertTrue(flags & os.O_NONBLOCK)
            return 17  # Model-only descriptor; no OS descriptor acquisition.
        # On Windows source-only preparation the Linux flags are absent. These
        # controlled constants exercise the expression without an OS call.
        with patch.object(os, 'O_NOFOLLOW', getattr(os, 'O_NOFOLLOW', 65536), create=True), \
                patch.object(os, 'O_CLOEXEC', getattr(os, 'O_CLOEXEC', 524288), create=True), \
                patch.object(os, 'O_NONBLOCK', getattr(os, 'O_NONBLOCK', 2048), create=True), \
                patch.object(harness.os, 'open', side_effect=acquire), \
                patch.object(harness.os, 'fstat', return_value=SimpleNamespace(st_mode=stat.S_IFIFO, st_size=1)), \
                patch.object(harness.os, 'read', side_effect=AssertionError('nonregular read')), \
                patch.object(harness.os, 'close') as close:
            with self.assertRaises(harness.Refused):
                harness.bounded_read(Path('model-substituted-source'), 128)
        close.assert_called_once_with(17)

    def manifest(self):
        names = ('scanner_docker_command', 'hosted_companion_resources', 'scanner_loopback_relay',
                 'hosted_scanner_readiness', 'borrowed_scanner_bridge', 'owned_storage_backend',
                 'storage_owner_command', 'held_pair_launcher', 'pinned_image_oracle', 'qualify_pair_resources', 'pair_failure_diagnostic')
        data = b'fixture_model_value = 7\n'
        return {'schemaVersion': 1, 'fileSource': 'a' * 40,
                'fileSourceRole': 'provenance-label-only-no-File-runtime',
                'oracleControls': {'path': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/test_pinned_image_oracle.py',
                                   'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)},
                'startupControls': {'path': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/test_startup_ping_handoff.py',
                                    'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)},
                'rawControls': {'path': 'tools/InvoiceCompletionProducerAcceptance/companion/test_raw_owner_command.py',
                                'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)},
                'modules': [{'name': name, 'path': 'tools/InvoiceCompletionProducerAcceptance/companion/' + name + '.py',
                             'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)} for name in names]}, data

    def test_all_bytes_held_before_any_module_execution(self):
        manifest, data = self.manifest()
        with patch.object(harness, 'bounded_read', return_value=data) as read:
            held = harness.hold_sources(Path.cwd(), manifest)
        self.assertEqual(read.call_count, 11)
        self.assertEqual(set(held), {row['name'] for row in manifest['modules']})
        # Execution receives held bytes; no second filesystem read permits a
        # changed pathname to supply another module after admission.
        loader = harness.HeldImports(held)
        module = SimpleNamespace(__name__='scanner_docker_command')
        with patch.object(harness, 'bounded_read', side_effect=AssertionError('late source read')):
            loader.exec_module(module)
        self.assertEqual(module.fixture_model_value, 7)

    def test_tamper_duplicate_unknown_and_wrong_file_authority_refuse(self):
        for kind in ('tamper', 'duplicate', 'unknown', 'runtime', 'bool-length'):
            manifest, data = self.manifest()
            if kind == 'tamper':
                manifest['modules'][9]['sha256'] = '0' * 64
            elif kind == 'duplicate':
                manifest['modules'][9] = manifest['modules'][0]
            elif kind == 'unknown':
                manifest['modules'][9]['name'] = 'unknown_module'
            elif kind == 'runtime':
                manifest['fileSourceRole'] = 'compiled-File-runtime'
            else:
                manifest['modules'][0]['length'] = True
            with patch.object(harness, 'bounded_read', return_value=data):
                with self.assertRaises(harness.Refused):
                    harness.hold_sources(Path.cwd(), manifest)

    def test_event_ancestor_fork_and_wrong_branch_refuse(self):
        head = 'a' * 40
        environment = {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted', 'RUNNER_OS': 'Linux',
                       'GITHUB_EVENT_NAME': 'pull_request', 'FINANCIAL_SOURCE_HEAD': head,
                       'GITHUB_REPOSITORY': 'owner/repository'}
        event = {'repository': {'full_name': 'owner/repository'},
                 'pull_request': {'number': 140, 'head': {'sha': head, 'repo': {'full_name': 'owner/repository'},
                                                        'ref': 'codex/scanner-shared-pair-20261008'}}}
        harness.admit_event(environment, event, head)
        for key, value in (('sha', 'b' * 40), ('repo', {'full_name': 'fork/repository'}), ('ref', 'other')):
            old = event['pull_request']['head'][key]
            event['pull_request']['head'][key] = value
            with self.assertRaises(harness.Refused):
                harness.admit_event(environment, event, head)
            event['pull_request']['head'][key] = old

    def test_retained_owner_never_voluntarily_exits_on_interrupted_wait(self):
        # Two retained observations then physically empty original registries;
        # the first controlled interrupt must not bypass the living fence.
        with patch.object(harness, 'owners_retained', side_effect=[True, True, False]), \
                patch.object(harness.time, 'sleep', side_effect=[KeyboardInterrupt(), None]) as pause:
            harness.living_failure_fence()
        self.assertEqual(pause.call_count, 2)

    def test_owner_query_fault_does_not_skip_fence(self):
        with patch.object(harness, 'owners_retained', side_effect=[RuntimeError('model fault'), True, False]), \
                patch.object(harness.time, 'sleep') as pause:
            harness.living_failure_fence()
        self.assertEqual(pause.call_count, 1)

    def test_partial_import_retains_other_original_owner(self):
        with patch.dict(harness.sys.modules, {'scanner_docker_command': SimpleNamespace(),
                                             'held_pair_launcher': SimpleNamespace(OWNER=object()),
                                             'pinned_image_oracle': SimpleNamespace()}):
            self.assertTrue(harness.owners_retained())

    def test_raw_git_requires_exact_untouched_newline_and_single_settled_row(self):
        from unittest.mock import Mock
        for output, rows_after, accepted in ((b'git version 2.43.0\n', ({},), True),
                                             (b'git version 2.43.0', ({},), False),
                                             (b'git version 2.43.0\n', (), False)):
            runner = SimpleNamespace(command_receipts=Mock(side_effect=[(), rows_after]),
                                     run_git_bytes=Mock(return_value=output))
            qualifier = SimpleNamespace(validate_original_command_ledger=Mock())
            with patch.object(harness, 'owners_retained', return_value=False):
                if accepted:
                    self.assertEqual(harness.observe_raw_git_version(runner, qualifier), hashlib.sha256(output).hexdigest())
                    qualifier.validate_original_command_ledger.assert_called_once_with(rows_after, ())
                else:
                    with self.assertRaises(harness.Refused):
                        harness.observe_raw_git_version(runner, qualifier)
            runner.run_git_bytes.assert_called_once_with(['--version'], timeout=10)



if __name__ == '__main__':
    unittest.main()


class StorageDiagnosticControls(unittest.TestCase):
    def prepare(self):
        import ast,types
        d=types.ModuleType('storage_diagnostic_model')
        exec(compile(HELD_DIAGNOSTIC_SOURCE,'<held-storage-diagnostic>','exec'),d.__dict__)
        runner=types.ModuleType('scanner_docker_command')
        exec(compile(HELD_STORAGE_SOURCES['runner'],'<held-storage-runner>','exec'),runner.__dict__)
        scanner=types.ModuleType('held_storage_scanner_model')
        # Compile only the actual actor-free method declaration, preserving its
        # qualified code, source line table and actual admitted globals mapping.
        tree=ast.parse(HELD_STORAGE_SOURCES['scanner'])
        cls=next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='Scanner')
        cls.body=[n for n in cls.body if isinstance(n,ast.FunctionDef) and n.name=='docker']
        scanner.run_docker=runner.run_docker
        exec(compile(ast.fix_missing_locations(ast.Module(body=[cls],type_ignores=[])),'<held-scanner-method-model>','exec'),scanner.__dict__)
        launcher=types.ModuleType('held_storage_launcher_model')
        borrowed=types.ModuleType('borrowed_scanner_bridge');borrowed.BridgeRefused=type('BridgeRefused',(ValueError,),{})
        borrowed.BorrowedScannerBridge=lambda *args:types.SimpleNamespace(_failure=False)
        seam=types.ModuleType('storage_owner_command');seam.storage_command=lambda *args:self.fail('unexpected storage process actor')
        with patch.dict(harness.sys.modules,{'pair_failure_diagnostic':d,'borrowed_scanner_bridge':borrowed,'storage_owner_command':seam}):
            exec(compile(HELD_STORAGE_SOURCES['launcher'],'<held-storage-launcher>','exec'),launcher.__dict__)
        return d,launcher,scanner,runner

    def original_failure(self,value=None,clean=True,capture=True,clone=None):
        import types,subprocess
        d,launcher,scanner,runner=self.prepare()
        if capture:d.capture_storage_sites(launcher,scanner,runner,HELD_STORAGE_SOURCES)
        if clone == 'runner':
            runner._run_fenced=types.FunctionType(runner._run_fenced.__code__,dict(runner.__dict__),argdefs=runner._run_fenced.__defaults__)
        elif clone == 'scanner':
            original=scanner.Scanner.docker
            scanner.Scanner.docker=types.FunctionType(original.__code__,dict(scanner.__dict__),argdefs=original.__defaults__)
            scanner.Scanner.docker.__kwdefaults__=original.__kwdefaults__
        error_line=d.STORAGE_ENGINE_LINE if value is None else value
        stdin=object();stdout=object();stderr=object();commands=[]
        def birth(args,**kwargs):
            commands.append(args[1])
            return types.SimpleNamespace(returncode=1 if args[1]=='create' else 0,stdout=stdout,stderr=stderr)
        class Lease:
            def __init__(self,timeout):
                self.original_failure=None;self.cleanup_failures=[];self.receipt={'cleanupVerified':True}
                self.output={stdout:b'',stderr:error_line.encode()}
            def bind(self):pass
            def drain(self):pass
            def cleanup(self):
                self.receipt['cleanupVerified']=clean or self.process.returncode==0
                if self.receipt['cleanupVerified']:runner._OWNERS.remove(self)
        storage=types.ModuleType('owned_storage_backend');storage.IMAGE='modeled-owned-image';storage.IMAGE_ID='modeled-id'
        owner=object.__new__(launcher.HeldPairLauncher)
        owner.finished=False;owner.failure=False;owner.bridge=None;owner.backend_create_attempted=False
        owner.context=object();owner.backend_name='modeled-owned-name';owner.scanner=scanner.Scanner()
        owner.scanner.start=lambda:None;owner.scanner.network_id='modeled-network'
        owner.scanner.validate_configured_network=lambda network:None
        owner.inspect=lambda kind,handle:({'IPAM':{'Config':[{'Subnet':'172.20.0.0/24'}]},'Containers':{}} if kind=='network' else {'Id':storage.IMAGE_ID,'RepoDigests':[storage.IMAGE],'Config':{}})
        owner.labels=lambda:{};launcher.OWNER=owner
        if clone == 'launcher':
            function=launcher.HeldPairLauncher.start.__wrapped__
            launcher.HeldPairLauncher.start=types.FunctionType(function.__code__,dict(launcher.__dict__))
        if clone == 'runner':runner._run_fenced.__globals__['_Lease']=Lease
        with patch.dict(harness.sys.modules,{'owned_storage_backend':storage}),patch.object(runner,'_Lease',Lease),patch.object(subprocess,'Popen',birth):
            try:owner.start()
            except BaseException as error:observed=error
            else:self.fail('expected actual source create refusal')
        if d.FIRST is None:d.failure(observed)
        self.assertEqual(commands,['pull','create']);self.assertTrue(owner.failure)
        return d,launcher,scanner,runner,owner,observed

    def test_original_settled_source_route_emits_only_fixed_clause(self):
        for ending in ('','\n','.','.\n'):
            d0,*_=self.prepare()
            d,launcher,scanner,runner,owner,error=self.original_failure(d0.STORAGE_ENGINE_LINE+ending)
            self.assertEqual(d.FIRST,('storage-create','cli-nonzero','docker-static-ip-requires-configured-subnet'))
            self.assertEqual(runner._OWNERS,[])
            d.bind('a'*40,'123',1,'b'*64,'pair')
            receipt=d.projection();self.assertEqual(len(receipt),14)
            self.assertFalse(receipt['pairAccepted']);self.assertFalse(receipt['cleanupAccepted'])
            self.assertFalse(receipt['fileRuntimeAccepted']);self.assertFalse(receipt['genuineEightHostFinancialAccepted'])
            text=__import__('json').dumps(receipt)
            self.assertNotIn(d.STORAGE_ENGINE_LINE,text);self.assertNotIn('modeled-owned',text)

    def test_unsupported_payload_stage_type_and_foreign_route_refuse(self):
        import subprocess
        baseline=self.prepare()[0].STORAGE_ENGINE_LINE
        for value in ('OTHER '+baseline,baseline+' OTHER',baseline+'\nSECOND',baseline[:-1],'x'*4096,'x'*5000):
            d,*rest=self.original_failure(value)
            self.assertIn(d.FIRST[2],d.STORAGE_DENIALS)
        d,*rest=self.original_failure();error=rest[-1]
        for value in (None,b'PRIVATE',True,{},4096):
            error.__dict__['stderr']=value
            self.assertIn(d.storage_clause(error),d.STORAGE_DENIALS)
        error.__dict__['stderr']=baseline
        d.stage('storage-start');self.assertEqual(d.storage_clause(error),'unclassified-source-clause')
        d.stage('storage-create')
        for foreign in (subprocess.CalledProcessError(1,['PRIVATE'],stderr=baseline),type('CalledProcessError',(Exception,),{})()):
            self.assertIn(d.storage_clause(foreign),d.STORAGE_DENIALS)
        try:raise subprocess.CalledProcessError(1,['PRIVATE'],stderr=baseline)
        except subprocess.CalledProcessError as foreign:
            self.assertIn(d.storage_clause(foreign),d.STORAGE_DENIALS)

    def test_precapture_foreign_body_and_namespace_are_refused(self):
        import types
        for family,path in (('launcher','start'),('scanner','docker'),('runner','_run_fenced')):
            for change in ('body','globals'):
                d,launcher,scanner,runner=self.prepare()
                owner={'launcher':launcher,'scanner':scanner,'runner':runner}[family]
                function=launcher.HeldPairLauncher.start.__wrapped__ if family=='launcher' else scanner.Scanner.docker if family=='scanner' else runner._run_fenced
                if change=='body':
                    namespace={};exec(compile('\n'*(function.__code__.co_firstlineno-1)+'def foreign(*a,**k):\n    return None\n','<foreign-body>','exec'),namespace)
                    function.__code__=namespace['foreign'].__code__
                else:
                    replacement=types.FunctionType(function.__code__,{})
                    if family=='launcher':launcher.HeldPairLauncher.start.__wrapped__=replacement
                    elif family=='scanner':scanner.Scanner.docker=replacement
                    else:runner._run_fenced=replacement
                with self.assertRaises(ValueError):d.capture_storage_sites(launcher,scanner,runner,HELD_STORAGE_SOURCES)
                self.assertIsNone(d.STORAGE_CAPTURE)

    def test_unsettled_original_command_retains_owner_and_never_classifies(self):
        d,launcher,scanner,runner,owner,error=self.original_failure(clean=False)
        self.assertIs(type(error),runner.DockerLifecycleError)
        self.assertNotEqual(d.FIRST[2],d.STORAGE_CLAUSE)
        self.assertTrue(owner.failure);self.assertTrue(runner._OWNERS)
        self.assertIn(d.storage_clause(error),d.STORAGE_DENIALS)

    def test_source_tamper_duplicate_capture_and_postcapture_substitution_refuse(self):
        d,launcher,scanner,runner=self.prepare()
        wrong=dict(HELD_STORAGE_SOURCES);wrong['runner']+=b'\n'
        with self.assertRaises(ValueError):d.capture_storage_sites(launcher,scanner,runner,wrong)
        self.assertIsNone(d.STORAGE_CAPTURE)
        d.capture_storage_sites(launcher,scanner,runner,HELD_STORAGE_SOURCES)
        with self.assertRaises(ValueError):d.capture_storage_sites(launcher,scanner,runner,HELD_STORAGE_SOURCES)
        # Same original error type/text at a foreign source frame is not an
        # original settled command, even after valid source capture.
        import subprocess
        d.stage('storage-create')
        namespace={'subprocess':subprocess,'line':d.STORAGE_ENGINE_LINE}
        exec(compile('def foreign():\n    raise subprocess.CalledProcessError(1,[],stderr=line)\n','<foreign-after-capture>','exec'),namespace)
        try:namespace['foreign']()
        except subprocess.CalledProcessError as error:
            self.assertIn(d.storage_clause(error),d.STORAGE_DENIALS)

    def test_postcapture_same_code_foreign_globals_never_gains_clause(self):
        for family in ('launcher','scanner','runner'):
            d,launcher,scanner,runner,owner,error=self.original_failure(clone=family)
            self.assertIn(d.FIRST[2],d.STORAGE_DENIALS)
            self.assertIn(d.storage_clause(error),d.STORAGE_DENIALS)
            self.assertTrue(owner.failure);self.assertEqual(runner._OWNERS,[])

    def test_fixed_gate_codes_distinguish_private_predicates_in_order(self):
        import copy,subprocess,types,sys
        d,launcher,scanner,runner,owner,error=self.original_failure()
        self.assertEqual(d.storage_clause(error),d.STORAGE_CLAUSE)
        capture=d.STORAGE_CAPTURE
        d.STORAGE_CAPTURE=None
        self.assertEqual(d.storage_clause(error),'storage-denial-source-not-captured')
        d.STORAGE_CAPTURE=capture
        self.assertEqual(d.storage_clause(ValueError('PRIVATE')),'storage-denial-error-type')
        foreign=subprocess.CalledProcessError(1,[],stderr=d.STORAGE_ENGINE_LINE)
        self.assertEqual(d.storage_clause(foreign),'storage-denial-trace-short')
        original_trace=error.__traceback__
        for _ in range(40):error.__traceback__=types.TracebackType(error.__traceback__,sys._getframe(),0,0)
        self.assertEqual(d.storage_clause(error),'storage-denial-trace-limit')
        error.__traceback__=original_trace
        codes=dict(capture[1]);codes['HeldPairLauncher.start']=(lambda:None).__code__
        d.STORAGE_CAPTURE=(capture[0],tuple(codes.items()),capture[2])
        self.assertEqual(d.storage_clause(error),'storage-denial-frame-code')
        d.STORAGE_CAPTURE=capture
        namespaces=dict(capture[2]);namespaces['HeldPairLauncher.start']={}
        d.STORAGE_CAPTURE=(capture[0],capture[1],tuple(namespaces.items()))
        self.assertEqual(d.storage_clause(error),'storage-denial-frame-globals')
        d.STORAGE_CAPTURE=capture
        sites=dict(d.STORAGE_SITES);d.STORAGE_SITES=dict(sites,create=(-1,-1))
        self.assertEqual(d.storage_clause(error),'storage-denial-frame-line')
        d.STORAGE_SITES=sites
        for value,expected in ((None,'stderr-type'),(b'PRIVATE','stderr-type'),('', 'stderr-bound'),('x'*4096,'stderr-bound'),('PRIVATE other error','stderr-grammar')):
            error.__dict__['stderr']=value
            self.assertEqual(d.storage_clause(error),'storage-denial-'+expected)

    def test_gate_projection_keeps_fixed_fourteen_false_scope_and_first_failure(self):
        d,*rest=self.original_failure('PRIVATE other error');error=rest[-1]
        self.assertEqual(d.FIRST,('storage-create','cli-nonzero','storage-denial-stderr-grammar'))
        d.bind('a'*40,'123',1,'b'*64,'pair');before=d.projection()
        d.failure(ValueError('PRIVATE second error'))
        self.assertEqual(before,d.projection());self.assertEqual(len(before),14)
        encoded=__import__('json').dumps(before)
        self.assertNotIn('PRIVATE',encoded);self.assertNotIn('stderr',encoded.replace('storage-denial-stderr-grammar',''))
        for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):
            self.assertIs(before[key],False)
        for code in d.STORAGE_DENIALS:
            d.FIRST=None;d.record('cli-nonzero',code)
            self.assertEqual(d.projection()['firstDenialClause'],code)
        with self.assertRaises(ValueError):d.record('cli-nonzero','storage-denial-PRIVATE')

    def test_complete_source_referenced_wrapper_has_separate_fixed_category(self):
        expected='Error response from daemon: invalid endpoint settings:\nuser specified IP address is supported only when connecting to networks with user configured subnets'
        original=self.prepare()[0]
        self.assertEqual(original.STORAGE_WRAPPED_LINE,expected)
        for ending in ('','\n'):
            d,*rest=self.original_failure(expected+ending)
            self.assertEqual(d.FIRST,('storage-create','cli-nonzero','docker-wrapped-static-ip-requires-configured-subnet'))
            d.bind('a'*40,'123',1,'b'*64,'pair');receipt=d.projection()
            self.assertEqual(len(receipt),14)
            for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):
                self.assertIs(receipt[key],False)
            self.assertNotIn(expected,__import__('json').dumps(receipt))

    def test_wrapper_is_not_prefix_stripping_or_embedded_multicause_matching(self):
        reference=self.prepare()[0].STORAGE_WRAPPED_LINE
        variants=(reference+'.',reference+'.\n',reference.replace('\n','\r\n'),reference+'\r\n',
                  reference+'\n\n',reference+'\nSECOND', 'PRIVATE '+reference,reference+' PRIVATE',
                  reference.replace('Error response from daemon: ',''),reference.replace('settings:','settings :'),
                  reference.replace('\n',' '),reference.replace('invalid endpoint settings:','invalid endpoint settings: invalid endpoint settings:'),
                  reference[:-1],reference+'\x00')
        for value in variants:
            d,*rest=self.original_failure(value)
            self.assertEqual(d.FIRST,('storage-create','cli-nonzero','storage-denial-stderr-grammar'))
        # Existing source/owner refusal precedes matching even for exact newtext.
        d,launcher,scanner,runner,owner,error=self.original_failure(reference,clean=False)
        self.assertIs(type(error),runner.DockerLifecycleError)
        self.assertNotEqual(d.FIRST[2],d.STORAGE_WRAPPED_CLAUSE)
        for family in ('launcher','scanner','runner'):
            d,*rest=self.original_failure(reference,clone=family)
            self.assertEqual(d.FIRST[2],'storage-denial-frame-globals')

    def test_complete_create_wrapper_is_format_only_private_candidate(self):
        prefix='Error response from daemon: invalid config for network '
        suffix=': invalid endpoint settings:\nuser specified IP address is supported only when connecting to networks with user configured subnets'
        for network in ('a'*64,'0123456789abcdef'*4):
            for ending in ('','\n'):
                message=prefix+network+suffix+ending
                d,*rest=self.original_failure(message)
                self.assertEqual(d.FIRST,('storage-create','cli-nonzero','docker-create-wrapped-static-ip-requires-configured-subnet'))
                self.assertEqual(__import__('re').compile(d.STORAGE_CREATE_WRAPPED_PATTERN).groups,0)
                d.bind('b'*40,'123',1,'c'*64,'pair');receipt=d.projection()
                self.assertEqual(len(receipt),14)
                for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):
                    self.assertIs(receipt[key],False)
                self.assertNotIn(network,__import__('json').dumps(receipt))
                self.assertNotIn(message,__import__('json').dumps(receipt))

    def test_create_wrapper_rejects_identifier_injection_and_foreign_route(self):
        prefix='Error response from daemon: invalid config for network '
        suffix=': invalid endpoint settings:\nuser specified IP address is supported only when connecting to networks with user configured subnets'
        reference=prefix+'a'*64+suffix
        ids=('a'*63,'a'*65,'A'*64,'g'*64,'private-network','a'*31+'\n'+'a'*32,'a'*31+':'+'a'*32,'a'*32+' PRIVATE '+'a'*32)
        variants=tuple(prefix+network+suffix for network in ids)+(reference+'.',reference+'\n\n',reference+'\r\n',reference.replace('\n','\r\n'),reference+'\nSECOND','PRIVATE '+reference,reference+' PRIVATE',reference+'\x00',reference.replace('settings:','settings :'),reference[:-1])
        for value in variants:
            d,*rest=self.original_failure(value)
            self.assertEqual(d.FIRST[2],'storage-denial-stderr-grammar')
        for family in ('launcher','scanner','runner'):
            d,*rest=self.original_failure(reference,clone=family)
            self.assertEqual(d.FIRST[2],'storage-denial-frame-globals')
        d,launcher,scanner,runner,owner,error=self.original_failure(reference,clean=False)
        self.assertIs(type(error),runner.DockerLifecycleError)
        self.assertNotEqual(d.FIRST[2],d.STORAGE_CREATE_WRAPPED_CLAUSE)

class NetworkSiteDiagnosticControls(unittest.TestCase):
    def model(self,capture=True):
        import types,sys
        d=types.ModuleType('network_diagnostic_model')
        exec(compile(HELD_DIAGNOSTIC_SOURCE,'<held-network-diagnostic>','exec'),d.__dict__)
        m=types.ModuleType('network_scanner_model')
        relay=types.ModuleType('scanner_loopback_relay');relay.LoopbackRelay=object;relay.StartupPingRefused=type('StartupPingRefused',(OSError,),{})
        runner=types.ModuleType('scanner_docker_command');runner.run_docker=lambda *a,**k:self.fail('unexpected actor')
        with patch.dict(sys.modules,{'scanner_loopback_relay':relay,'scanner_docker_command':runner}):
            exec(compile(HELD_STORAGE_SOURCES['scanner'],'<held-network-scanner>','exec'),m.__dict__)
        if capture:d.capture_network_sites(m,HELD_STORAGE_SOURCES['scanner'])
        d.stage('scanner-start');return d,m

    def test_original_nested_direct_and_owned_validator_raise_are_fixed_private_sites(self):
        for case in ('nested-observed','nested-listed','census-ipam','owned'):
            d,m=self.model()
            def docker(*args,**kw):
                if case=='nested-observed':return object()
                if case=='nested-listed':return 'PRIVATE INVALID'
                if args[1]=='ls':return 'a'*64
                return '[{"Id":"'+('a'*64)+'","Driver":"bridge","IPAM":{"Config":null}}]'
            try:
                if case=='owned':m.Scanner('modeled',configured_network=True).validate_configured_network(None)
                else:m.configured_network_census(docker)
            except ValueError as error:d.failure(error)
            else:self.fail('expected original guard')
            self.assertTrue(d.FIRST[2].startswith('scanner-network-guard-'))
            d.bind('b'*40,'123',1,'c'*64,'pair');p=d.projection();self.assertEqual(len(p),14)
            for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):self.assertIs(p[key],False)
            self.assertNotIn('PRIVATE',__import__('json').dumps(p));self.assertNotIn('a'*64,__import__('json').dumps(p))

    def test_original_literal_type_stage_trace_and_unknown_library_remain_refused(self):
        import json
        d,m=self.model()
        try:m.configured_network_census(lambda *args,**kw:object())
        except ValueError as error:original=error
        self.assertNotEqual(d.network_clause(original),'unclassified-source-clause')
        old=original.args;original.args=('PRIVATE substitution',)
        self.assertEqual(d.network_clause(original),'unclassified-source-clause');original.args=old
        original.args=(str.__new__(type('ForeignString',(str,),{}),old[0]),)
        self.assertEqual(d.network_clause(original),'unclassified-source-clause');original.args=old
        wrong=TimeoutError(*old);wrong.__traceback__=original.__traceback__
        self.assertEqual(d.network_clause(wrong),'unclassified-source-clause')
        d.stage('storage-create');self.assertEqual(d.network_clause(original),'unclassified-source-clause')
        d.stage('scanner-start');d.NETWORK_CAPTURE=None;self.assertEqual(d.network_clause(original),'unclassified-source-clause')
        d,m=self.model()
        try:m.configured_network_census(lambda *args,**kw:'a'*64 if args[1]=='ls' else 'INVALID JSON')
        except json.JSONDecodeError as error:self.assertEqual(d.network_clause(error),'unclassified-source-clause')
        else:self.fail('expected decoder refusal')

    def test_precapture_source_body_namespace_and_wrong_bytes_refuse_without_actor(self):
        import types
        for family in ('census-body','census-namespace','validator-body','validator-namespace'):
            d,m=self.model(capture=False)
            owner=m if family.startswith('census') else m.Scanner
            key='configured_network_census' if family.startswith('census') else 'validate_configured_network'
            function=getattr(owner,key)
            if family.endswith('namespace'):
                setattr(owner,key,types.FunctionType(function.__code__,dict(m.__dict__),argdefs=function.__defaults__))
            else:
                ns={};exec(compile('\n'*(function.__code__.co_firstlineno-1)+'def foreign(*args,**kwargs):\n    raise ValueError("PRIVATE")\n','<foreign>','exec'),ns)
                function.__code__=ns['foreign'].__code__
            with self.assertRaises(ValueError):d.capture_network_sites(m,HELD_STORAGE_SOURCES['scanner'])
            self.assertIsNone(d.NETWORK_CAPTURE)
        d,m=self.model(capture=False)
        with self.assertRaises(ValueError):d.capture_network_sites(m,b'wrong source')
        self.assertIsNone(d.NETWORK_CAPTURE)

    def test_postcapture_same_code_foreign_globals_and_bounded_trace_fail_closed(self):
        import types
        d,m=self.model()
        m.configured_network_census=types.FunctionType(m.configured_network_census.__code__,dict(m.__dict__))
        try:m.configured_network_census(lambda *a,**k:object())
        except ValueError as error:self.assertEqual(d.network_clause(error),'unclassified-source-clause')
        else:self.fail('expected source guard')
        d,m=self.model()
        original=m.Scanner.validate_configured_network
        m.Scanner.validate_configured_network=types.FunctionType(original.__code__,dict(m.__dict__))
        m.Scanner.validate_configured_network.__kwdefaults__=original.__kwdefaults__
        try:m.Scanner('modeled',configured_network=True).validate_configured_network(None)
        except ValueError as error:self.assertEqual(d.network_clause(error),'unclassified-source-clause')
        else:self.fail('expected source guard')
        d,m=self.model()
        def recurse(depth):
            if depth:return recurse(depth-1)
            return m.configured_network_census(lambda *a,**k:object())
        try:recurse(40)
        except ValueError as error:self.assertEqual(d.network_clause(error),'unclassified-source-clause')
        else:self.fail('expected source guard')

    def test_literal_exact_length_precedes_equality_and_overlong_argument_refuses(self):
        import ast
        tree=ast.parse(HELD_DIAGNOSTIC_SOURCE)
        fn=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='network_clause')
        test=next(n.test for n in ast.walk(fn) if isinstance(n,ast.If) and isinstance(n.test,ast.BoolOp) and any(isinstance(v,ast.Compare) and isinstance(v.left,ast.Call) and isinstance(v.left.func,ast.Name) and v.left.func.id=='len' and isinstance(v.left.args[0],ast.Subscript) for v in n.test.values))
        rendered=[ast.unparse(n) for n in test.values]
        self.assertEqual(rendered[-3:],['type(arguments[0]) is not str','len(arguments[0]) != len(literal)','arguments[0] != literal'])
        d,m=self.model()
        try:m.configured_network_census(lambda *args,**kw:object())
        except ValueError as error:original=error
        capture=d.NETWORK_CAPTURE;trace=original.__traceback__
        original.args=('PRIVATE overlong argument'*1000,)
        self.assertEqual(d.network_clause(original),'unclassified-source-clause')
        self.assertIs(d.NETWORK_CAPTURE,capture);self.assertIs(original.__traceback__,trace)
        d.failure(original);d.bind('a'*40,'123',1,'b'*64,'pair')
        self.assertNotIn('PRIVATE',__import__('json').dumps(d.projection()))

    def test_original_ipam_type_missing_null_nonlist_oversize_have_distinct_fixed_sites(self):
        import json
        clauses=[]
        for value in (None,{}, {'Config':None},{'Config':{}},{'Config':[{}]*9}):
            d,m=self.model();calls=[]
            def docker(*args,**kw):
                calls.append(args)
                if args[1]=='ls':return 'a'*64
                return json.dumps([{'Id':'a'*64,'Driver':'host','IPAM':value}])
            try:m.configured_network_census(docker)
            except ValueError as error:
                self.assertEqual(error.args,('Configured network census IPAM shape differs',))
                d.failure(error)
            else:self.fail('unchanged source refusal required')
            self.assertEqual([args[1] for args in calls],['ls','inspect'])
            self.assertTrue(d.FIRST[2].startswith('scanner-network-guard-'));clauses.append(d.FIRST[2])
            d.bind('b'*40,'123',1,'c'*64,'pair');p=d.projection();self.assertEqual(len(p),14)
            for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):self.assertIs(p[key],False)
            self.assertNotIn('a'*64,__import__('json').dumps(p))
        self.assertEqual(len(set(clauses)),5)


class StorageObserverSiteControls(unittest.TestCase):
    def model(self):
        import types,sys
        d=types.ModuleType('observer_diagnostic_model')
        exec(compile(HELD_DIAGNOSTIC_SOURCE,'<held-observer-diagnostic>','exec'),d.__dict__)
        s=types.ModuleType('owned_storage_backend')
        b=types.ModuleType('borrowed_scanner_bridge')
        c=types.ModuleType('storage_owner_command')
        loader=harness.HeldImports({'owned_storage_backend':HELD_OBSERVER_SOURCES['storage'],
            'borrowed_scanner_bridge':HELD_BRIDGE_SOURCE,'storage_owner_command':HELD_OBSERVER_SOURCES['command']})
        with patch.dict(sys.modules,{s.__name__:s,b.__name__:b,c.__name__:c}):
            loader.exec_module(b);loader.exec_module(s);loader.exec_module(c)
        d.MODEL_ORIGINAL_CLASSES=loader._observer_class_births
        d.capture_observer_sites(s,c,b,HELD_OBSERVER_SOURCES,loader._observer_class_births)
        d.stage('storage-observe')
        return d,s,c,b

    def observe_refusal(self,s,c):
        import sys
        with patch.dict(sys.modules,{'storage_owner_command':c}):
            return s.observe(None,None,command_runner=object())

    def error(self,operation):
        try:operation()
        except BaseException as error:return error
        self.fail('source guard did not refuse')

    def test_original_storage_first_guards_and_four_command_sites_have_fixed_codes(self):
        import sys,types
        d,s,c,b=self.model()
        operations=(lambda:s.instant('PRIVATE'),lambda:s.Lease(*(['PRIVATE']*16)).validate(None),
                    lambda:self.observe_refusal(s,c))
        observed=[]
        for operation in operations:
            clause=d.observer_clause(self.error(operation));self.assertTrue(clause.startswith('storage-observer-guard-'));observed.append(clause)
        self.assertEqual(len(set(observed)),3)
        runner=types.ModuleType('scanner_docker_command');runner.run_docker=lambda *a,**k:'xx'
        with patch.dict(sys.modules,{'scanner_docker_command':runner}):
            for args,maximum in ((None,1),(['docker',1],1),(['docker','rm','a'*64],1),(['docker','exec','a'*64,'cat','/proc/1/stat'],1)):
                observed.append(d.observer_clause(self.error(lambda:c.storage_command(args,maximum))))
        self.assertEqual(len(set(observed)),7)

    def test_nested_original_code_matches_only_original_namespace(self):
        import types
        d,s,c,b=self.model();code=dict(d.OBSERVER_CAPTURE[2])[('storage','observe.refresh')]
        values={'handle':SimpleNamespace(refresh=lambda:{'networkId':'b'}),'lease':SimpleNamespace(network_id='a')}
        def cell(value):return (lambda:value).__closure__[0]
        closure=tuple(cell(values[name]) for name in code.co_freevars)
        original=types.FunctionType(code,s.__dict__,closure=closure)
        foreign=types.FunctionType(code,dict(s.__dict__),closure=closure)
        self.assertTrue(d.observer_clause(self.error(original)).startswith('storage-observer-guard-'))
        self.assertEqual(d.observer_clause(self.error(foreign)),'unclassified-source-clause')

    def test_postcapture_foreign_globals_code_class_stage_and_overtrace_refuse(self):
        import types
        d,s,c,b=self.model();original=s.require
        s.require=types.FunctionType(original.__code__,dict(s.__dict__))
        self.assertEqual(d.observer_clause(self.error(lambda:s.instant('PRIVATE'))),'unclassified-source-clause')
        s.require=original
        foreign=types.FunctionType(s.instant.__code__,dict(s.__dict__))
        self.assertEqual(d.observer_clause(self.error(lambda:foreign('PRIVATE'))),'unclassified-source-clause')
        original_class=s.AdmissionError
        s.AdmissionError=type('AdmissionError',(ValueError,),{})
        self.assertEqual(d.observer_clause(self.error(lambda:s.instant('PRIVATE'))),'unclassified-source-clause')
        s.AdmissionError=original_class
        error=self.error(lambda:s.instant('PRIVATE'));d.stage('storage-create')
        self.assertEqual(d.observer_clause(error),'unclassified-source-clause');d.stage('storage-observe')
        def recurse(depth):
            if depth:recurse(depth-1)
            else:s.instant('PRIVATE')
        self.assertEqual(d.observer_clause(self.error(lambda:recurse(40))),'unclassified-source-clause')

    def test_precapture_substitution_nested_parent_and_class_route_refuse(self):
        import types
        for kind in ('source','function','nested-parent','command-class'):
            d,s,c,b=self.model();d.OBSERVER_CAPTURE=None;raw=dict(HELD_OBSERVER_SOURCES)
            if kind=='source':raw['storage']+=b'\n'
            elif kind=='function':s.instant=types.FunctionType(s.instant.__code__,dict(s.__dict__))
            elif kind=='nested-parent':s.observe.__code__=(lambda:None).__code__
            else:c.BridgeRefused=type('BridgeRefused',(ValueError,),{})
            with self.assertRaises(ValueError):d.capture_observer_sites(s,c,b,raw,d.MODEL_ORIGINAL_CLASSES)
            self.assertIsNone(d.OBSERVER_CAPTURE)

    def test_fixed_projection_never_reads_exception_text_or_admits_success(self):
        d,s,c,b=self.model()
        s.AdmissionError.__str__=lambda self:(_ for _ in ()).throw(AssertionError('text read'))
        error=self.error(lambda:s.instant('PRIVATE'))
        error.diagnosticCode='PRIVATE';d.failure(error)
        d.bind('a'*40,'123',1,'b'*64,'pair')
        proof=d.projection();self.assertEqual(len(proof),14)
        self.assertEqual(proof['firstCategory'],'owner-refusal')
        self.assertTrue(proof['firstDenialClause'].startswith('storage-observer-guard-'))
        self.assertTrue(all(proof[key] is False for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted')))
        self.assertNotIn('PRIVATE',repr(proof))

    def test_actual_loader_original_class_birth_rejects_precapture_label_clones(self):
        for kind in ('storage','joint-bridge-command','class-record-shape','module-binding'):
            d,s,c,b=self.model();d.OBSERVER_CAPTURE=None;original=dict(d.MODEL_ORIGINAL_CLASSES)
            if kind=='storage':s.AdmissionError=type('AdmissionError',(ValueError,),{'__module__':s.__name__})
            elif kind=='joint-bridge-command':
                b.BridgeRefused=type('BridgeRefused',(ValueError,),{'__module__':b.__name__});c.BridgeRefused=b.BridgeRefused
            elif kind=='class-record-shape':original['owned_storage_backend']=(s,)
            else:original['owned_storage_backend']=(object(),s.AdmissionError)
            with self.assertRaises(ValueError):d.capture_observer_sites(s,c,b,HELD_OBSERVER_SOURCES,original)
            self.assertIsNone(d.OBSERVER_CAPTURE)


class StorageListenerCardinalityControls(unittest.TestCase):
    ORIGINAL = "    require(len(inodes) == 1, 'One exact backend listener required')\n"
    SPLIT = "    if len(inodes) == 0:\n        require(False, 'One exact backend listener required')\n    else:\n        require(len(inodes) == 1, 'One exact backend listener required')\n"

    def test_original_source_cardinality_partition_preserves_outcomes_and_one_require(self):
        import ast,types,sys
        source=HELD_OBSERVER_SOURCES['storage'];source=source.replace(ZERO_INSTRUMENTED.encode(),ZERO_ORIGINAL.encode());self.assertEqual(source.count(self.SPLIT.encode()),1)
        storage=types.ModuleType('cardinality_original_storage')
        with patch.dict(sys.modules,{storage.__name__:storage}):exec(compile(source,'<held-source>','exec'),storage.__dict__)
        functions=[]
        for guard in (self.ORIGINAL,self.SPLIT):
            scope={'require':storage.require};exec(compile('def check(inodes):\n'+guard,'<source-extracted-cardinality>','exec'),scope);functions.append(scope['check'])
        for count in range(101):
            results=[]
            for function in functions:
                try:result=('accepted',function(['123']*count))
                except BaseException as error:result=(type(error),error.args)
                results.append(result)
            self.assertEqual(results[0],results[1])
        for guard in (self.ORIGINAL,self.SPLIT):
            for count in (0,1,2,100):
                calls=[]
                def require(value,message):
                    calls.append((value,message))
                    if not value:raise storage.AdmissionError(message)
                scope={'require':require};exec(compile('def check(inodes):\n'+guard,'<counted-source-guard>','exec'),scope)
                try:scope['check'](['123']*count)
                except storage.AdmissionError:pass
                self.assertEqual(calls,[(count==1,'One exact backend listener required')])

    def test_two_fixed_sites_bind_exact_zero_vs_nonzero_refusal_and_same_literal(self):
        import ast,types
        tree=ast.parse(HELD_OBSERVER_SOURCES['storage']);fn=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='observe')
        branch=next(n for n in fn.body if isinstance(n,ast.If) and ast.unparse(n.test)=='len(inodes) == 0')
        self.assertEqual(len(branch.body),1);self.assertEqual(len(branch.orelse),1)
        zero,nonzero=branch.body[0].body[0].value,branch.orelse[0].value
        self.assertEqual(ast.unparse(zero.args[0]),'False');self.assertEqual(ast.unparse(nonzero.args[0]),'len(inodes) == 1')
        self.assertEqual(zero.args[1].value,nonzero.args[1].value)
        self.assertEqual(zero.args[1].value,'One exact backend listener required')
        d=types.ModuleType('cardinality_diagnostic');exec(compile(HELD_DIAGNOSTIC_SOURCE,'<held-diag>','exec'),d.__dict__)
        sites=[row for row in d.OBSERVER_SITES if row[0]=='storage' and row[1]=='observe' and row[2] in (zero.lineno,nonzero.lineno)]
        self.assertEqual(len(sites),2);self.assertEqual(len({row[4] for row in sites}),2)
        self.assertTrue(all(row[5]=='require-call' for row in sites))

    def test_entire_observer_source_restores_by_only_cardinality_guard_recomposition(self):
        import ast
        current=HELD_OBSERVER_SOURCES['storage'].replace(ZERO_INSTRUMENTED.encode(),ZERO_ORIGINAL.encode());original=current.replace(self.SPLIT.encode(),self.ORIGINAL.encode())
        old=ast.parse(original);new=ast.parse(current)
        oldfn=next(n for n in old.body if isinstance(n,ast.FunctionDef) and n.name=='observe')
        newfn=next(n for n in new.body if isinstance(n,ast.FunctionDef) and n.name=='observe')
        oldcalls=[ast.unparse(n) for n in ast.walk(oldfn) if isinstance(n,ast.Call) and isinstance(n.func,ast.Name) and n.func.id in ('execute','snapshots','ticks','refresh')]
        newcalls=[ast.unparse(n) for n in ast.walk(newfn) if isinstance(n,ast.Call) and isinstance(n.func,ast.Name) and n.func.id in ('execute','snapshots','ticks','refresh')]
        self.assertEqual(oldcalls,newcalls)
        self.assertIn(b'/proc/1/net/tcp6',current)
        self.assertEqual(len(current)-len(original),len(self.SPLIT.encode())-len(self.ORIGINAL.encode()))

ZERO_ORIGINAL = "    if len(inodes) == 0:\n        require(False, 'One exact backend listener required')\n"
ZERO_INSTRUMENTED = "    if len(inodes) == 0:\n        try:\n            require(False, 'One exact backend listener required')\n        except AdmissionError as zero_error:\n            import pair_failure_diagnostic as diagnostic\n            diagnostic.failure(zero_error)\n            _zero_listener_evidence(lease, storage_command, zero_error)\n            raise\n"

class ZeroListenerEvidenceControls(unittest.TestCase):
    def source_model(self):
        return StorageObserverSiteControls().model()

    def fixture(self, s):
        from datetime import datetime, timezone, timedelta
        now=datetime.now(timezone.utc)
        stamp=lambda v:v.isoformat().replace('+00:00','Z')
        lease=s.Lease('a'*64,'b'*64,'c'*64,'123','1','d'*40,
            'c821-11111111-2222-3333-4444-555555555555',stamp(now-timedelta(seconds=60)),
            stamp(now+timedelta(seconds=60)),stamp(now-timedelta(seconds=50)),
            stamp(now-timedelta(seconds=40)),stamp(now-timedelta(seconds=50)),
            '172.19.0.2',4443,'e'*64,4567)
        host=dict(NetworkMode=lease.network_id,ReadonlyRootfs=True,Privileged=False,CapDrop=['ALL'],CapAdd=None,
            SecurityOpt=['no-new-privileges:true'],PidMode='',IpcMode='private',CgroupnsMode='private',
            RestartPolicy={'Name':'no'},AutoRemove=False,Memory=128*1024**2,NanoCpus=500000000,
            Binds=None,Devices=None,Tmpfs=None,PortBindings=None)
        container=dict(Id=lease.container_id,Created=lease.created,Image=s.IMAGE_ID,RestartCount=0,
            State=dict(Running=True,Paused=False,Restarting=False,StartedAt=lease.started,Pid=12),
            Config=dict(Image=s.IMAGE,Volumes=None,Entrypoint=['/bin/fake-gcs-server'],Cmd=lease.arguments(),
                Env=['PATH=/bin'],Labels=lease.labels('storage')),HostConfig=host,Mounts=[],
            NetworkSettings=dict(Ports={'4443/tcp':None},Networks={'exact':dict(NetworkID=lease.network_id,IPAddress=lease.bridge_ip)}))
        image=dict(Id=s.IMAGE_ID,RepoDigests=[s.IMAGE],Config=dict(Env=['PATH=/bin'],Volumes=None))
        network=dict(Id=lease.network_id,Created=lease.network_created,Internal=True,Driver='bridge',EnableIPv6=False,
            Scope='local',Labels=lease.labels(),Containers={lease.container_id:{},lease.scanner_id:{}},IPAM={'Config':[{'Subnet':'172.19.0.0/24'}]})
        scanner=dict(Id=lease.scanner_id,Created=lease.created,State=dict(Running=True,Paused=False,Restarting=False,StartedAt=lease.started,Pid=13),
            Config=dict(Labels=lease.labels('scanner')),NetworkSettings=dict(Networks={'exact':dict(NetworkID=lease.network_id)}))
        return lease,container,image,network,scanner

    def evidence(self, s, case='single', fault_at=None, audit=None, mutation=None):
        import json,copy
        lease,container,image,network,scanner=self.fixture(s);calls=[] if audit is None else audit;reads={}
        def runner(args, maximum, *, absolute_deadline):
            calls.append((tuple(args),maximum,absolute_deadline))
            if len(calls)==fault_at:raise s.AdmissionError('MODELED PRIVATE FAILURE')
            if args[1:3] in (['container','inspect'],['network','inspect'],['image','inspect']):
                obj={lease.container_id:container,lease.scanner_id:scanner,lease.network_id:network,s.IMAGE_ID:image}[args[3]]
                obj=copy.deepcopy(obj)
                if mutation is not None:mutation(len(calls),args,obj)
                if case=='changed-pid' and len(calls)>10 and args[3]==lease.container_id:obj['State']['Pid']=14
                if case=='foreign-census':network['Containers']['f'*64]={}
                return json.dumps([obj]).encode()
            tail=tuple(args[3:]);reads[tail]=reads.get(tail,0)+1
            if tail==('cat','/proc/1/stat'):
                tick=4568 if case=='initial-tick' or (case=='changed-tick' and reads[tail]>1) else lease.kernel_ticks
                return ('1 (server) S '+' '.join(['0']*18+[str(tick)])).encode()
            if tail==('ls','-l','/proc/1/fd'):
                inode='999' if case=='unowned' or (case=='changed-fd' and reads[tail]>1) else '777'
                value=f'lrwx------ 1 root root 64 Oct 8 12:00 3 -> socket:[{inode}]\n'
                if case=='malformed-fd':value='PRIVATE socket:[777]\n'
                return value.encode()
            if tail[0]=='cat' and tail[1] in ('/proc/1/net/tcp','/proc/1/net/tcp6'):
                v6=tail[1].endswith('tcp6');width=32 if v6 else 8
                header='sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode\n'
                if case=='malformed-table' and v6:return b'PRIVATE INVALID\n'
                count=0 if (not v6 and case!='later-ipv4') or case=='absent' else (2 if case=='multiple' and v6 else 1)
                row=f'0: {"0"*width}:{lease.port:04X} {"0"*width}:0000 0A 0 0 0 0 0 777\n'
                if case=='wrong-port':row=row.replace(f':{lease.port:04X}',':0001')
                if case=='wrong-state':row=row.replace(' 0A ',' 01 ')
                return (header+row*count).encode()
            self.fail('Unexpected modeled command')
        return s._zero_listener_evidence(lease,runner),calls

    def test_tcp6_evidence_never_admits_and_uses_one_deadline_thirteen_commands(self):
        d,s,c,b=self.source_model();clause,calls=self.evidence(s)
        self.assertEqual(clause,'zero-ipv4-tcp6-single-init-owned-stable')
        self.assertEqual(len(calls),13);self.assertEqual(len({row[2] for row in calls}),1)
        self.assertTrue(all(row[1]<=262144 for row in calls))
        self.assertIsNone(d.FIRST);self.assertIsNone(d.LISTENER_EVIDENCE)

    def test_absence_multiple_wrong_binding_and_fd_generation_have_fixed_refusals(self):
        d,s,c,b=self.source_model()
        for case,expected in [('absent','no-selected-tcp6-listener'),('multiple','multiple-tcp6-listeners'),
            ('wrong-port','no-selected-tcp6-listener'),('wrong-state','no-selected-tcp6-listener'),
            ('unowned','tcp6-fd-unowned'),('changed-fd','tcp6-fd-unowned'),('changed-pid','generation-changed'),
            ('changed-tick','generation-changed'),('later-ipv4','later-ipv4-present')]:
            with self.subTest(case=case):self.assertEqual(self.evidence(s,case)[0],'zero-ipv4-'+expected)

    def test_parser_and_original_census_failures_stop_without_evidence_registration(self):
        d,s,c,b=self.source_model()
        for case in ('malformed-table','malformed-fd','foreign-census'):
            with self.subTest(case=case),self.assertRaises(s.AdmissionError):self.evidence(s,case)
        self.assertIsNone(d.LISTENER_EVIDENCE)

    def test_each_original_command_failure_propagates_without_subsequent_commands(self):
        d,s,c,b=self.source_model()
        for index in range(1,14):
            audit=[]
            with self.subTest(index=index),self.assertRaises(s.AdmissionError):self.evidence(s,fault_at=index,audit=audit)
            self.assertEqual(len(audit),index)
        self.assertIsNone(d.LISTENER_EVIDENCE)

    def test_deadline_type_expiration_and_late_success_refuse_before_or_after_original_runner(self):
        import types,sys,time
        d,s,c,b=self.source_model();births=[]
        runner=types.ModuleType('scanner_docker_command')
        runner.run_docker=lambda args,timeout:(births.append(timeout) or 'observed')
        args=['docker','exec','a'*64,'cat','/proc/1/net/tcp6']
        with patch.dict(sys.modules,{'scanner_docker_command':runner}):
            for value in (True,float('nan'),float('inf'),-float('inf'),10**400,0,-1):
                with self.subTest(value=type(value).__name__),self.assertRaises(b.BridgeRefused):c.storage_command(args,absolute_deadline=value)
            self.assertEqual(births,[])
            with patch.object(c.time,'monotonic',side_effect=[10.0,10.5]):
                self.assertEqual(c.storage_command(args,absolute_deadline=11.0),b'observed')
            self.assertEqual(births,[1.0])
            with patch.object(c.time,'monotonic',side_effect=[10.0,11.0]),self.assertRaises(b.BridgeRefused):c.storage_command(args,absolute_deadline=11.0)
            self.assertEqual(births,[1.0,1.0])
            c.storage_command(['docker','exec','a'*64,'cat','/proc/1/net/tcp'])
            self.assertEqual(births[-1],10)

    def test_private_registration_refuses_caller_minted_metadata_and_keeps_fourteen_false_flags(self):
        d,s,c,b=self.source_model();error=StorageObserverSiteControls().error(lambda:s.instant('PRIVATE'))
        d.failure(error);first=d.FIRST
        with self.assertRaises(ValueError):d.listener_evidence(error,'zero-ipv4-tcp6-single-init-owned-stable')
        self.assertEqual(d.FIRST,first);self.assertIsNone(d.LISTENER_EVIDENCE)
        d.bind('a'*40,'123',1,'b'*64,'pair');proof=d.projection();self.assertEqual(len(proof),14)
        for key in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted'):self.assertIs(proof[key],False)

    def test_each_returned_object_uncertainty_stops_before_next_cli(self):
        d,s,c,b=self.source_model()
        cases=[(1,['Id'],'f'*64),(1,['RepoDigests'],[]),(1,['Config'],[]),
            (2,['Id'],'f'*64),(2,['Created'],'UNKNOWN'),(2,['State','Pid'],0),
            (2,['State','Running'],False),(2,['HostConfig','CapDrop'],[]),
            (2,['HostConfig','Memory'],0),(2,['Config'],[]),
            (3,['Id'],'f'*64),(3,['Created'],'UNKNOWN'),(3,['Internal'],False),
            (3,['Containers'],{}),(3,['IPAM'],[]),
            (4,['Id'],'f'*64),(4,['State','Running'],False),(4,['State','Pid'],0),
            (4,['Config','Labels'],{})]
        for expected,path,value in cases:
            audit=[]
            def mutate(index,args,obj):
                if index!=expected:return
                target=obj
                for key in path[:-1]:target=target[key]
                target[path[-1]]=value
            with self.subTest(command=expected,path=path),self.assertRaises(s.AdmissionError):self.evidence(s,audit=audit,mutation=mutate)
            self.assertEqual(len(audit),expected)
        audit=[]
        def change_pid(index,args,obj):
            if index==11:obj['State']['Pid']=14
        self.assertEqual(self.evidence(s,audit=audit,mutation=change_pid)[0],'zero-ipv4-generation-changed')
        self.assertEqual(len(audit),11)
        audit=[]
        def change_ipam(index,args,obj):
            if index==12:obj['IPAM']={'Config':[{'Subnet':'172.20.0.0/24'}]}
        self.assertEqual(self.evidence(s,audit=audit,mutation=change_ipam)[0],'zero-ipv4-generation-changed')
        self.assertEqual(len(audit),12)

    def test_utc_mapping_latency_consumes_budget_and_late_early_return_refuses(self):
        import time
        from datetime import datetime
        d,s,c,b=self.source_model();clock=[100.0];samples=[]
        class DelayedUtc(datetime):
            @classmethod
            def now(cls,tz=None):
                clock[0]=121.0
                return datetime.now(tz)
        def mono():samples.append(clock[0]);return clock[0]
        with patch.object(s,'datetime',DelayedUtc),patch.object(time,'monotonic',side_effect=mono):
            clause,calls=self.evidence(s)
        self.assertEqual(clause,'zero-ipv4-tcp6-single-init-owned-stable');self.assertEqual(samples[0],100.0)
        self.assertTrue(all(row[2]==130.0 for row in calls))
        audit=[]
        with patch.object(time,'monotonic',side_effect=[100.0,131.0]),self.assertRaises(s.AdmissionError):self.evidence(s,case='initial-tick',audit=audit)
        self.assertEqual(len(audit),5)

    def full_observe(self, case='single', fault_at=None, helper_substitution=None):
        import copy,json,sys,types
        d,s,c,b=self.source_model();lease,container,image,network,scanner=self.fixture(s)
        if helper_substitution=='return':s._zero_listener_evidence=lambda *args:'zero-ipv4-tcp6-single-init-owned-stable'
        if helper_substitution=='foreign-globals':s._zero_listener_evidence=types.FunctionType(s._zero_listener_evidence.__code__,dict(s.__dict__),argdefs=s._zero_listener_evidence.__defaults__)
        calls=[];seen_zero=[];quarantine=[];extra_reads=0
        handle=SimpleNamespace(network_id=lease.network_id,scanner_container_id=lease.scanner_id,
            created_utc=lease.network_created,ownership_labels={'financial.acceptance.run':lease.lease_id[5:]},
            context=SimpleNamespace(run_id=lease.run_id,attempt=1,file_sha=lease.file_sha,lease_id=lease.lease_id,
                expires_utc=lease.expires,issued_utc=lease.issued))
        handle.refresh=lambda:dict(networkId=lease.network_id,createdUtc=lease.network_created,scannerContainerId=lease.scanner_id,
            internal=True,driver='bridge',ownershipLabels=handle.ownership_labels,members=sorted([lease.container_id,lease.scanner_id]))
        runner=types.ModuleType('scanner_docker_command')
        retained=object();failure=b.BridgeRefused('PRIVATE subordinate owner uncertainty')
        def run(args,timeout):
            nonlocal extra_reads
            calls.append((tuple(args),timeout))
            if d.FIRST is not None:
                seen_zero.append(d.FIRST);extra_reads+=1
                if extra_reads==fault_at:
                    quarantine.append(retained)
                    raise failure
            if args[:2] in (['container','inspect'],['network','inspect'],['image','inspect']):
                return json.dumps([{lease.container_id:container,lease.scanner_id:scanner,lease.network_id:network,s.IMAGE_ID:image}[args[2]]])
            tail=tuple(args[2:])
            if tail==('cat','/proc/1/stat'):return '1 (server) S '+' '.join(['0']*18+[str(lease.kernel_ticks)])
            if tail==('readlink','/proc/1/exe'):return '/bin/fake-gcs-server'
            if tail==('sha256sum','/proc/1/exe'):return lease.executable_sha+'  /proc/1/exe'
            if tail==('cat','/proc/1/cmdline'):return '\0'.join(['/bin/fake-gcs-server',*lease.arguments()])+'\0'
            if tail==('ls','-l','/proc/1/fd'):return 'lrwx------ 1 root root 64 Oct 8 12:00 3 -> socket:[777]\n'
            if tail==('cat','/proc/1/net/tcp'):return 'sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode\n'
            if tail==('cat','/proc/1/net/tcp6'):
                if case=='malformed':return 'PRIVATE bad table'
                return f'sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode\n0: {"0"*32}:{lease.port:04X} {"0"*32}:0000 0A 0 0 0 0 0 777\n'
            self.fail('Unexpected full-observe modeled command')
        runner.run_docker=run
        with patch.dict(sys.modules,{'scanner_docker_command':runner,'storage_owner_command':c,'pair_failure_diagnostic':d}),patch.object(s.sys,'platform','linux'),patch.dict(s.os.environ,{'RUNNER_ENVIRONMENT':'github-hosted','GITHUB_RUN_ID':lease.run_id,'GITHUB_RUN_ATTEMPT':lease.attempt}):
            try:s.observe(lease,handle,command_runner=c.storage_command)
            except BaseException as error:observed=error
            else:self.fail('Original IPv4 admission incorrectly passed')
        return d,s,c,b,observed,seen_zero,quarantine,retained,failure,extra_reads

    def test_actual_observe_records_original_zero_first_before_all_subordinate_reads(self):
        d,s,c,b,error,seen,quarantine,retained,failure,count=self.full_observe()
        self.assertIs(type(error),s.AdmissionError);self.assertEqual(error.args,('One exact backend listener required',))
        self.assertEqual(count,13);self.assertTrue(all(row==d.FIRST for row in seen))
        self.assertEqual(d.FIRST,('storage-observe','owner-refusal',d.LISTENER_ZERO_CLAUSE))
        self.assertIs(d.LISTENER_EVIDENCE[0],error);self.assertEqual(quarantine,[])
        d.bind('a'*40,'123',1,'b'*64,'pair');proof=d.projection()
        self.assertEqual(proof['firstDenialClause'],'zero-ipv4-tcp6-single-init-owned-stable')
        self.assertEqual(len(proof),14);self.assertTrue(all(proof[k] is False for k in ('pairAccepted','cleanupAccepted','fileRuntimeAccepted','genuineEightHostFinancialAccepted')))

    def test_actual_subordinate_failure_identity_custody_and_first_refusal_survive_without_next_birth(self):
        for index in range(1,14):
            with self.subTest(index=index):
                d,s,c,b,error,seen,quarantine,retained,failure,count=self.full_observe(fault_at=index)
                self.assertIs(error,failure);self.assertEqual(count,index);self.assertEqual(quarantine,[retained])
                self.assertTrue(all(row==d.FIRST for row in seen));self.assertIsNone(d.LISTENER_EVIDENCE)
                d.failure(error);self.assertEqual(d.FIRST,('storage-observe','owner-refusal',d.LISTENER_ZERO_CLAUSE))
                d.bind('a'*40,'123',1,'b'*64,'pair');self.assertEqual(d.projection()['firstDenialClause'],d.LISTENER_ZERO_CLAUSE)

    def test_actual_malformed_observation_stops_and_preserves_original_first_without_minting_evidence(self):
        d,s,c,b,error,seen,quarantine,retained,failure,count=self.full_observe(case='malformed')
        self.assertIs(type(error),s.AdmissionError);self.assertEqual(count,8)
        self.assertIsNone(d.LISTENER_EVIDENCE);d.failure(error)
        self.assertEqual(d.FIRST,('storage-observe','owner-refusal',d.LISTENER_ZERO_CLAUSE))

    def test_postcapture_helper_returns_cannot_mint_and_foreign_helper_globals_cannot_register(self):
        for kind in ('return','foreign-globals'):
            with self.subTest(kind=kind):
                d,s,c,b,error,seen,quarantine,retained,failure,count=self.full_observe(helper_substitution=kind)
                self.assertIsNone(d.LISTENER_EVIDENCE)
                if kind=='return':self.assertEqual(count,0)
                else:self.assertEqual(count,13);self.assertIs(type(error),ValueError)
                d.bind('a'*40,'123',1,'b'*64,'pair')
                self.assertEqual(d.projection()['firstDenialClause'],d.LISTENER_ZERO_CLAUSE)

class OwnedIpv4BindingControls(unittest.TestCase):
    source_model = ZeroListenerEvidenceControls.source_model
    def fixture(self, s):
        from dataclasses import replace
        lease,container,image,network,scanner=ZeroListenerEvidenceControls.fixture(self,s)
        lease=replace(lease,bind_owned_ipv4=True)
        container['Config']['Cmd']=lease.arguments()
        return lease,container,image,network,scanner

    def normal(self, case='owned', legacy=False):
        import copy,json,sys,types
        d,s,c,b=self.source_model();lease,container,image,network,scanner=self.fixture(s);calls=[];ticks=0;backend_reads=0
        if legacy:
            from dataclasses import replace
            lease=replace(lease,bind_owned_ipv4=False);container['Config']['Cmd']=lease.arguments()
        handle=SimpleNamespace(network_id=lease.network_id,scanner_container_id=lease.scanner_id,
            created_utc=lease.network_created,ownership_labels={'financial.acceptance.run':lease.lease_id[5:]},
            context=SimpleNamespace(run_id=lease.run_id,attempt=1,file_sha=lease.file_sha,lease_id=lease.lease_id,
                expires_utc=lease.expires,issued_utc=lease.issued))
        handle.refresh=lambda:dict(networkId=lease.network_id,createdUtc=lease.network_created,scannerContainerId=lease.scanner_id,
            internal=True,driver='bridge',ownershipLabels=handle.ownership_labels,members=sorted([lease.container_id,lease.scanner_id]))
        runner=types.ModuleType('scanner_docker_command')
        def run(args,timeout):
            nonlocal ticks,backend_reads
            calls.append(tuple(args))
            if args[:2] in (['container','inspect'],['network','inspect'],['image','inspect']):
                obj=copy.deepcopy({lease.container_id:container,lease.scanner_id:scanner,lease.network_id:network,s.IMAGE_ID:image}[args[2]])
                if args[2]==lease.container_id:
                    backend_reads+=1
                    if case=='changed-engine-pid' and backend_reads>1:obj['State']['Pid']=14
                    if case=='wildcard-argv':obj['Config']['Cmd'][3]='0.0.0.0'
                return json.dumps([obj])
            tail=tuple(args[2:])
            if tail==('cat','/proc/1/stat'):
                ticks+=1;value=4568 if case=='wrong-pid-generation' or (case=='changed-ticks' and ticks>1) else lease.kernel_ticks
                return '1 (server) S '+' '.join(['0']*18+[str(value)])
            if tail==('readlink','/proc/1/exe'):return '/bin/fake-gcs-server'
            if tail==('sha256sum','/proc/1/exe'):return lease.executable_sha+'  /proc/1/exe'
            if tail==('cat','/proc/1/cmdline'):return '\0'.join(['/bin/fake-gcs-server',*lease.arguments()])+'\0'
            if tail==('ls','-l','/proc/1/fd'):
                inode='888' if case=='unowned-fd' else '777'
                return f'lrwx------ 1 root root 64 Oct 8 12:00 3 -> socket:[{inode}]\n'
            if tail[0]=='cat' and tail[1] in ('/proc/1/net/tcp','/proc/1/net/tcp6'):
                header='sl local_address rem_address st tx_queue rx_queue tr tm->when retrnsmt uid timeout inode\n'
                if tail[1].endswith('tcp6'):return header
                address=s.ipaddress.IPv4Address(lease.bridge_ip).packed[::-1].hex().upper() if lease.bind_owned_ipv4 else '00000000'
                if case=='wildcard':address='00000000'
                if case=='foreign-ip':address=s.ipaddress.IPv4Address('172.19.0.3').packed[::-1].hex().upper()
                if case=='loopback':address='0100007F'
                if case=='wrong-byte-order':address=s.ipaddress.IPv4Address(lease.bridge_ip).packed.hex().upper()
                port=lease.port+1 if case=='wrong-port' else lease.port
                state='01' if case=='not-listening' else '0A'
                row=f'0: {address}:{port:04X} 00000000:0000 {state} 0 0 0 0 0 777\n'
                return header+row*(2 if case=='two-listeners' else 1)
            self.fail('Unexpected modeled source command')
        runner.run_docker=run
        with patch.dict(sys.modules,{'scanner_docker_command':runner,'storage_owner_command':c,'pair_failure_diagnostic':d}),patch.object(s.sys,'platform','linux'),patch.dict(s.os.environ,{'RUNNER_ENVIRONMENT':'github-hosted','GITHUB_RUN_ID':lease.run_id,'GITHUB_RUN_ATTEMPT':lease.attempt}):
            try:value=s.observe(lease,handle,command_runner=c.storage_command)
            except BaseException as error:return d,s,lease,calls,error,None
        return d,s,lease,calls,None,value

    def test_actual_source_observer_accepts_only_exact_owned_ipv4_model_without_diagnostics(self):
        d,s,lease,calls,error,value=self.normal()
        self.assertIsNone(error);self.assertIsNone(d.FIRST);self.assertIsNone(d.LISTENER_EVIDENCE)
        self.assertIs(value['privateBackendObserved'],True);self.assertIs(value['genuineEightHostFinancialAccepted'],False)
        self.assertFalse(any('/proc/1/net/tcp6' in args for args in calls))

    def test_wildcard_foreign_loopback_byteorder_port_state_and_two_listeners_refuse(self):
        for case in ('wildcard','foreign-ip','loopback','wrong-byte-order','wrong-port','not-listening','two-listeners'):
            with self.subTest(case=case):
                d,s,lease,calls,error,value=self.normal(case)
                self.assertIs(type(error),s.AdmissionError);self.assertIsNone(value)

    def test_original_init_fd_kernel_ticks_engine_pid_and_argv_fences_still_refuse(self):
        for case in ('unowned-fd','wrong-pid-generation','changed-ticks','changed-engine-pid','wildcard-argv'):
            with self.subTest(case=case):
                d,s,lease,calls,error,value=self.normal(case)
                self.assertIs(type(error),s.AdmissionError);self.assertIsNone(value)

    def test_launcher_and_lease_arguments_bind_same_owned_address_and_keep_public_urls(self):
        import ast
        d,s,c,b=self.source_model();lease,*_=self.fixture(s)
        tree=ast.parse(HELD_OWNER_SOURCES['held_pair_launcher'])
        cls=next(n for n in tree.body if isinstance(n,ast.ClassDef) and n.name=='HeldPairLauncher')
        fn=next(n for n in cls.body if isinstance(n,ast.FunctionDef) and n.name=='start')
        expression=next(n.value for n in ast.walk(fn) if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='arguments' for t in n.targets))
        actual=eval(compile(ast.Expression(expression),'<actual-args-source>','eval'),{'address':lease.bridge_ip,'port':lease.port})
        self.assertEqual(actual,lease.arguments());self.assertEqual(actual[3],lease.bridge_ip)
        self.assertEqual(actual[-4:],['-external-url',f'http://{lease.bridge_ip}:{lease.port}','-public-host',f'{lease.bridge_ip}:{lease.port}'])
        self.assertNotIn('0.0.0.0',actual)

    def test_primary_and_supplementary_tcp4_filters_use_same_linux_little_endian_lease_ip(self):
        import ast
        tree=ast.parse(HELD_OBSERVER_SOURCES['storage'])
        normal=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='observe')
        predicate=next(n for n in ast.walk(normal) if isinstance(n,ast.If) and isinstance(n.test,ast.BoolOp) and ast.unparse(n.test).startswith('fields[1] == (ipaddress.IPv4Address'))
        expression=predicate.test.values[0].comparators[0].left
        helper=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='_zero_listener_evidence')
        secondary=next(n.value.body for n in ast.walk(helper) if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id=='expected_address' for t in n.targets))
        d,s,c,b=self.source_model();lease,*_=self.fixture(s)
        self.assertEqual(eval(compile(ast.Expression(expression),'<actual-address-source>','eval'),{'ipaddress':s.ipaddress,'lease':lease}),'020013AC')

    def test_default_legacy_observer_and_arguments_remain_wildcard(self):
        d,s,lease,calls,error,value=self.normal(legacy=True)
        self.assertIsNone(error);self.assertFalse(lease.bind_owned_ipv4)
        self.assertEqual(lease.arguments()[3],'0.0.0.0')
        self.assertIs(value['privateBackendObserved'],True)
        self.assertIsNone(d.FIRST)

    def test_non_boolean_binding_modes_refuse_and_launcher_explicitly_opts_in(self):
        import ast
        from dataclasses import replace
        d,s,c,b=self.source_model();lease,*_=self.fixture(s)
        for flag in (0,1,None,'true',[],{}):
            with self.subTest(flag=flag):
                with self.assertRaises(s.AdmissionError):replace(lease,bind_owned_ipv4=flag).validate(s.datetime.now(s.timezone.utc))
        tree=ast.parse(HELD_OWNER_SOURCES['held_pair_launcher'])
        calls=[n for n in ast.walk(tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr=='Lease']
        self.assertEqual(len(calls),1)
        kw=next(k for k in calls[0].keywords if k.arg=='bind_owned_ipv4')
        self.assertIs(kw.value.value,True)
