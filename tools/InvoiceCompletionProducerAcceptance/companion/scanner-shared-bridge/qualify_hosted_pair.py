"""Normal actual pair qualification only; imports consume preflight-held bytes.

No File/frontend runtime is launched. Quarantine remains in this living worker;
the workflow timeout is an external runner boundary, not proof of local cleanup.
"""
import hashlib
import importlib.abc
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
import types
import unittest
from datetime import datetime, timedelta, timezone
import uuid


LIMIT = 1048576
MANIFEST_SHA256 = '9c1ed6441eab02c715e1341bdf0141188f97031e928b3aaf0ff9386dd2d20545'


class Refused(ValueError):
    pass


def require(value):
    if not value:
        raise Refused('Standalone source/owner qualification refused')


def bounded_read(path, limit):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and 0 < before.st_size <= limit)
        data = bytearray()
        while len(data) <= limit:
            block = os.read(descriptor, min(65536, limit + 1 - len(data)))
            if not block:
                break
            data.extend(block)
        after = os.fstat(descriptor)
        current = os.stat(path, follow_symlinks=False)
        fields = lambda value: (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns)
        require(len(data) <= limit and len(data) == before.st_size
                and fields(before) == fields(after) == fields(current))
        return bytes(data)
    finally:
        os.close(descriptor)


def exact_head(root):
    # Standard actions/checkout .git directory only. Alternate/worktree layouts
    # require a separately reviewed acquisition path rather than shell guessing.
    text = bounded_read(root / '.git/HEAD', 4096).decode('ascii').strip()
    if text.startswith('ref: '):
        reference = text[5:]
        require(re.fullmatch(r'refs/heads/[A-Za-z0-9/_-]+', reference) is not None)
        text = bounded_read(root / '.git' / reference, 128).decode('ascii').strip()
    require(re.fullmatch('[0-9a-f]{40}', text) is not None)
    return text


def admit_event(environment, event, head):
    require(environment.get('GITHUB_ACTIONS') == 'true'
            and environment.get('RUNNER_ENVIRONMENT') == 'github-hosted'
            and environment.get('RUNNER_OS') == 'Linux'
            and environment.get('GITHUB_EVENT_NAME') == 'pull_request'
            and environment.get('FINANCIAL_SOURCE_HEAD') == head)
    pull = event.get('pull_request', {})
    require(event.get('repository', {}).get('full_name') == environment.get('GITHUB_REPOSITORY')
            and pull.get('head', {}).get('repo', {}).get('full_name') == environment.get('GITHUB_REPOSITORY')
            and pull.get('head', {}).get('sha') == head
            and pull.get('head', {}).get('ref') in ('codex/scanner-shared-pair-20261008', 'codex/file-front-caller-join-20261009')
            and type(pull.get('number')) is int and pull['number'] > 0)


def hold_sources(root, manifest):
    require(type(manifest) is dict and set(manifest) == {'schemaVersion', 'fileSource', 'fileSourceRole', 'modules', 'rawControls', 'oracleControls', 'startupControls'}
            and type(manifest['schemaVersion']) is int and manifest['schemaVersion'] == 1
            and manifest['fileSourceRole'] == 'provenance-label-only-no-File-runtime'
            and re.fullmatch('[0-9a-f]{40}', manifest['fileSource']) is not None
            and type(manifest['modules']) is list and len(manifest['modules']) == 11)
    held = {}
    paths = set()
    for item in manifest['modules']:
        require(type(item) is dict and set(item) == {'name', 'path', 'sha256', 'length'}
                and re.fullmatch('[a-z_]+', item['name']) is not None
                and item['name'] not in held and item['path'] not in paths
                and item['path'].startswith('tools/InvoiceCompletionProducerAcceptance/companion/')
                and '..' not in Path(item['path']).parts and '\\' not in item['path']
                and re.fullmatch('[0-9a-f]{64}', item['sha256']) is not None
                and type(item['length']) is int and 0 < item['length'] <= LIMIT)
        path = root / item['path']
        require(path.resolve().is_relative_to(root.resolve()))
        data = bounded_read(path, item['length'])
        require(len(data) == item['length'] and hashlib.sha256(data).hexdigest() == item['sha256'])
        held[item['name']] = data
        paths.add(item['path'])
    require(set(held) == {'scanner_docker_command', 'hosted_companion_resources', 'scanner_loopback_relay',
                         'hosted_scanner_readiness', 'borrowed_scanner_bridge', 'owned_storage_backend',
                         'storage_owner_command', 'held_pair_launcher', 'pinned_image_oracle', 'qualify_pair_resources', 'pair_failure_diagnostic'})
    return held


