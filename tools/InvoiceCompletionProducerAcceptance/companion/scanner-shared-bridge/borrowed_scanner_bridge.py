"""Non-owning bridge capability over the actual held Scanner, never a receipt factory.

Loader must acquire/pin imported owner modules before importing this adapter.
Live operations are hosted only. No scanner/network creation or broad removal.
"""
from datetime import datetime, timezone
import json
import os
import sys
from types import MappingProxyType
from functools import wraps


class BridgeRefused(ValueError):
    pass


def require(value, message):
    if not value:
        raise BridgeRefused(message)


def sticky(function):
    @wraps(function)
    def guarded(self, *args, **kwargs):
        try:
            return function(self, *args, **kwargs)
        except BaseException:
            self._failure = True
            raise
    return guarded


def validate_image_chain(base, derived, build, base_reference, runtime_id, run):
    require(base_reference in (base.get('RepoDigests') or []), 'Pinned base image absent')
    require(build.get('buildCompleted') is True and build.get('owned') is True
            and build.get('runLabel') == run and build.get('baseImageId') == base.get('Id')
            and build.get('imageId') == runtime_id == derived.get('Id'), 'Owned derived build differs')
    require(derived.get('Config', {}).get('Labels', {}).get('financial.acceptance.run') == run,
            'Derived image ownership differs')
    base_layers = base.get('RootFS', {}).get('Layers')
    derived_layers = derived.get('RootFS', {}).get('Layers')
    require(type(base_layers) is list and len(base_layers) > 0 and type(derived_layers) is list
            and len(derived_layers) == len(base_layers) + 1
            and derived_layers[:-1] == base_layers, 'Actual derived layer ancestry differs')
    require(not derived.get('Config', {}).get('Volumes'), 'Derived image persistent volume denied')


def validate_network(network, network_id, created, run, members):
    require(network.get('Id') == network_id and network.get('Created') == created
            and network.get('Internal') is True and network.get('Driver') == 'bridge'
            and network.get('EnableIPv6') is False and network.get('Scope') == 'local'
            and network.get('Labels', {}).get('financial.acceptance.run') == run,
            'Held bridge generation or policy differs')
    require(type(network.get('Containers')) is dict
            and set(network['Containers']) == set(members), 'Exact bridge census differs')


