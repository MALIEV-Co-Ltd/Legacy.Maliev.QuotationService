"""Source/model controls only. No process, pidfd, descriptor or provider birth."""
import subprocess
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import scanner_docker_command as owner
import hosted_companion_resources as h

QUALIFIER_CONTROL_SOURCE_SHA = 'dcf8f20c020a69ba73a8d5ecd1acb9b15c180d37a69b1fb5f2c57db8076e999b'
HELD_QUALIFIER_SOURCE = None  # Supplied only by immutable source loader, never an observation receipt.


class RawOwnerControls(unittest.TestCase):
    def setUp(self):
        self.platform_model = patch.object(h.sys, 'platform', 'linux')
        self.platform_model.start()
        self.addCleanup(self.platform_model.stop)
        self.saved_history = list(owner._HISTORY)
        self.saved_owners = list(owner._OWNERS)
        owner._HISTORY.clear()
        owner._OWNERS.clear()

    def tearDown(self):
        owner._HISTORY[:] = self.saved_history
        owner._OWNERS[:] = self.saved_owners

    def execute_model(self, payload, raw, executable='docker'):
        out, err = object(), object()
        process = SimpleNamespace(stdout=out, stderr=err, returncode=0)
        class LeaseModel:
            def __init__(self, timeout):
                self.receipt = {'cleanupVerified': False}
                self.original_failure = None
                self.output = {out: bytearray(payload), err: bytearray()}
            def bind(self):
                pass
            def drain(self):
                pass
            def cleanup(self):
                self.receipt['cleanupVerified'] = True
                owner._OWNERS.remove(self)
        with patch.object(owner, '_Lease', LeaseModel), patch.object(owner.subprocess, 'Popen', return_value=process) as birth:
            value = owner._run_fenced(['synthetic-fixed-argument'], 10, False, executable=executable, raw_stdout=raw)
        self.assertEqual(birth.call_args.args[0], [executable, 'synthetic-fixed-argument'])
        return value

    def test_raw_preserves_newline_nul_and_non_utf8(self):
        for payload in (b'Foreground yes\n', b'/bin/exe\0arg\0', b'\xff\x80\n'):
            with self.subTest(payload=payload):
                self.assertEqual(self.execute_model(payload, True), payload)

    def test_original_trimmed_text_contract_unchanged(self):
        self.assertEqual(self.execute_model(b'  original text\n', False), 'original text')

    def test_git_uses_same_modelled_lifetime_not_another_supervisor(self):
        self.assertEqual(self.execute_model(b'synthetic-commit\n', True, 'git'), b'synthetic-commit\n')

    def test_delegate_keeps_raw_configuration_bytes(self):
        with patch.object(owner, 'run_docker_bytes', return_value=b'Foreground yes\n') as raw:
            self.assertEqual(h.command(['docker', 'container', 'exec', 'a'*64, 'cat', '/etc/clamav/acceptance.conf']),
                             b'Foreground yes\n')
        self.assertEqual(raw.call_args.args[0][0:2], ['container', 'exec'])

    def test_git_delegate_and_exact_source_output(self):
        with patch.object(owner, 'run_git_bytes', return_value=b'abc\n'):
            self.assertEqual(h.command(['git', '-C', '/synthetic', 'rev-parse', 'HEAD']), b'abc\n')

    def test_nonzero_maps_but_lifecycle_quarantine_is_not_swallowed(self):
        with patch.object(owner, 'run_docker_bytes', side_effect=subprocess.CalledProcessError(1, ['docker'])):
            with self.assertRaises(h.AdmissionError):
                h.command(['docker', 'container', 'exec', 'a'*64, 'cat', '/proc/99/stat'])
        with patch.object(owner, 'run_docker_bytes', side_effect=owner.DockerLifecycleError('quarantine')):
            with self.assertRaises(owner.DockerLifecycleError):
                h.command(['docker', 'container', 'exec', 'a'*64, 'cat', '/proc/99/stat'])

    def test_original_per_call_cap_still_rejects(self):
        with patch.object(owner, 'run_docker_bytes', return_value=b'x'*65):
            with self.assertRaises(h.AdmissionError):
                h.command(['docker', 'inspect', 'a'*64], maximum=64)


if __name__ == '__main__':
    unittest.main()