def hold_raw_controls(root, manifest):
    item = manifest['rawControls']
    path = 'tools/InvoiceCompletionProducerAcceptance/companion/test_raw_owner_command.py'
    require(type(item) is dict and set(item) == {'path', 'sha256', 'length'}
            and item['path'] == path and re.fullmatch('[0-9a-f]{64}', item['sha256']) is not None
            and type(item['length']) is int and 0 < item['length'] <= LIMIT)
    data = bounded_read(root / path, item['length'])
    require(len(data) == item['length'] and hashlib.sha256(data).hexdigest() == item['sha256'])
    return data


def hold_oracle_controls(root, manifest):
    item = manifest['oracleControls']
    path = 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/test_pinned_image_oracle.py'
    require(type(item) is dict and set(item) == {'path', 'sha256', 'length'}
            and item['path'] == path and re.fullmatch('[0-9a-f]{64}', item['sha256']) is not None
            and type(item['length']) is int and 0 < item['length'] <= LIMIT)
    data = bounded_read(root / path, item['length'])
    require(len(data) == item['length'] and hashlib.sha256(data).hexdigest() == item['sha256'])
    return data


def hold_startup_controls(root, manifest):
    item = manifest['startupControls']
    path = 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/test_startup_ping_handoff.py'
    require(type(item) is dict and set(item) == {'path', 'sha256', 'length'}
            and item['path'] == path and re.fullmatch('[0-9a-f]{64}', item['sha256']) is not None
            and type(item['length']) is int and 0 < item['length'] <= LIMIT)
    data = bounded_read(root / path, item['length'])
    require(len(data) == item['length'] and hashlib.sha256(data).hexdigest() == item['sha256'])
    return data


def execute_startup_controls(data, relay_source, scanner_source):
    name = 'held_startup_ping_controls'
    require(name not in sys.modules)
    module = types.ModuleType(name)
    module.__file__ = '<private-held-source:' + name + '>'
    sys.modules[name] = module
    exec(compile(data, module.__file__, 'exec'), module.__dict__)
    module.HELD_RELAY_SOURCE = relay_source
    module.HELD_SCANNER_SOURCE = scanner_source
    suite = unittest.defaultTestLoader.loadTestsFromModule(module)
    require(suite.countTestCases() == 55)
    result = unittest.TextTestRunner().run(suite)
    require(result.wasSuccessful() and result.testsRun == 55)


def execute_oracle_controls(data, oracle_source):
    name = 'held_oracle_copy_controls'
    require(name not in sys.modules)
    module = types.ModuleType(name)
    module.__file__ = '<private-held-source:' + name + '>'
    sys.modules[name] = module
    exec(compile(data, module.__file__, 'exec'), module.__dict__)
    module.HELD_ORACLE_SOURCE = oracle_source
    suite = unittest.defaultTestLoader.loadTestsFromModule(module)
    require(suite.countTestCases() == 11)
    result = unittest.TextTestRunner().run(suite)
    require(result.wasSuccessful() and result.testsRun == 11)
    oracle = sys.modules['pinned_image_oracle']
    diagnostic = sys.modules['pair_failure_diagnostic']
    require(oracle.ORACLE is None and diagnostic.FIRST is None and diagnostic.BINDING is None)