class BorrowedScannerBridge:
    """One backend borrow; scanner owner must close ONLY through close_scanner().

    Optional File/front consumers must use the exact retained original lifetime
    capability; ordinary process/dictionary adoption remains refused.

    The Python reference retains the actual Scanner. It cannot prevent an outside
    owner from calling Scanner.close directly; doing so invalidates refresh.
    Existing owner source is unchanged. Private outputs are never File admission.
    """
    def __init__(self, scanner, context):
        import hosted_scanner_readiness as scanner_module
        import hosted_companion_resources as h
        require(type(scanner) is scanner_module.Scanner and type(context) is h.Context,
                'Original owner instances required')
        require(sys.platform == 'linux' and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted',
                'Hosted Linux owner required')
        context.validate(os.environ, datetime.now(timezone.utc))
        require(scanner.run_id == context.lease_id[5:] and scanner.receipt.get('scannerReady') is True,
                'Live scanner run/readiness differs')
        self.scanner = scanner
        self.context = context
        self.network_id = scanner.network_id
        self.scanner_container_id = scanner.container_id
        self.ownership_labels = MappingProxyType({'financial.acceptance.run': scanner.run_id})
        self._generation = dict(scanner.receipt['containerGeneration'])
        self._runtime_id = scanner.image_id
        self._borrow = None
        self._consumers = {}
        self._released = False
        self._failure = False
        network = self._inspect('network', self.network_id)
        self.created_utc = network['Created']
        self._refresh_members((self.scanner_container_id,))

    def _inspect(self, kind, handle):
        rows = json.loads(self.scanner.docker(kind, 'inspect', handle, timeout=3))
        require(type(rows) is list and len(rows) == 1 and type(rows[0]) is dict,
                'One actual Engine observation required')
        return rows[0]

    def _relay(self):
        relay = self.scanner.relay
        require(relay is not None, 'Held relay unavailable')
        require(not relay.stop.is_set(), 'Held relay unavailable')
        require(relay.acceptor.is_alive(), 'Held relay unavailable')
        require(relay.ready_history_valid(), 'Held relay unavailable')
        require(relay.listener.fileno() >= 0, 'Held relay unavailable')
        require(relay.listener.getsockname() == relay.endpoint, 'Held relay unavailable')
        require(relay.endpoint == ('127.0.0.1', self.scanner.port), 'Held relay unavailable')
        expected = relay.identity
        require(expected['ownerPid'] == os.getpid()
                and expected['listenerFd'] == relay.listener.fileno()
                and os.readlink('/proc/self/fd/' + str(relay.listener.fileno())) == expected['listenerSocket'],
                'Actual relay descriptor differs')
        with open('/proc/self/stat', encoding='ascii') as stream:
            stat = stream.read(8193)
        require(len(stat) <= 8192 and int(stat[stat.rfind(')') + 2:].split()[19]) == expected['ownerKernelStartTicks'],
                'Relay owner generation differs')
        self.scanner.validate_backend_endpoint()

    def _refresh_members(self, members, cleanup=False):
        self.context.validate(os.environ, datetime.now(timezone.utc), cleanup=cleanup)
        if self._consumers and not cleanup:
            capability = self._retained_consumers()
            require(not capability.failed and not capability.released,
                    'Retained consumers are quarantined or settled')
            if capability.admitted:
                capability.observe_before_use()
            else:
                # Phase follows original Child custody, not the admitted flag:
                # front-only preparation uses actual original live observation;
                # any File birth/publication requires full consumer admission.
                fronts = [row for row in capability.lifetime.children
                          if row.owner is capability.front and row.role == 'Front']
                if fronts:
                    capability.observe_prepared_front()
                else:
                    capability.observe_prebirth()
        require(not self._released and self.scanner.network_id == self.network_id
                and self.scanner.container_id == self.scanner_container_id
                and self.scanner.image_id == self._runtime_id
                and self.scanner.receipt['containerGeneration'] == self._generation,
                'Held scanner changed')
        if cleanup:
            container = self._inspect('container', self.scanner_container_id)
            require(container.get('Id') == self.scanner_container_id
                    and container.get('Image') == self._runtime_id
                    and container.get('Created') == self._generation['createdUtc']
                    and container.get('State', {}).get('StartedAt') == self._generation['startedUtc']
                    and container.get('Config', {}).get('Labels', {}).get('financial.acceptance.run') == self.scanner.run_id,
                    'Cleanup scanner ownership differs')
            network = self._inspect('network', self.network_id)
            validate_network(network, self.network_id, self.created_utc, self.scanner.run_id, members)
            return None
        self._relay()
        import hosted_scanner_readiness as sm
        base = self._inspect('image', sm.IMAGE)
        derived = self._inspect('image', self._runtime_id)
        validate_image_chain(base, derived, self.scanner.receipt['derivedImage'], sm.IMAGE,
                             self._runtime_id, self.scanner.run_id)
        network = self._inspect('network', self.network_id)
        validate_network(network, self.network_id, self.created_utc, self.scanner.run_id, members)
        return {'networkId': self.network_id, 'createdUtc': self.created_utc,
                'scannerContainerId': self.scanner_container_id, 'internal': True, 'driver': 'bridge',
                'ownershipLabels': dict(self.ownership_labels), 'members': sorted(members)}

    @sticky
    def borrow(self, lease):
        require(not self._failure, 'Failed bridge quarantined for cleanup only')
        import owned_storage_backend as storage
        require(type(lease) is storage.Lease and self._borrow is None and not self._released,
                'One original storage lease required')
        require(lease.network_id == self.network_id and lease.scanner_id == self.scanner_container_id
                and lease.network_created == self.created_utc and lease.run_id == self.context.run_id
                and lease.attempt == str(self.context.attempt) and lease.file_sha == self.context.file_sha
                and lease.lease_id == self.context.lease_id and lease.issued == self.context.issued_utc
                and lease.expires == self.context.expires_utc, 'Storage context differs')
        # Retain before first fallible observation; failed admission still owns the borrow.
        self._borrow = lease
        try:
            self.refresh()
        except BaseException:
            self._failure = True
            raise
        return self

    @sticky
    def refresh(self):
        require(not self._failure, 'Failed bridge quarantined for cleanup only')
        require(self._borrow is not None, 'Storage borrow required')
        lease = self._borrow
        value = self._refresh_members((self.scanner_container_id, lease.container_id))
        import owned_storage_backend as storage
        storage.validate_snapshots(lease, self._inspect('container', lease.container_id),
                                   self._inspect('image', storage.IMAGE_ID),
                                   self._inspect('network', self.network_id),
                                   self._inspect('container', self.scanner_container_id),
                                   datetime.now(timezone.utc))
        return value

    @sticky
    def retain_consumer(self, role, process):
        # Popen.poll()/closed streams cannot establish original birth identity,
        # reader-task settlement, or File/front source admission. Producer-owned
        # retained lifecycle handoff is still absent; refuse consumer use.
        raise BridgeRefused('Qualified File/front lifecycle handoff unavailable')

    @sticky
    def retain_file_front_consumers(self, capability):
        """Atomic pre-birth retention; no process adoption or caller receipts."""
        import retained_file_front_consumers as consumers
        require(type(capability) is consumers.RetainedFileFrontConsumers,
                'Exact original retained consumer capability required')
        require(not self._failure and not self._released and self._borrow is not None
                and not self._consumers, 'One current consumer handoff required')
        # Retain BOTH references before fallible original-custody observation.
        self._consumers = {'File': capability, 'Front': capability}
        retained = self._retained_consumers()
        require(not capability.admitted and not capability.failed and not capability.released
                and capability.front.process is None and not any(
                    row.role in ('File', 'Front') for row in capability.lifetime.children),
                'Original consumers must be retained before birth')
        retained.observe_prebirth()
        return None

    def _retained_consumers(self):
        import retained_file_front_consumers as consumers
        import held_host_lifetime as lifetime
        require(set(self._consumers) == {'File', 'Front'}
                and self._consumers['File'] is self._consumers['Front'],
                'Exact paired retained consumers required')
        capability = self._consumers['File']
        require(type(capability) is consumers.RetainedFileFrontConsumers
                and type(capability.lifetime) is lifetime.HostLifetime
                and type(capability.normal) is lifetime.LifetimeNormalHosts
                and type(capability.front) is lifetime.LifetimeFrontHost,
                'Original consumer owner types required')
        capability._custody()
        require(capability.normal.context is self.context and capability.front.context is self.context,
                'Original consumers must share the exact bridge context')
        return capability

    def _quiescent(self):
        if not self._consumers:
            return  # Original standalone path, no fabricated consumer settlement.
        capability = self._retained_consumers()
        # Always use the original source-owned close/release API, including repeat
        # absence checks. Its clean close is idempotent; its failures remain sticky.
        # Never clear retained references on success/failure or trust released alone.
        capability.close_original_and_assert_backend_release()
        require(capability.released and not capability.failed,
                'Original consumer settlement remains incomplete')

    @sticky
    def remove_backend_after_quiescence(self):
        require(self._borrow is not None and not self._released, 'Retained borrow required')
        self._quiescent()
        lease = self._borrow
        # Expired lease refuses new use; cleanup keeps exact ownership checks.
        self._refresh_members((self.scanner_container_id, lease.container_id), cleanup=True)
        before = self._inspect('container', lease.container_id)
        require(before.get('Id') == lease.container_id and before.get('Created') == lease.created
                and before.get('State', {}).get('StartedAt') == lease.started
                and before.get('Image') == __import__('owned_storage_backend').IMAGE_ID
                and all(before.get('Config', {}).get('Labels', {}).get(k) == v
                        for k, v in lease.labels('storage').items()), 'Cleanup backend ownership differs')
        try:
            self.scanner.docker('stop', '--time', '5', lease.container_id, timeout=10)
            stopped = self._inspect('container', lease.container_id)
            require(stopped.get('Id') == lease.container_id and stopped.get('Created') == before['Created']
                    and stopped.get('State', {}).get('StartedAt') == before['State']['StartedAt']
                    and stopped.get('Config', {}).get('Labels') == before['Config']['Labels']
                    and all(stopped.get('State', {}).get(key) is False for key in ('Running', 'Paused', 'Restarting')),
                    'Exact stopped backend generation required')
            self.scanner.docker('rm', lease.container_id, timeout=5)
            self.confirm_backend_absent_and_release()
        except BaseException:
            self._failure = True
            raise

    @sticky
    def confirm_backend_absent_and_release(self):
        # Also permits finite recovery after an uncertain remove; absence is reobserved.
        require(self._borrow is not None and not self._released, 'Retained borrow required')
        self._quiescent()
        ids = self.scanner.docker('ps', '-a', '--no-trunc', '--filter',
                                  'id=' + self._borrow.container_id, '--format', '{{.ID}}', timeout=3)
        require(ids.strip() == '', 'Backend removal not verified')
        self._refresh_members((self.scanner_container_id,), cleanup=True)
        self._borrow = None
        self._released = True

    @sticky
    def close_scanner(self):
        require(self._released and self._borrow is None, 'Bridge borrower not released')
        self.scanner.close()
        require(self.scanner.receipt.get('cleanupVerified') is True, 'Scanner cleanup not verified')
        require(not self._failure, 'Original bridge failure remains sticky')
