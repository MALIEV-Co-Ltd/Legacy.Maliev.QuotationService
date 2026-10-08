"""Original-owner standalone hosted pair launcher; not a File/front composer.

The outer source loader must pin ALL modules/expectations before calling. This
module consumes a separately qualified executable digest; it never hashes a live
binary to create its own expected value. No ownership JSON is accepted.
"""
from datetime import datetime, timezone
import ipaddress
from itertools import islice
from functools import wraps
import json
import os
import re
import sys

import pair_failure_diagnostic as diagnostic

from borrowed_scanner_bridge import BorrowedScannerBridge, BridgeRefused
from storage_owner_command import storage_command

# Keep the actual owner reachable before first allocation and across all failures.
# One pair per worker; root must retain/fence this module until exact cleanup.
OWNER = None


def require(ok, message):
    if not ok:
        diagnostic.guard(message)
        raise BridgeRefused(message)


def owner_operation(function):
    @wraps(function)
    def guarded(self, *args, **kwargs):
        try:
            return function(self, *args, **kwargs)
        except BaseException as diagnostic_error:
            diagnostic.failure(diagnostic_error)
            self.failure = True
            if self.bridge is not None:
                self.bridge._failure = True
            raise
    return guarded


def select_backend_ip(network):
    rows = network.get('IPAM', {}).get('Config')
    require(type(rows) is list and len(rows) == 1, 'One actual bridge subnet required')
    subnet = ipaddress.ip_network(rows[0]['Subnet'])
    require(subnet.version == 4 and any(subnet.subnet_of(ipaddress.ip_network(value))
            for value in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')), 'Private IPv4 bridge required')
    used = {ipaddress.ip_interface(row['IPv4Address']).ip
            for row in network.get('Containers', {}).values()}
    if rows[0].get('Gateway'):
        used.add(ipaddress.ip_address(rows[0]['Gateway']))
    for candidate in islice(subnet.hosts(), 256):
        if candidate not in used:
            return str(candidate)
    raise BridgeRefused('No bounded free bridge address')


class HeldPairLauncher:
    def __init__(self, context, expected_executable_sha256):
        global OWNER
        import hosted_companion_resources as h
        import hosted_scanner_readiness as sm
        import owned_storage_backend as storage
        import scanner_docker_command
        import inspect
        require(OWNER is None, 'One retained pair owner required')
        require(type(context) is h.Context and sys.platform == 'linux'
                and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted', 'Original hosted context required')
        context.validate(os.environ, datetime.now(timezone.utc))
        require(type(expected_executable_sha256) is str
                and re.fullmatch('[0-9a-f]{64}', expected_executable_sha256)
                and expected_executable_sha256 != '0' * 64, 'Independent executable expectation required')
        require('command_runner' in inspect.signature(storage.observe).parameters
                and callable(scanner_docker_command.run_docker), 'Reviewed owner command seam unavailable')
        self.context = context
        self.executable_sha256 = expected_executable_sha256
        self.scanner = sm.Scanner(run_id=context.lease_id[5:], deadline_seconds=120, configured_network=True)
        self.bridge = None
        self.backend_id = None
        self.backend_name = 'financial-storage-' + self.scanner.run_id
        self.allocation_issued = None
        self.backend_create_attempted = False
        self.backend_started = None
        self.lease = None
        self.failure = False
        self.finished = False
        OWNER = self

    def inspect(self, kind, handle):
        rows = json.loads(self.scanner.docker(kind, 'inspect', handle, timeout=3))
        require(type(rows) is list and len(rows) == 1, 'One exact Engine object required')
        return rows[0]

    def labels(self):
        c = self.context
        return {'financial.acceptance.run': self.scanner.run_id,
                'com.maliev.c821.run': c.run_id, 'com.maliev.c821.attempt': str(c.attempt),
                'com.maliev.c821.file-source': c.file_sha, 'com.maliev.c821.lease': c.lease_id,
                'com.maliev.c821.expires': c.expires_utc, 'com.maliev.c821.persistent': 'false',
                'com.maliev.c821.role': 'storage'}

    def require_backend_ownership(self, container):
        import owned_storage_backend as storage
        require(container.get('Id') == self.backend_id and container.get('Name') == '/' + self.backend_name
                and container.get('Image') == storage.IMAGE_ID
                and all(container.get('Config', {}).get('Labels', {}).get(k) == v
                        for k, v in self.labels().items()), 'Exact backend ownership required')
        created = datetime.fromisoformat(container['Created'].replace('Z', '+00:00'))
        require(self.allocation_issued <= created <= datetime.now(timezone.utc), 'Backend allocation generation differs')
        if self.backend_started is not None:
            require(container.get('State', {}).get('StartedAt') == self.backend_started,
                    'Backend start generation differs')

    @owner_operation
    def start(self):
        require(OWNER is self and not self.finished and not self.failure
                and self.bridge is None and not self.backend_create_attempted,
                'One clean pair start required')
        try:
            import owned_storage_backend as storage
            diagnostic.stage('scanner-start')
            self.scanner.start()
            diagnostic.stage('scanner-bridge-acquire')
            self.bridge = BorrowedScannerBridge(self.scanner, self.context)
            network = self.inspect('network', self.scanner.network_id)
            self.scanner.validate_configured_network(network)
            address = select_backend_ip(network)
            diagnostic.stage('storage-image')
            self.scanner.docker('pull', storage.IMAGE, timeout=120)
            image = self.inspect('image', storage.IMAGE)
            require(image.get('Id') == storage.IMAGE_ID and storage.IMAGE in (image.get('RepoDigests') or [])
                    and not image.get('Config', {}).get('Volumes'), 'Qualified volume-free backend image differs')
            port = 4443
            arguments = ['-scheme', 'http', '-host', '0.0.0.0', '-port', str(port), '-backend', 'memory',
                         '-external-url', 'http://' + address + ':' + str(port),
                         '-public-host', address + ':' + str(port)]
            args = ['create', '--name', self.backend_name, '--network', self.scanner.network_id,
                    '--ip', address, '--memory', '256m', '--cpus', '1', '--read-only', '--cap-drop', 'ALL',
                    '--security-opt', 'no-new-privileges:true', '--ipc', 'private', '--cgroupns', 'private',
                    '--restart', 'no', '--entrypoint', '/bin/fake-gcs-server']
            for key, value in self.labels().items():
                args.extend(['--label', key + '=' + value])
            args.extend([storage.IMAGE, *arguments])
            self.allocation_issued = datetime.now(timezone.utc)
            self.backend_create_attempted = True
            diagnostic.stage('storage-create')
            created_id = self.scanner.docker(*args, timeout=10)
            require(re.fullmatch('[0-9a-f]{64}', created_id) is not None, 'Exact created backend ID required')
            self.backend_id = created_id
            diagnostic.stage('storage-start')
            self.scanner.docker('start', self.backend_id, timeout=10)
            container = self.inspect('container', self.backend_id)
            self.require_backend_ownership(container)
            self.backend_started = container['State']['StartedAt']
            diagnostic.stage('storage-process')
            stat = storage_command(['docker', 'exec', self.backend_id, 'cat', '/proc/1/stat']).decode('ascii')
            fields = stat[stat.rfind(')') + 1:].split()
            require(stat.startswith('1 (') and len(fields) >= 20 and fields[0] not in ('Z', 'X', 'x'),
                    'Actual live backend kernel birth required')
            c = self.context
            self.lease = storage.Lease(self.backend_id, self.scanner.container_id, self.scanner.network_id,
                c.run_id, str(c.attempt), c.file_sha, c.lease_id, c.issued_utc, c.expires_utc,
                container['Created'], self.backend_started, network['Created'], address, port,
                self.executable_sha256, int(fields[19]))
            diagnostic.stage('bridge-borrow')
            self.bridge.borrow(self.lease)
            return self
        except BaseException:
            self.failure = True
            raise

    @owner_operation
    def observe(self):
        require(OWNER is self and not self.finished and not self.failure
                and self.lease is not None, 'Clean current retained pair owner required')
        try:
            import owned_storage_backend as storage
            diagnostic.stage('storage-observe')
            return storage.observe(self.lease, self.bridge, command_runner=storage_command)
        except BaseException:
            self.failure = True
            self.bridge._failure = True
            raise

    @owner_operation
    def close(self):
        """Always call while OWNER remains held, including after start/import failure.

        Corrected runner quarantine has no public recovery API; retain owner and refuse.
        Any unresolved state retains OWNER; no clean exit or receipt is produced.
        """
        global OWNER
        diagnostic.stage('pair-release')
        if self.finished:
            require(not self.failure, 'Original pair failure remains sticky')
            return
        require(OWNER is self, 'Current retained pair owner required')
        try:
            if self.bridge is not None and self.bridge._borrow is not None:
                present = self.scanner.docker('ps', '-a', '--no-trunc', '--filter',
                    'id=' + self.bridge._borrow.container_id, '--format', '{{.ID}}', timeout=3).splitlines()
                require(present in ([], [self.bridge._borrow.container_id]), 'Borrowed backend census differs')
                if present:
                    self.bridge.remove_backend_after_quiescence()
                else:
                    self.bridge.confirm_backend_absent_and_release()
                self.backend_id = None
            elif self.backend_create_attempted:
                if self.backend_id is None:
                    ids = self.scanner.docker('ps', '-a', '--no-trunc', '--filter',
                        'name=^/' + self.backend_name + '$', '--filter',
                        'label=financial.acceptance.run=' + self.scanner.run_id, '--format', '{{.ID}}', timeout=3).splitlines()
                    require(len(ids) <= 1, 'Uncertain allocation is ambiguous')
                    if ids:
                        require(re.fullmatch('[0-9a-f]{64}', ids[0]) is not None, 'Recovered exact backend ID required')
                        self.backend_id = ids[0]
                if self.backend_id is not None:
                    # Recovery of an uncertain rm must not require inspect of an
                    # already absent container. A full-ID census is independently read.
                    present = self.scanner.docker('ps', '-a', '--no-trunc', '--filter',
                        'id=' + self.backend_id, '--format', '{{.ID}}', timeout=3).splitlines()
                    require(present in ([], [self.backend_id]), 'Backend census differs')
                    if not present:
                        self.backend_id = None
                if self.backend_id is not None:
                    container = self.inspect('container', self.backend_id)
                    self.require_backend_ownership(container)
                    generation = (container['Created'], container['State']['StartedAt'])
                    self.scanner.docker('stop', '--time', '5', self.backend_id, timeout=10)
                    stopped = self.inspect('container', self.backend_id)
                    self.require_backend_ownership(stopped)
                    require((stopped['Created'], stopped['State']['StartedAt']) == generation
                            and all(stopped['State'].get(k) is False for k in ('Running', 'Paused', 'Restarting')),
                            'Exact stopped backend required')
                    self.scanner.docker('rm', self.backend_id, timeout=5)
                    ids = self.scanner.docker('ps', '-a', '--no-trunc', '--filter', 'id=' + self.backend_id,
                        '--format', '{{.ID}}', timeout=3)
                    require(ids.strip() == '', 'Backend absence not observed')
                    self.backend_id = None
            if self.bridge is not None and self.bridge._released:
                try:
                    self.bridge.close_scanner()
                except BaseException:
                    # Sticky original refusal may be raised AFTER physical owned
                    # cleanup. It must still fail proof, without losing the fact
                    # that the exact scanner owner completed its own cleanup.
                    if self.scanner.receipt.get('cleanupVerified') is not True:
                        raise
                    self.failure = True
            else:
                if self.bridge is not None:
                    self.bridge._refresh_members((self.scanner.container_id,), cleanup=True)
                # Original Scanner owns relay/image/network even after setup failure.
                self.scanner.close()
            require(self.scanner.receipt.get('cleanupVerified') is True, 'Original scanner cleanup not verified')
            self.finished = True
            OWNER = None
        except BaseException:
            self.failure = True
            raise
        require(not self.failure, 'Original pair failure remains sticky')