def execute_raw_controls(data, qualifier_source):
    # All runtime and test bytes were held before the first runtime import.
    # Controls exercise models only; tests cannot reread the qualifier pathname.
    name = 'held_raw_owner_controls'
    require(name not in sys.modules)
    module = types.ModuleType(name)
    module.__file__ = '<private-held-source:' + name + '>'
    sys.modules[name] = module
    exec(compile(data, module.__file__, 'exec'), module.__dict__)
    module.HELD_QUALIFIER_SOURCE = qualifier_source
    suite = unittest.defaultTestLoader.loadTestsFromModule(module)
    require(suite.countTestCases() == 13)
    result = unittest.TextTestRunner().run(suite)
    require(result.wasSuccessful() and result.testsRun == 13)


def observe_raw_git_version(runner, qualifier):
    require(runner.command_receipts() == ())
    observed = runner.run_git_bytes(['--version'], timeout=10)
    require(type(observed) is bytes and len(observed) <= 512
            and re.fullmatch(rb'git version [0-9]+\.[0-9]+\.[0-9]+[a-zA-Z0-9.+-]*\n', observed) is not None)
    rows = runner.command_receipts()
    require(type(rows) is tuple and len(rows) == 1)
    qualifier.validate_original_command_ledger(rows, ())
    require(not owners_retained())
    return hashlib.sha256(observed).hexdigest()


