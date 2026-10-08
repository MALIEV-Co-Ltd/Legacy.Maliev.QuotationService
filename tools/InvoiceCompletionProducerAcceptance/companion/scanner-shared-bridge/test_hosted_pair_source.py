"""Pure held-source and failure-fence controls; no actors or descriptor probes."""
import hashlib
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import qualify_hosted_pair as harness


class HostedPairSourceControls(unittest.TestCase):
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
                 'storage_owner_command', 'held_pair_launcher', 'pinned_image_oracle', 'qualify_pair_resources')
        data = b'fixture_model_value = 7\n'
        return {'schemaVersion': 1, 'fileSource': 'a' * 40,
                'fileSourceRole': 'provenance-label-only-no-File-runtime',
                'rawControls': {'path': 'tools/InvoiceCompletionProducerAcceptance/companion/test_raw_owner_command.py',
                                'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)},
                'modules': [{'name': name, 'path': 'tools/InvoiceCompletionProducerAcceptance/companion/' + name + '.py',
                             'sha256': hashlib.sha256(data).hexdigest(), 'length': len(data)} for name in names]}, data

    def test_all_bytes_held_before_any_module_execution(self):
        manifest, data = self.manifest()
        with patch.object(harness, 'bounded_read', return_value=data) as read:
            held = harness.hold_sources(Path.cwd(), manifest)
        self.assertEqual(read.call_count, 10)
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
