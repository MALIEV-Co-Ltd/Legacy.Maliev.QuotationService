"""Pure synthetic receipt/schema controls. No scanner, subprocess, socket, FD or SDK."""
import copy
import json
import io
import stat
from contextlib import ExitStack
from types import SimpleNamespace
from unittest.mock import patch
import check_scanner_runtime_receipt as reader
import unittest

from check_scanner_runtime_receipt import validate_bytes, bounded_read, ReceiptRefused, MAXIMUM, PHASES


SYNTHETIC = {'schemaVersion': 1,
 'runId': 'synthetic-control',
 'imageReference': 'clamav/clamav@sha256:7659dcb0db47941d3cf8336af84bbb63c7e70b76fc00601774358412b42ed186',
 'genuineEightHostFinancialAccepted': False,
 'resources': [{'kind': 'container',
                'name': 'financial-scanner-synthetic-control',
                'owned': True,
                'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'}],
 'scannerReady': True,
 'cleanupVerified': True,
 'hostedIdentity': {'runId': '123',
                    'attempt': '1',
                    'head': 'ffffffffffffffffffffffffffffffffffffffff',
                    'eventSha': '1111111111111111111111111111111111111111'},
 'stage': 'actual-scanner-controls-complete',
 'imageId': 'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
 'derivedImage': {'owned': True,
                  'runLabel': 'synthetic-control',
                  'dockerfileSha256': 'cceeef30943eef9772a5d45be9d2a13348c55ba9331d4c63f151c203f3d4e352',
                  'imageId': 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                  'baseImageId': 'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                  'buildCompleted': True,
                  'cleanupVerified': True},
 'network': {'name': 'financial-scanner-synthetic-control-network',
             'owned': True,
             'allocationIssuedUtc': '2026-10-08T00:44:17.042856+00:00',
             'networkId': 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
             'cleanupVerified': True},
 'allocationIssuedUtc': '2026-10-08T00:44:17.076958+00:00',
 'startupDiagnostic': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                       'observedUtc': '2026-10-08T00:44:17.281008+00:00',
                       'state': {'Status': 'running',
                                 'Running': True,
                                 'Paused': False,
                                 'Restarting': False,
                                 'OOMKilled': False,
                                 'Dead': False,
                                 'Pid': 43,
                                 'ExitCode': 0,
                                 'Error': '',
                                 'StartedAt': '2026-10-08T00:44:17.126210996Z',
                                 'FinishedAt': '0001-01-01T00:00:00Z'},
                       'createdUtc': '2026-10-08T00:44:17.092625333Z',
                       'declaredPortBindings': {},
                       'actualPortBindings': {},
                       'networkMode': 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
                       'networkIds': {'financial-scanner-synthetic-control-network': 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'},
                       'observed': True,
                       'startupLogTail': '',
                       'startupLogTailSha256': 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
                       'startupLogTailTruncated': False,
                       'runtimeBoundaryObserved': True},
 'containerGeneration': {'createdUtc': '2026-10-08T00:44:17.092625333Z',
                         'startedUtc': '2026-10-08T00:44:17.126210996Z'},
 'portIsolation': {'declaredPortBindings': {},
                   'publishAllPorts': False,
                   'actualPortBindings': {},
                   'dockerPortPublicationObserved': False,
                   'absenceVerified': True},
 'loopbackRelay': {'host': '127.0.0.1',
                   'port': 12345,
                   'ownerPid': 42,
                   'listenerFd': 3,
                   'maximumConnectionSeconds': 10,
                   'maximumPayloadBytes': 209715200,
                   'maximumConcurrentConnections': 4,
                   'listenerSocket': 'socket:[999]',
                   'ownerKernelStartTicks': 100,
                   'backend': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                               'networkId': 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
                               'endpointId': 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
                               'host': '172.18.0.2',
                               'port': 3310,
                               'createdUtc': '2026-10-08T00:44:17.092625333Z',
                               'startedUtc': '2026-10-08T00:44:17.126210996Z'},
                   'portIsolation': {'declaredPortBindings': {},
                                     'publishAllPorts': False,
                                     'actualPortBindings': {},
                                     'dockerPortPublicationObserved': False,
                                     'absenceVerified': True},
                   'ownedInProcess': True,
                   'cleanupVerified': True,
                   'listenerClosed': True,
                   'ownedListenerInodeAbsent': True,
                   'connectionFailureTypes': ['ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError',
                                              'ConnectionRefusedError'],
                   'acceptorStopped': True},
 'runtimePolicy': {'readOnlyRoot': True,
                   'persistentVolumes': 0,
                   'directEntrypoint': '/usr/sbin/clamd',
                   'memoryBytes': 1610612736},
 'measurement': {'executable': '/usr/sbin/clamd',
                 'executableSha256': '79b5338d64f9345711550a33e0d4ac76acc425120c5831dfc65f7ba3cf48783b',
                 'configSha256': 'c64a43b47df212c085dfea3ecc6f6784667373774ffcd8472852b49de3ae8dcd',
                 'databases': [{'path': '/var/lib/clamav/bytecode.cvd',
                                'sha256': '6d4aa01f219e988060fc419f495d07f27e0cdf1a2cccc065971da922c76f7ffb'},
                               {'path': '/var/lib/clamav/daily.cvd',
                                'sha256': '0f7520addba76ca2fc500a95a9da946f3f12de512b42f861f7f89a4fddeb2375'},
                               {'path': '/var/lib/clamav/main.cvd',
                                'sha256': '0b2182d229f46981ec8f535382222f7c9dfdd656b250ad47988b910a8d302365'}]},
 'version': 'ClamAV 1.5.4/28136/Sun Sep 27 06:26:12 2026',
 'daemonProcess': {'pid': 1,
                   'kernelStartTicks': 3758,
                   'listenerInode': '15856',
                   'executable': '/usr/sbin/clamd',
                   'executableSha256': '79b5338d64f9345711550a33e0d4ac76acc425120c5831dfc65f7ba3cf48783b',
                   'argv': ['/usr/sbin/clamd', '--foreground', '--config-file=/etc/clamav/acceptance.conf'],
                   'configurationSha256': 'c64a43b47df212c085dfea3ecc6f6784667373774ffcd8472852b49de3ae8dcd',
                   'databaseDirectory': '/var/lib/clamav'},
 'scannerPlan': {'executable': '/usr/sbin/clamd',
                 'executable_sha256': '79b5338d64f9345711550a33e0d4ac76acc425120c5831dfc65f7ba3cf48783b',
                 'argv': ['/usr/sbin/clamd', '--foreground', '--config-file=/etc/clamav/acceptance.conf'],
                 'configuration': '/etc/clamav/acceptance.conf',
                 'configuration_sha256': 'c64a43b47df212c085dfea3ecc6f6784667373774ffcd8472852b49de3ae8dcd',
                 'database_files_sha256': {'bytecode.cvd': '6d4aa01f219e988060fc419f495d07f27e0cdf1a2cccc065971da922c76f7ffb',
                                           'daily.cvd': '0f7520addba76ca2fc500a95a9da946f3f12de512b42f861f7f89a4fddeb2375',
                                           'main.cvd': '0b2182d229f46981ec8f535382222f7c9dfdd656b250ad47988b910a8d302365'},
                 'startup_log': '/tmp/acceptance-clamd.log'},
 'parentAdmission': False,
 'endpoint': {'host': '127.0.0.1', 'port': 12345},
 'controls': {'clean': True, 'eicar': True, 'unavailableAfterStop': True},
 'counters': {'attempted': 2, 'clean': 1, 'infected': 1},
 'counterScope': 'Actual readiness control INSTREAM calls; financial File runtime counters remain unobserved',
 'stoppedContainerObserved': True,
 'cleanupErrors': [],
 'runtimeBoundaryObservations': {'start': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                           'imageId': 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                           'runLabel': 'synthetic-control',
                                           'createdUtc': '2026-10-08T00:44:17.092625333Z',
                                           'startedUtc': '2026-10-08T00:44:17.126210996Z',
                                           'policy': {'memoryBytes': 1610612736,
                                                      'nanoCpus': 2000000000,
                                                      'capDrop': ['ALL'],
                                                      'capAdd': None,
                                                      'readOnlyRoot': True,
                                                      'engineConfigurationObserved': True,
                                                      'kernelEnforcementObserved': False},
                                           'portIsolation': {'declaredPortBindings': {},
                                                             'publishAllPorts': False,
                                                             'actualPortBindings': {},
                                                             'dockerPortPublicationObserved': False,
                                                             'absenceVerified': True}},
                                 'current': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                             'imageId': 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                             'runLabel': 'synthetic-control',
                                             'createdUtc': '2026-10-08T00:44:17.092625333Z',
                                             'startedUtc': '2026-10-08T00:44:17.126210996Z',
                                             'policy': {'memoryBytes': 1610612736,
                                                        'nanoCpus': 2000000000,
                                                        'capDrop': ['ALL'],
                                                        'capAdd': None,
                                                        'readOnlyRoot': True,
                                                        'engineConfigurationObserved': True,
                                                        'kernelEnforcementObserved': False},
                                             'portIsolation': {'declaredPortBindings': {},
                                                               'publishAllPorts': False,
                                                               'actualPortBindings': {},
                                                               'dockerPortPublicationObserved': False,
                                                               'absenceVerified': True}},
                                 'preStop': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                             'imageId': 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                             'runLabel': 'synthetic-control',
                                             'createdUtc': '2026-10-08T00:44:17.092625333Z',
                                             'startedUtc': '2026-10-08T00:44:17.126210996Z',
                                             'policy': {'memoryBytes': 1610612736,
                                                        'nanoCpus': 2000000000,
                                                        'capDrop': ['ALL'],
                                                        'capAdd': None,
                                                        'readOnlyRoot': True,
                                                        'engineConfigurationObserved': True,
                                                        'kernelEnforcementObserved': False},
                                             'portIsolation': {'declaredPortBindings': {},
                                                               'publishAllPorts': False,
                                                               'actualPortBindings': {},
                                                               'dockerPortPublicationObserved': False,
                                                               'absenceVerified': True}},
                                 'preRemoval': {'containerId': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                                'imageId': 'sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                                'runLabel': 'synthetic-control',
                                                'createdUtc': '2026-10-08T00:44:17.092625333Z',
                                                'startedUtc': '2026-10-08T00:44:17.126210996Z',
                                                'policy': {'memoryBytes': 1610612736,
                                                           'nanoCpus': 2000000000,
                                                           'capDrop': ['ALL'],
                                                           'capAdd': None,
                                                           'readOnlyRoot': True,
                                                           'engineConfigurationObserved': True,
                                                           'kernelEnforcementObserved': False},
                                                'portIsolation': {'declaredPortBindings': {},
                                                                  'publishAllPorts': False,
                                                                  'actualPortBindings': {},
                                                                  'dockerPortPublicationObserved': False,
                                                                  'absenceVerified': True}}}}