class DiscoveryAssociationControls(unittest.TestCase):
    CID = 'a' * 64

    def setUp(self):
        self.saved = [list(x) for x in (owner._HISTORY, h._FAILED_DISCOVERY_QUERIES, h._PROC_INVENTORIES, h._HANDLED_DISCOVERY_QUERIES)]
        for x in (owner._HISTORY, h._FAILED_DISCOVERY_QUERIES, h._PROC_INVENTORIES, h._HANDLED_DISCOVERY_QUERIES):
            x.clear()
        self.platform = patch.object(h.sys, 'platform', 'linux')
        self.platform.start()

    def tearDown(self):
        self.platform.stop()
        for x, saved in zip((owner._HISTORY, h._FAILED_DISCOVERY_QUERIES, h._PROC_INVENTORIES, h._HANDLED_DISCOVERY_QUERIES), self.saved):
            x[:] = saved

    @staticmethod
    def row(failed):
        row = {key: True for key in ('returnedProcessObserved', 'generationBound', 'originalReaped', 'bothReadersEof', 'bothReadersClosed', 'selectorCloseCompleted', 'pidfdCloseCompleted', 'cleanupVerified')}
        row.update(originalFailure=failed, originalExitCode=1 if failed else 0, quarantined=False, attemptLedgerCapped=False, descendantCleanupProved=False, kernelCapsObserved=False, cleanupAttempts=(('wait', 'completed'),))
        return row

    def acquisition_model(self, tail=('cat', '/proc/42/stat'), payload=b'1\nself\n'):
        def execute(args, timeout):
            failed = tuple(args[3:]) != ('ls', '-1', '/proc')
            owner._HISTORY.append(self.row(failed))
            if failed:
                raise subprocess.CalledProcessError(1, ['docker', *args])
            return payload
        with patch.object(owner, 'run_docker_bytes', execute):
            try:
                h.command(['docker', 'container', 'exec', self.CID, *tail])
            except h.AdmissionError as error:
                caught = error
            observed = h.command(['docker', 'container', 'exec', self.CID, 'ls', '-1', '/proc'])
        return caught, observed

    def test_actual_dispatch_identity_then_absence_keeps_failed_row(self):
        error, observed = self.acquisition_model()
        h._record_handled_discovery(error, self.CID, '42', False, observed)
        self.assertEqual(h.handled_discovery_queries(), ((0, 1, 'vanished-nonowner-read-query'),))
        self.assertTrue(owner.command_receipts()[0]['originalFailure'])
        self.assertEqual(owner.command_receipts()[0]['originalExitCode'], 1)

    def test_unhandled_and_mutating_queries_emit_no_association(self):
        for tail in (('rm', '/proc/42/stat'), ('cat', '/proc/42/cmdline')):
            error, observed = self.acquisition_model(tail)
            h._record_handled_discovery(error, self.CID, '42', False, observed)
        self.assertEqual(h.handled_discovery_queries(), ())

    def test_substituted_exception_cannot_classify(self):
        error, observed = self.acquisition_model()
        h._record_handled_discovery(h.AdmissionError(str(error)), self.CID, '42', False, observed)
        self.assertEqual(h.handled_discovery_queries(), ())

    def test_substituted_bytes_matched_container_pid_and_present_pid_refuse(self):
        error, observed = self.acquisition_model()
        cases = ((error, self.CID, '42', False, bytes(bytearray(observed))),
                 (error, 'b'*64, '42', False, observed), (error, self.CID, '43', False, observed),
                 (error, self.CID, '42', True, observed))
        for args in cases:
            with self.assertRaises(h.AdmissionError):
                h._record_handled_discovery(*args)
        error, observed = self.acquisition_model(payload=b'1\n42\n')
        with self.assertRaises(h.AdmissionError):
            h._record_handled_discovery(error, self.CID, '42', False, observed)

    def test_unsettled_or_substituted_index_argv_and_duplicate_refuse(self):
        error, observed = self.acquisition_model()
        original = h._FAILED_DISCOVERY_QUERIES[0]
        for replacement in ((error, 1, original[2]), (error, 0, ('docker', 'container', 'stop', self.CID))):
            h._FAILED_DISCOVERY_QUERIES[0] = replacement
            with self.assertRaises(h.AdmissionError):
                h._record_handled_discovery(error, self.CID, '42', False, observed)
        h._FAILED_DISCOVERY_QUERIES[0] = original
        owner._HISTORY[0]['quarantined'] = True
        with self.assertRaises(h.AdmissionError):
            h._record_handled_discovery(error, self.CID, '42', False, observed)
        owner._HISTORY[0]['quarantined'] = False
        h._record_handled_discovery(error, self.CID, '42', False, observed)
        with self.assertRaises(h.AdmissionError):
            h._record_handled_discovery(error, self.CID, '42', False, observed)

    def test_qualifier_rejects_unassociated_nonzero_wrong_category_and_index(self):
        import ast
        from pathlib import Path
        source = HELD_QUALIFIER_SOURCE
        if source is None:
            # Ordinary CI source-only discovery; native held execution always
            # supplies pre-admitted bytes and never takes this pathname lane.
            import hashlib
            source = (Path(__file__).parent / 'scanner-shared-bridge/qualify_pair_resources.py').read_bytes()
            self.assertLessEqual(len(source), 65536)
            self.assertEqual(hashlib.sha256(source).hexdigest(), QUALIFIER_CONTROL_SOURCE_SHA)
        self.assertIs(type(source), bytes)
        tree = ast.parse(source)
        functions = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in ('require', 'validate_original_command_ledger')]
        namespace = {'BridgeRefused': h.AdmissionError, 'diagnostic': SimpleNamespace(guard=lambda message: None)}
        exec(compile(ast.Module(body=functions, type_ignores=[]), '<held-pure-qualifier>', 'exec'), namespace)
        validate = namespace['validate_original_command_ledger']
        rows = (self.row(True), self.row(False))
        validate(rows, ((0, 1, 'vanished-nonowner-read-query'),))
        for associations in ((), ((0, 1, 'mutation'),), ((0, 0, 'vanished-nonowner-read-query'),), ((0, 1, 'vanished-nonowner-read-query'), (0, 1, 'vanished-nonowner-read-query'))):
            with self.assertRaises(h.AdmissionError):
                validate(rows, associations)