class HeldImports(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    def __init__(self, held):
        self.held = held
        self._observer_class_births = {}

    def find_spec(self, fullname, path=None, target=None):
        if fullname in self.held:
            return importlib.util.spec_from_loader(fullname, self)
        return None

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        module.__file__ = '<private-held-source:' + module.__name__ + '>'
        tracked = module.__name__ in ('owned_storage_backend', 'borrowed_scanner_bridge')
        if tracked:
            require(module.__name__ not in self._observer_class_births)
            self._observer_class_births[module.__name__] = (module, None)
        exec(compile(self.held[module.__name__], module.__file__, 'exec'), module.__dict__)
        if tracked:
            name = 'AdmissionError' if module.__name__ == 'owned_storage_backend' else 'BridgeRefused'
            self._observer_class_births[module.__name__] = (module, getattr(module, name))


CALLER_MANIFEST_SHA256 = '675e35d3eef7971e16ab14e7d50d980c584333043ccea2f3fa8596f272afa890'
CALLER_MODULE_PATHS = {'actual_dotnet_start': 'tools/InvoiceCompletionProducerAcceptance/companion/actual_dotnet_start.py', 'borrowed_scanner_bridge': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/borrowed_scanner_bridge.py', 'held_host_lifetime': 'tools/InvoiceCompletionProducerAcceptance/companion/held_host_lifetime.py', 'held_linux_exit': 'tools/InvoiceCompletionProducerAcceptance/companion/held_linux_exit.py', 'held_pair_launcher': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/held_pair_launcher.py', 'hosted_companion_resources': 'tools/InvoiceCompletionProducerAcceptance/companion/hosted_companion_resources.py', 'hosted_scanner_readiness': 'tools/InvoiceCompletionProducerAcceptance/companion/hosted_scanner_readiness.py', 'owned_front_host': 'tools/InvoiceCompletionProducerAcceptance/companion/owned_front_host.py', 'owned_normal_hosts': 'tools/InvoiceCompletionProducerAcceptance/companion/owned_normal_hosts.py', 'owned_storage_backend': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/owned_storage_backend.py', 'pair_failure_diagnostic': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/pair_failure_diagnostic.py', 'regular_owned_files': 'tools/InvoiceCompletionProducerAcceptance/companion/regular_owned_files.py', 'retained_file_front_consumers': 'tools/InvoiceCompletionProducerAcceptance/companion/retained_file_front_consumers.py', 'run_eight_host_financial_acceptance': 'tools/InvoiceCompletionProducerAcceptance/companion/run_eight_host_financial_acceptance.py', 'scanner_docker_command': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner_docker_command.py', 'scanner_loopback_relay': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner_loopback_relay.py', 'sdk_observer_command': 'tools/InvoiceCompletionProducerAcceptance/companion/sdk_observer_command.py', 'shared_file_front_configuration': 'tools/InvoiceCompletionProducerAcceptance/companion/shared_file_front_configuration.py', 'storage_owner_command': 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/storage_owner_command.py', 'tls_listener_admission': 'tools/InvoiceCompletionProducerAcceptance/companion/tls_listener_admission.py'}
CALLER_CONTROL_PATHS = ('tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/test_bridge_consumers.py', 'tools/InvoiceCompletionProducerAcceptance/companion/test_actual_dotnet_start.py', 'tools/InvoiceCompletionProducerAcceptance/companion/test_caller_source_admission.py', 'tools/InvoiceCompletionProducerAcceptance/companion/test_caller_startup.py', 'tools/InvoiceCompletionProducerAcceptance/companion/test_retained_file_front_consumers.py', 'tools/InvoiceCompletionProducerAcceptance/companion/test_sdk_observer_command.py')


def admit_caller_sources(root):
    """Qualify source imports only; no profile, graph, resource or authority creation."""
    path = root / 'tools/InvoiceCompletionProducerAcceptance/companion/file-front-caller-source-manifest.json'
    encoded = bounded_read(path, LIMIT)
    require(hashlib.sha256(encoded).hexdigest() == CALLER_MANIFEST_SHA256)
    manifest = json.loads(encoded)
    require(type(manifest) is dict and set(manifest) == {'schemaVersion', 'qualificationBase', 'sourceOnly', 'ordinaryFileAuthorityAccepted', 'modules', 'controls', 'reviewedPackets'}
            and type(manifest['schemaVersion']) is int and manifest['schemaVersion'] == 1
            and manifest['qualificationBase'] == '4dc6d4982ad90bdd6c10fbf42c6acd5e60d0859b'
            and manifest['sourceOnly'] is True and manifest['ordinaryFileAuthorityAccepted'] is False
            and type(manifest['modules']) is list and len(manifest['modules']) == 20)
    held = {}
    for row in manifest['modules']:
        require(type(row) is dict and set(row) == {'name', 'path', 'sha256', 'length'}
                and type(row['name']) is str and row['name'] in CALLER_MODULE_PATHS
                and row['name'] not in held and row['path'] == CALLER_MODULE_PATHS[row['name']]
                and type(row['sha256']) is str and re.fullmatch('[0-9a-f]{64}', row['sha256']) is not None
                and type(row['length']) is int and 0 < row['length'] <= LIMIT)
        source = root / row['path']
        require(source.resolve().is_relative_to(root.resolve()))
        data = bounded_read(source, row['length'])
        require(len(data) == row['length'] and hashlib.sha256(data).hexdigest() == row['sha256'])
        held[row['name']] = data
    require(set(held) == set(CALLER_MODULE_PATHS))
    require(type(manifest['controls']) is list and len(manifest['controls']) == len(CALLER_CONTROL_PATHS))
    controls = set()
    for row in manifest['controls']:
        require(type(row) is dict and set(row) == {'path', 'sha256', 'length'}
                and type(row['path']) is str and row['path'] in CALLER_CONTROL_PATHS and row['path'] not in controls
                and type(row['sha256']) is str and re.fullmatch('[0-9a-f]{64}', row['sha256']) is not None
                and type(row['length']) is int and 0 < row['length'] <= LIMIT)
        data = bounded_read(root / row['path'], row['length'])
        require(len(data) == row['length'] and hashlib.sha256(data).hexdigest() == row['sha256'])
        controls.add(row['path'])
    require(controls == set(CALLER_CONTROL_PATHS))
    return held


class CallerHeldImports(HeldImports):
    def __init__(self, root, held):
        super().__init__(held)
        self.root = root
        self._caller_births = {}

    def exec_module(self, module):
        # Execute only the held bytes with original class-birth custody. Exact
        # qualified paths support original later source rechecks; disk bytes are
        # never executed by this loader.
        require(module.__name__ in self.held and module.__name__ not in self._caller_births)
        self._caller_births[module.__name__] = (module, module.__dict__, False)
        super().exec_module(module)
        module.__file__ = str(self.root / CALLER_MODULE_PATHS[module.__name__])
        self._caller_births[module.__name__] = (module, module.__dict__, True)


def recheck_caller_sources(caller):
    """Required immediately before original actor acquisition; no receipt emission."""
    require(type(caller) is types.ModuleType and caller.__name__ == 'run_eight_host_financial_acceptance')
    owner = caller.__spec__.loader
    require(type(owner) is CallerHeldImports and sys.modules.get(caller.__name__) is caller)
    require(caller.__name__ in owner._caller_births and owner._caller_births[caller.__name__][0] is caller
            and owner._caller_births[caller.__name__][1] is caller.__dict__ and owner._caller_births[caller.__name__][2] is True)
    current = admit_caller_sources(owner.root)
    require(set(current) == set(owner.held)
            and all(current[name] == owner.held[name] for name in current))
    for name in current:
        if name in sys.modules:
            require(type(sys.modules[name]) is types.ModuleType and sys.modules[name].__spec__.loader is owner
                    and name in owner._caller_births and owner._caller_births[name][0] is sys.modules[name]
                    and owner._caller_births[name][1] is sys.modules[name].__dict__ and owner._caller_births[name][2] is True
                    and sys.modules[name].__file__ == str(owner.root / CALLER_MODULE_PATHS[name]))


def prepare_qualified_file_front_startup(caller, *args):
    recheck_caller_sources(caller)
    return caller.prepare_file_front_startup(*args)


def admit_and_load_caller_sources():
    root = Path.cwd()
    head = exact_head(root)
    event = json.loads(bounded_read(Path(os.environ['GITHUB_EVENT_PATH']), LIMIT))
    admit_event(os.environ, event, head)
    held = admit_caller_sources(root)
    require(not any(name in sys.modules for name in held))
    owner = CallerHeldImports(root, held)
    sys.meta_path.insert(0, owner)
    import run_eight_host_financial_acceptance as caller
    require(caller.__spec__.loader is owner)
    recheck_caller_sources(caller)
    return caller, owner


def owners_retained():
    runner = sys.modules.get('scanner_docker_command')
    launcher = sys.modules.get('held_pair_launcher')
    oracle = sys.modules.get('pinned_image_oracle')
    return bool((runner is not None and getattr(runner, '_OWNERS', ()))
                or (launcher is not None and getattr(launcher, 'OWNER', None) is not None)
                or (oracle is not None and getattr(oracle, 'ORACLE', None) is not None))


def living_failure_fence():
    # Never exit a worker voluntarily while an original registry retains leases.
    # CI's ultimate timeout/cancellation is explicitly not cleanup acceptance.
    while True:
        try:
            if not owners_retained():
                return
            time.sleep(0.25)
        except BaseException:
            continue


def validate_pair_projection(proof):
    positive = ('originalCliCleanupObserved', 'independentStoppedImageFileObserved',
                'actualSharedBridgeAndBackendProcessObserved', 'scopedOracleAndPairCleanupObserved')
    negative = ('fileBoundaryAccepted', 'frontNativeAccepted', 'financialBusinessGraphAccepted',
                'genuineEightHostFinancialAccepted', 'allChildFdCensusAccepted',
                'kernelCliCapsObserved', 'parentDeathCleanupProved')
    identifiers = ('backendImageReference', 'backendImageId', 'independentImageExecutableSha256', 'fileSource')
    require(type(proof) is dict and set(proof) == set(positive + negative + identifiers)
            | {'schemaVersion', 'handledReadQueryAssociations', 'originalCliCommandsObserved'}
            and type(proof['schemaVersion']) is int and proof['schemaVersion'] == 1
            and all(proof[key] is True for key in positive)
            and all(proof[key] is False for key in negative)
            and type(proof['originalCliCommandsObserved']) is int
            and 0 < proof['originalCliCommandsObserved'] <= 256
            and type(proof['handledReadQueryAssociations']) is tuple
            and re.fullmatch('[0-9a-f]{64}', proof['independentImageExecutableSha256']) is not None)


def admit_and_load():
    root = Path.cwd()
    head = exact_head(root)
    event = json.loads(bounded_read(Path(os.environ['GITHUB_EVENT_PATH']), LIMIT))
    admit_event(os.environ, event, head)
    manifest_path = root / 'tools/InvoiceCompletionProducerAcceptance/companion/scanner-shared-bridge/hosted-pair-manifest.json'
    manifest_bytes = bounded_read(manifest_path, LIMIT)
    require(hashlib.sha256(manifest_bytes).hexdigest() == MANIFEST_SHA256)
    manifest = json.loads(manifest_bytes)
    held = hold_sources(root, manifest)
    startup_controls = hold_startup_controls(root, manifest)
    raw_controls = hold_raw_controls(root, manifest)
    oracle_controls = hold_oracle_controls(root, manifest)
    require(not any(name in sys.modules for name in held))
    sys.meta_path.insert(0, HeldImports(held))
    execute_oracle_controls(oracle_controls, held['pinned_image_oracle'])
    execute_startup_controls(startup_controls, held['scanner_loopback_relay'], held['hosted_scanner_readiness'])
    execute_raw_controls(raw_controls, held['qualify_pair_resources'])
    import hosted_companion_resources as h
    import qualify_pair_resources as qualifier
    import scanner_docker_command as runner
    require(runner.command_receipts() == () and not owners_retained())
    import pair_failure_diagnostic as diagnostic
    require(type(h.__spec__.loader) is HeldImports
            and h.__spec__.loader.held['hosted_companion_resources'] is held['hosted_companion_resources'])
    diagnostic.capture_h_sites(h, held['hosted_companion_resources'])
    bridge = sys.modules['borrowed_scanner_bridge']
    require(type(bridge.__spec__.loader) is HeldImports
            and bridge.__spec__.loader.held['borrowed_scanner_bridge'] is held['borrowed_scanner_bridge'])
    diagnostic.capture_bridge_sites(bridge, held['borrowed_scanner_bridge'])
    import hosted_scanner_readiness as scanner
    diagnostic.capture_network_sites(scanner, held['hosted_scanner_readiness'])
    diagnostic.capture_storage_sites(sys.modules['held_pair_launcher'], scanner, runner,
        {'launcher':held['held_pair_launcher'], 'scanner':held['hosted_scanner_readiness'], 'runner':held['scanner_docker_command']})
    import owned_storage_backend as storage
    import storage_owner_command as storage_command
    diagnostic.capture_observer_sites(storage, storage_command, bridge,
        {'storage':held['owned_storage_backend'], 'command':held['storage_owner_command']},
        storage.__spec__.loader._observer_class_births)
    return root, head, manifest, manifest_bytes, h, qualifier, runner


def validated_context(manifest, resources):
    run = os.environ.get('GITHUB_RUN_ID', '')
    attempt = os.environ.get('GITHUB_RUN_ATTEMPT', '')
    require(type(run) is str and re.fullmatch('[1-9][0-9]{0,19}', run) is not None
            and int(run) <= 2**63 - 1
            and type(attempt) is str and re.fullmatch('[1-9][0-9]{0,9}', attempt) is not None
            and int(attempt) <= 2**31 - 1)
    now = datetime.now(timezone.utc)
    context = resources.Context(run, int(attempt), manifest['fileSource'], 'c821-' + str(uuid.uuid4()),
                                now.isoformat().replace('+00:00', 'Z'),
                                (now + timedelta(minutes=10)).isoformat().replace('+00:00', 'Z'))
    context.validate(os.environ, now)
    return context


def main():
    root, head, manifest, manifest_bytes, h, qualifier, runner = admit_and_load()
    context = validated_context(manifest, h)
    import pair_failure_diagnostic as diagnostic
    diagnostic.bind(head, context.run_id, context.attempt, hashlib.sha256(manifest_bytes).hexdigest(), 'pair')
    diagnostic.stage('public-worker')
    # The callable owns independent oracle and pair finalizers. No dictionaries
    # are supplied as ownership/observation evidence by this wrapper.
    proof = qualifier.qualify_pair(context)
    require(not owners_retained())
    diagnostic.stage('public-projection')
    validate_pair_projection(proof)
    require(proof['fileSource'] == manifest['fileSource'])
    proof.update(sourceHead=head, runId=context.run_id, runAttempt=context.attempt,
                 sourceManifestSha256=hashlib.sha256(manifest_bytes).hexdigest(),
                 fileSourceRole=manifest['fileSourceRole'], fileRuntimeAccepted=False,
                 heldRawSourceControlMethodsPassed=13)
    output = root / 'TestResults/HostedSharedPair/standalone-pair.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(proof, sort_keys=True) + '\n', encoding='utf8')



def raw_git_main():
    root, head, manifest, manifest_bytes, h, qualifier, runner = admit_and_load()
    context = validated_context(manifest, h)
    import pair_failure_diagnostic as diagnostic
    diagnostic.bind(head, context.run_id, context.attempt, hashlib.sha256(manifest_bytes).hexdigest(), 'raw-git')
    diagnostic.stage('raw-git')
    digest = observe_raw_git_version(runner, qualifier)
    # Dedicated fresh worker/receipt, never counted as a pair operation.
    proof = {'schemaVersion': 1, 'sourceHead': head, 'runId': context.run_id,
             'runAttempt': context.attempt,
             'sourceManifestSha256': hashlib.sha256(manifest_bytes).hexdigest(),
             'actualRawGitVersionObserved': True, 'rawStdoutTerminalNewlineObserved': True,
             'rawVersionBytesSha256': digest, 'originalCliCommandsObserved': len(runner.command_receipts()),
             'originalCliCleanupObserved': True, 'heldRawSourceControlMethodsPassed': 13,
             'fileRuntimeAccepted': False, 'fileBoundaryAccepted': False, 'frontNativeAccepted': False,
             'financialBusinessGraphAccepted': False, 'genuineEightHostFinancialAccepted': False,
             'allChildFdCensusAccepted': False, 'kernelCliCapsObserved': False, 'parentDeathCleanupProved': False}
    output = root / 'TestResults/HostedSharedPair/raw-git.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(proof, sort_keys=True) + '\n', encoding='utf8')


def write_failure_diagnostic(error):
    # Best-effort public evidence cannot unwind or replace the critical living fence.
    try:
        diagnostic = sys.modules.get('pair_failure_diagnostic')
        if diagnostic is None:
            return
        diagnostic.failure(error)
        proof = diagnostic.projection()
        output = Path.cwd() / 'TestResults/HostedSharedPair/first-failure.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(proof, sort_keys=True) + '\n', encoding='utf8')
    except BaseException:
        return


if __name__ == '__main__':
    try:
        if sys.argv[1:] == ['--raw-git']:
            raw_git_main()
        else:
            require(sys.argv[1:] == [])
            main()
    except BaseException as failure:
        try:
            write_failure_diagnostic(failure)
            print('Standalone pair qualification refused; private owners retained when unsettled', file=sys.stderr)
        except BaseException:
            pass
        finally:
            living_failure_fence()
        raise SystemExit(1)