SYNTHETIC["startupRelayHandoff"] = {
    "originalStartupAttempts": 16, "originalSettledConnectRefusals": 15,
    "originalStartupPongObserved": True, "originalStartupWorkersSettled": True}
SYNTHETIC["loopbackRelay"]["connectionFailureTypes"] = ["ConnectionRefusedError"] * 15 + ["ValueError"]


def validate(value):
    return validate_bytes(json.dumps(value).encode(), "123", "1", "f" * 40, "1" * 40)


class ScannerRuntimeReceiptTests(unittest.TestCase):
    def refuse(self, value):
        with self.assertRaises((ReceiptRefused, ValueError, TypeError, AttributeError)):
            validate(value)

    def test_positive_schema_reports_only_fixed_observation_scope(self):
        result = validate(copy.deepcopy(SYNTHETIC))
        self.assertEqual({"ScannerRuntimeObservedPhases": 4, "ScannerControlsPassed": 3,
                          "OwnedCleanupVerified": True, "KernelEnforcementObserved": False,
                          "GenuineEightHostFinancialAccepted": False}, result)

    def test_exact_all_four_phases_are_required(self):
        for phase in PHASES:
            value = copy.deepcopy(SYNTHETIC)
            del value["runtimeBoundaryObservations"][phase]
            self.refuse(value)
        value = copy.deepcopy(SYNTHETIC)
        value["runtimeBoundaryObservations"]["guessed"] = value["runtimeBoundaryObservations"]["start"]
        self.refuse(value)

    def test_each_phase_actual_policy_not_configured_placeholder(self):
        for phase in PHASES:
            for key, bad in (("memoryBytes", 0), ("memoryBytes", True), ("nanoCpus", 0),
                             ("nanoCpus", True), ("capDrop", []), ("capAdd", ["NET_ADMIN"]),
                             ("readOnlyRoot", False), ("engineConfigurationObserved", False),
                             ("engineConfigurationObserved", 1), ("kernelEnforcementObserved", True)):
                value = copy.deepcopy(SYNTHETIC)
                value["runtimeBoundaryObservations"][phase]["policy"][key] = bad
                self.refuse(value)
            for key in SYNTHETIC["runtimeBoundaryObservations"][phase]["policy"]:
                value = copy.deepcopy(SYNTHETIC)
                del value["runtimeBoundaryObservations"][phase]["policy"][key]
                self.refuse(value)

    def test_each_phase_owner_and_generation_must_match(self):
        for phase in PHASES:
            for key in ("containerId", "imageId", "runLabel", "createdUtc", "startedUtc"):
                value = copy.deepcopy(SYNTHETIC)
                value["runtimeBoundaryObservations"][phase][key] = "foreign"
                self.refuse(value)

    def test_every_phase_denies_configured_suppressed_or_actual_port_map(self):
        for phase in PHASES:
            for key, bad in (("declaredPortBindings", {"3310/tcp": []}),
                             ("actualPortBindings", {"3310/tcp": []}),
                             ("actualPortBindings", {"3310/tcp": [{"HostPort": "12345"}]}),
                             ("publishAllPorts", True), ("absenceVerified", False),
                             ("dockerPortPublicationObserved", True)):
                value = copy.deepcopy(SYNTHETIC)
                value["runtimeBoundaryObservations"][phase]["portIsolation"][key] = bad
                self.refuse(value)

    def test_readiness_controls_and_counter_types_cannot_be_inferred(self):
        for key in ("clean", "eicar", "unavailableAfterStop"):
            for bad in (False, 1):
                value = copy.deepcopy(SYNTHETIC)
                value["controls"][key] = bad
                self.refuse(value)
        for key in ("attempted", "clean", "infected"):
            for bad in (True, 0, 3):
                value = copy.deepcopy(SYNTHETIC)
                value["counters"][key] = bad
                self.refuse(value)

    def test_cleanup_and_original_policy_failures_stay_failed(self):
        for key in ("cleanupVerified", "stoppedContainerObserved", "scannerReady"):
            value = copy.deepcopy(SYNTHETIC)
            value[key] = False
            self.refuse(value)
        for error_key in ("cleanupErrors", "runtimeBoundaryFailures"):
            value = copy.deepcopy(SYNTHETIC)
            value[error_key] = ["preRemovalRefused"]
            self.refuse(value)
        for group in ("derivedImage", "network", "loopbackRelay"):
            value = copy.deepcopy(SYNTHETIC)
            value[group]["cleanupVerified"] = False
            self.refuse(value)
        for key in ("listenerClosed", "acceptorStopped", "ownedListenerInodeAbsent"):
            value = copy.deepcopy(SYNTHETIC)
            value["loopbackRelay"][key] = False
            self.refuse(value)

    def test_startup_and_relay_associations_cannot_substitute_other_owner(self):
        for group, key in (("startupDiagnostic", "containerId"), ("derivedImage", "runLabel"),
                           ("derivedImage", "baseImageId"), ("network", "networkId")):
            value = copy.deepcopy(SYNTHETIC)
            value[group][key] = "foreign"
            self.refuse(value)
        for key in ("containerId", "networkId", "createdUtc", "startedUtc"):
            value = copy.deepcopy(SYNTHETIC)
            value["loopbackRelay"]["backend"][key] = "foreign"
            self.refuse(value)
        value = copy.deepcopy(SYNTHETIC)
        value["startupDiagnostic"]["runtimeBoundaryObserved"] = False
        self.refuse(value)

    def test_hosted_head_run_attempt_and_event_are_exact(self):
        for key in ("runId", "attempt", "head", "eventSha"):
            value = copy.deepcopy(SYNTHETIC)
            value["hostedIdentity"][key] = "foreign"
            self.refuse(value)
        for index in range(4):
            arguments = ["123", "1", "f" * 40, "1" * 40]
            arguments[index] = None
            with self.assertRaises(ReceiptRefused):
                validate_bytes(json.dumps(SYNTHETIC).encode(), *arguments)

    def test_unknown_failure_privacy_and_business_claims_refuse(self):
        for key in ("failureType", "failureCommand", "failureReason", "privateToken", "rawPath"):
            value = copy.deepcopy(SYNTHETIC)
            value[key] = "synthetic-unknown"
            self.refuse(value)
        for key in ("genuineEightHostFinancialAccepted", "parentAdmission"):
            value = copy.deepcopy(SYNTHETIC)
            value[key] = True
            self.refuse(value)
        value = copy.deepcopy(SYNTHETIC)
        value["startupDiagnostic"]["startupLogErrorType"] = "TimeoutError"
        self.refuse(value)

    def test_unknown_nested_fields_and_raw_privacy_values_refuse(self):
        for group in ("derivedImage", "network", "startupDiagnostic", "runtimePolicy", "measurement",
                      "daemonProcess", "scannerPlan", "loopbackRelay", "containerGeneration", "hostedIdentity"):
            value = copy.deepcopy(SYNTHETIC)
            value[group]["privateRawPath"] = "synthetic-unknown"
            self.refuse(value)
        for phase in PHASES:
            for group in (None, "policy", "portIsolation"):
                value = copy.deepcopy(SYNTHETIC)
                target = value["runtimeBoundaryObservations"][phase]
                if group is not None: target = target[group]
                target["unknown"] = "synthetic-unknown"
                self.refuse(value)

    def test_duplicate_nonfinite_malformed_and_oversized_json_refuse(self):
        good = json.dumps(SYNTHETIC).encode()
        for raw in (b'{"schemaVersion":1,"schemaVersion":1}', b'{"x":NaN}', b'null', b'[]',
                    b'\xff', good + b' trailing', b' ' * (MAXIMUM + 1)):
            with self.assertRaises((ReceiptRefused, ValueError, UnicodeError)):
                validate_bytes(raw, "123", "1", "f" * 40, "1" * 40)

    def test_held_reader_refuses_nonregular_substitution_and_closes_exact_temporary_fd(self):
        raw = json.dumps(SYNTHETIC).encode()
        good = SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_size=len(raw), st_dev=1, st_ino=2,
                               st_mtime_ns=3, st_ctime_ns=4)
        for mode in ("good", "fifo", "oversized", "changed", "fdopen-failed", "read-failed"):
            before, after = copy.copy(good), copy.copy(good)
            if mode == "fifo": before.st_mode = stat.S_IFIFO | 0o600
            if mode == "oversized": before.st_size = MAXIMUM + 1
            if mode == "changed": after.st_ino = 99
            class FailedRead(io.BytesIO):
                def read(self, count): raise OSError("synthetic-read-fault")
            with ExitStack() as stack:
                stack.enter_context(patch.object(reader.os, "O_NOFOLLOW", 131072, create=True))
                stack.enter_context(patch.object(reader.os, "O_NONBLOCK", 2048, create=True))
                opened = stack.enter_context(patch.object(reader.os, "open", return_value=17))
                stack.enter_context(patch.object(reader.os, "fstat", side_effect=[before, after]))
                closed = stack.enter_context(patch.object(reader.os, "close"))
                stream = FailedRead(raw) if mode == "read-failed" else io.BytesIO(raw)
                opening = stack.enter_context(patch.object(reader.os, "fdopen", side_effect=OSError("synthetic-open-fault") if mode == "fdopen-failed" else None, return_value=stream))
                if mode == "good": self.assertEqual(raw, reader.held_receipt_read("synthetic-receipt"))
                else:
                    with self.assertRaises((ReceiptRefused, OSError)): reader.held_receipt_read("synthetic-receipt")
                self.assertEqual([unittest.mock.call(17)], closed.call_args_list)
                flags = opened.call_args.args[1]
                self.assertTrue(flags & reader.os.O_NONBLOCK and flags & reader.os.O_NOFOLLOW)
                if mode in ("fifo", "oversized"): opening.assert_not_called()
                if mode in ("good", "changed", "read-failed"): self.assertTrue(stream.closed)

    def test_reader_requests_at_most_limit_plus_one_before_parsing(self):
        class Reader:
            def __init__(self, raw): self.raw, self.requests = raw, []
            def read(self, count): self.requests.append(count); return self.raw[:count]
        reader = Reader(json.dumps(SYNTHETIC).encode())
        self.assertEqual(reader.raw, bounded_read(reader))
        self.assertEqual([MAXIMUM + 1], reader.requests)
        for raw in (b'', b'x' * (MAXIMUM + 1)):
            with self.assertRaises(ReceiptRefused): bounded_read(Reader(raw))



    def test_startup_handoff_is_mandatory_exact_and_never_failure_evidence(self):
        value=copy.deepcopy(SYNTHETIC);del value['startupRelayHandoff'];self.refuse(value)
        for key in SYNTHETIC['startupRelayHandoff']:
            value=copy.deepcopy(SYNTHETIC);del value['startupRelayHandoff'][key];self.refuse(value)
        for replacement in (None,[],True):
            value=copy.deepcopy(SYNTHETIC);value['startupRelayHandoff']=replacement;self.refuse(value)
        value=copy.deepcopy(SYNTHETIC);value['startupRelayHandoff']['unknown']=True;self.refuse(value)
        for key in ('startupAdmissionDiagnostic','failureType','failureReason','failureCommand'):
            value=copy.deepcopy(SYNTHETIC);value[key]={};self.refuse(value)

    def test_startup_handoff_counts_are_strict_bounded_ints(self):
        for key,bad_values in (
            ('originalStartupAttempts',(True,False,16.0,0,-1,17,'16',None)),
            ('originalSettledConnectRefusals',(True,False,15.0,-1,16,'15',None))):
            for bad in bad_values:
                value=copy.deepcopy(SYNTHETIC);value['startupRelayHandoff'][key]=bad;self.refuse(value)
        for attempts,refusals in ((15,15),(16,14),(1,1)):
            value=copy.deepcopy(SYNTHETIC)
            value['startupRelayHandoff']['originalStartupAttempts']=attempts
            value['startupRelayHandoff']['originalSettledConnectRefusals']=refusals
            self.refuse(value)

    def test_startup_handoff_flags_require_actual_true_without_aliases(self):
        for key in ('originalStartupPongObserved','originalStartupWorkersSettled'):
            for bad in (False,0,1,'true',None,[]):
                value=copy.deepcopy(SYNTHETIC);value['startupRelayHandoff'][key]=bad;self.refuse(value)

    def test_startup_handoff_rows_remain_exact_with_separate_unavailable_probe(self):
        for bad in ([],['ValueError'],['ConnectionRefusedError']*16,
                    ['ConnectionRefusedError']*14+['ValueError'],
                    ['ValueError']+['ConnectionRefusedError']*15,
                    ['ConnectionRefusedError']*15+['OSError'],
                    ['ConnectionRefusedError']*15+['ValueError','ValueError']):
            value=copy.deepcopy(SYNTHETIC);value['loopbackRelay']['connectionFailureTypes']=bad;self.refuse(value)
        value=copy.deepcopy(SYNTHETIC);value['startupRelayHandoff']['originalStartupAttempts']=1
        value['startupRelayHandoff']['originalSettledConnectRefusals']=0
        value['loopbackRelay']['connectionFailureTypes']=['ValueError']
        self.assertEqual(validate(value),validate(SYNTHETIC))

    def test_handoff_cannot_replace_existing_owner_control_cleanup_and_scope_gates(self):
        for group,key,bad in (('hostedIdentity','head','0'*40),('controls','unavailableAfterStop',False),
                              ('loopbackRelay','cleanupVerified',False),('counters','attempted',3)):
            value=copy.deepcopy(SYNTHETIC);value[group][key]=bad;self.refuse(value)
        for key in ('parentAdmission','genuineEightHostFinancialAccepted'):
            value=copy.deepcopy(SYNTHETIC);value[key]=True;self.refuse(value)


if __name__ == "__main__":
    unittest.main()
