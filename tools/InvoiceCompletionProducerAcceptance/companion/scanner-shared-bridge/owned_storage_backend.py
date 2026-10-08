"""Additive read-only admission adapter. No provider calls or acceptance booleans.

The caller must hold the exact created storage/scanner/network handles until all
File/front connections quiesce. This adapter neither launches nor removes them.
Pure observations in tests cannot mint a live admission.
"""
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
import os
import re
import subprocess
import sys

IMAGE = 'fsouza/fake-gcs-server@sha256:9e6924ee1b609d9913c3ea836cb4d4a9bc0419cd036ddcd136695720b5713baf'
IMAGE_ID = 'sha256:d79ded7272ddc99187d94f61959f37041792e1a79cb3e6d26c9dfb5b845aca21'
PREFIX = 'com.maliev.c821.'


class AdmissionError(ValueError):
    pass


def require(ok, message):
    if not ok:
        raise AdmissionError(message)


def hex_value(value, size):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{' + str(size) + '}', value) is not None


def instant(value):
    require(isinstance(value, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?Z', value), 'UTC instant required')
    return datetime.fromisoformat(value[:-1] + '+00:00')


@dataclass(frozen=True)
class Lease:
    container_id: str
    scanner_id: str
    network_id: str
    run_id: str
    attempt: str
    file_sha: str
    lease_id: str
    issued: str
    expires: str
    created: str
    started: str
    network_created: str
    bridge_ip: str
    port: int
    executable_sha: str
    kernel_ticks: int
    bind_owned_ipv4: bool = False

    def labels(self, role=None):
        result = {PREFIX + 'run': self.run_id, PREFIX + 'attempt': self.attempt,
                  PREFIX + 'file-source': self.file_sha, PREFIX + 'lease': self.lease_id,
                  PREFIX + 'expires': self.expires, PREFIX + 'persistent': 'false'}
        result['financial.acceptance.run'] = self.lease_id[5:]
        if role:
            result[PREFIX + 'role'] = role
        return result

    def validate(self, now):
        require(type(self.bind_owned_ipv4) is bool, 'Exact binding mode required')
        require(all(hex_value(x, 64) for x in (self.container_id, self.scanner_id, self.network_id)), 'Exact handles required')
        require(self.container_id != self.scanner_id, 'Distinct storage/scanner required')
        require(re.fullmatch('[1-9][0-9]{0,19}', self.run_id) and re.fullmatch('[1-9][0-9]{0,8}', self.attempt), 'Canonical run required')
        require(hex_value(self.file_sha, 40) and hex_value(self.executable_sha, 64), 'Source/executable seals required')
        require(re.fullmatch(r'c821-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', self.lease_id)
                and self.lease_id != 'c821-00000000-0000-0000-0000-000000000000', 'Canonical ownership lease required')
        issue, expiry = instant(self.issued), instant(self.expires)
        require(issue <= now < expiry and 0 < (expiry - issue).total_seconds() <= 1800, 'Current finite lease required')
        require(issue <= instant(self.created) <= instant(self.started) < expiry
                and issue <= instant(self.network_created) <= now and instant(self.started) <= now, 'Current generation required')
        address = ipaddress.IPv4Address(self.bridge_ip)
        require(any(address in ipaddress.ip_network(n) for n in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')), 'Literal private bridge required')
        require(type(self.port) is int and 1024 <= self.port <= 65535 and type(self.kernel_ticks) is int and self.kernel_ticks > 0, 'Port/kernel identity required')

    def arguments(self):
        origin = f'http://{self.bridge_ip}:{self.port}'
        return ['-scheme', 'http', '-host', self.bridge_ip if self.bind_owned_ipv4 else '0.0.0.0', '-port', str(self.port),
                '-backend', 'memory', '-external-url', origin, '-public-host', f'{self.bridge_ip}:{self.port}']


def labels_match(actual, expected):
    return isinstance(actual, dict) and all(actual.get(k) == v for k, v in expected.items())


def validate_snapshots(lease, container, image, network, scanner, now):
    """Pure fail-closed controls. Does not establish live resource ownership."""
    lease.validate(now)
    require(container.get('Id') == lease.container_id and container.get('Created') == lease.created, 'Storage generation differs')
    state, config, host = container.get('State', {}), container.get('Config', {}), container.get('HostConfig', {})
    require(state.get('Running') is True and state.get('Paused') is False and state.get('Restarting') is False
            and state.get('StartedAt') == lease.started and type(state.get('Pid')) is int and state['Pid'] > 0
            and container.get('RestartCount') == 0, 'Live storage generation required')
    require(config.get('Image') == IMAGE and container.get('Image') == IMAGE_ID and image.get('Id') == IMAGE_ID
            and IMAGE in (image.get('RepoDigests') or []), 'Reviewed immutable backend required')
    require(not config.get('Volumes') and not image.get('Config', {}).get('Volumes'), 'Image VOLUME declarations denied')
    require(config.get('Entrypoint') == ['/bin/fake-gcs-server'] and config.get('Cmd') == lease.arguments()
            and config.get('Env') == image.get('Config', {}).get('Env'), 'Immutable process configuration differs')
    require(labels_match(config.get('Labels'), lease.labels('storage')), 'Storage ownership differs')
    require(host.get('NetworkMode') == lease.network_id and host.get('ReadonlyRootfs') is True and host.get('Privileged') is False
            and host.get('CapDrop') == ['ALL'] and not host.get('CapAdd')
            and 'no-new-privileges:true' in (host.get('SecurityOpt') or [])
            and host.get('PidMode') == '' and host.get('IpcMode') == 'private' and host.get('CgroupnsMode') == 'private'
            and host.get('RestartPolicy', {}).get('Name') == 'no' and host.get('AutoRemove') is False, 'Storage isolation differs')
    require(type(host.get('Memory')) is int and 64*1024**2 <= host['Memory'] <= 1024**3
            and type(host.get('NanoCpus')) is int and 0 < host['NanoCpus'] <= 2_000_000_000, 'Finite storage limits required')
    require(not any(host.get(k) for k in ('Binds', 'Devices', 'Tmpfs', 'PortBindings')) and not container.get('Mounts'), 'Mount/publication denied')
    settings = container.get('NetworkSettings', {})
    require(not any(settings.get('Ports', {}).values()), 'Actual published storage endpoint denied')
    attachments = list((settings.get('Networks') or {}).values())
    require(len(attachments) == 1 and attachments[0].get('NetworkID') == lease.network_id
            and attachments[0].get('IPAddress') == lease.bridge_ip, 'Storage bridge differs')
    require(network.get('Id') == lease.network_id and network.get('Created') == lease.network_created
            and network.get('Internal') is True and network.get('Driver') == 'bridge'
            and network.get('EnableIPv6') is False and network.get('Scope') == 'local'
            and labels_match(network.get('Labels'), {'financial.acceptance.run': lease.lease_id[5:]}), 'Owned internal network required')
    require(set(network.get('Containers') or {}) == {lease.container_id, lease.scanner_id}, 'Exact two-resource census required')
    require(scanner.get('Id') == lease.scanner_id and labels_match(scanner.get('Config', {}).get('Labels'), {'financial.acceptance.run': lease.lease_id[5:]}),
            'Foreign scanner handle denied')
    scanner_networks = list((scanner.get('NetworkSettings', {}).get('Networks') or {}).values())
    require(len(scanner_networks) == 1 and scanner_networks[0].get('NetworkID') == lease.network_id, 'Scanner network differs')


def command(args, maximum=2*1024*1024):
    """Original API routed through the corrected retained owner command adapter."""
    from storage_owner_command import storage_command
    return storage_command(args, maximum)


def inspect(kind, handle, command_runner=command):
    parsed = json.loads(command_runner(['docker', kind, 'inspect', handle]))
    require(isinstance(parsed, list) and len(parsed) == 1 and isinstance(parsed[0], dict), 'One exact observation required')
    return parsed[0]


def _zero_listener_evidence(lease, runner, zero_error=None):
    """Subordinate read-only evidence. NEVER satisfies original IPv4 admission."""
    import time
    started = time.monotonic()
    remaining = (instant(lease.expires) - datetime.now(timezone.utc)).total_seconds()
    if not remaining > 0:
        raise AdmissionError('Listener evidence lease expired')
    deadline = started + min(30.0, remaining)
    calls = 0

    def call(args, maximum=262144):
        nonlocal calls
        if calls == 14:
            raise AdmissionError('Listener evidence command budget exhausted')
        calls += 1
        return runner(args, maximum, absolute_deadline=deadline)

    def execution(*args):
        return call(['docker', 'exec', lease.container_id, *args]).decode('ascii')

    def snapshot(kind, handle):
        value = json.loads(call(['docker', kind, 'inspect', handle]))
        if type(value) is not list or len(value) != 1 or type(value[0]) is not dict:
            raise AdmissionError('Listener evidence snapshot shape differs')
        return value[0]

    def generation(expected_pid=None, expected_ipam=None):
        container = snapshot('container', lease.container_id)
        checked_container(container)
        if expected_pid is not None and container['State']['Pid'] != expected_pid:
            return container, None, None
        network = snapshot('network', lease.network_id)
        checked_network(network)
        if expected_ipam is not None and network['IPAM'] != expected_ipam:
            return container, network, None
        scanner = snapshot('container', lease.scanner_id)
        checked_scanner(scanner)
        return container, network, scanner

    def start_ticks():
        value = execution('cat', '/proc/1/stat')
        fields = value[value.rfind(')') + 1:].split()
        if not value.startswith('1 (') or len(fields) < 20 or fields[0] in ('Z', 'X', 'x') or not fields[19].isdigit():
            raise AdmissionError('Listener evidence kernel stat differs')
        return int(fields[19])

    def fd_inodes():
        lines = execution('ls', '-l', '/proc/1/fd').splitlines()
        result, descriptors = {}, set()
        for line in lines:
            if line.startswith('total '):
                continue
            fields = line.split()
            if len(fields) < 9 or len(fields) > 16 or not re.fullmatch('l[rwxstST-]{9}', fields[0]):
                raise AdmissionError('Listener evidence descriptor row differs')
            if not fields[-3].isdigit() or fields[-2] != '->' or fields[-3] in descriptors:
                raise AdmissionError('Listener evidence descriptor identity differs')
            descriptors.add(fields[-3])
            if fields[-1].startswith('socket:'):
                match = re.fullmatch(r'socket:\[([1-9][0-9]{0,19})\]', fields[-1])
                if match is None:
                    raise AdmissionError('Listener evidence socket target differs')
                result[fields[-3]] = match[1]
        return result

    def listeners(path, address_width):
        lines = execution('cat', path).splitlines()
        if not lines or 'local_address' not in lines[0]:
            raise AdmissionError('Listener evidence table header differs')
        result = []
        for line in lines[1:]:
            fields = line.split()
            if len(fields) < 10 or len(fields) > 32 or re.fullmatch('[0-9A-F]{' + str(address_width) + '}:[0-9A-F]{4}', fields[1]) is None:
                raise AdmissionError('Listener evidence table row differs')
            expected_address = ipaddress.IPv4Address(lease.bridge_ip).packed[::-1].hex().upper() if address_width == 8 and lease.bind_owned_ipv4 else '0' * address_width
            if fields[1] == expected_address + f':{lease.port:04X}' and fields[3] == '0A':
                if re.fullmatch('[1-9][0-9]{0,19}', fields[9]) is None:
                    raise AdmissionError('Listener evidence socket inode differs')
                result.append(fields[9])
        return result

    def scanner_generation(value):
        state = value.get('State')
        if type(state) is not dict or state.get('Running') is not True or state.get('Paused') is not False or state.get('Restarting') is not False or type(state.get('Pid')) is not int or state['Pid'] <= 0:
            raise AdmissionError('Listener evidence scanner generation differs')
        created, started = value.get('Created'), state.get('StartedAt')
        instant(created)
        instant(started)
        return created, started, state['Pid']

    def checked_image(value):
        if (value.get('Id') != IMAGE_ID or type(value.get('Config')) is not dict
                or type(value.get('RepoDigests')) is not list or IMAGE not in value['RepoDigests']
                or value['Config'].get('Volumes')):
            raise AdmissionError('Listener evidence immutable image differs')

    def checked_container(container):
        if any(type(container.get(name)) is not dict for name in ('State', 'Config', 'HostConfig', 'NetworkSettings')):
            raise AdmissionError('Listener evidence container shape differs')
        require(container.get('Id') == lease.container_id and container.get('Created') == lease.created, 'Storage generation differs')
        state, config, host = container.get('State', {}), container.get('Config', {}), container.get('HostConfig', {})
        require(state.get('Running') is True and state.get('Paused') is False and state.get('Restarting') is False
                and state.get('StartedAt') == lease.started and type(state.get('Pid')) is int and state['Pid'] > 0
                and container.get('RestartCount') == 0, 'Live storage generation required')
        require(config.get('Image') == IMAGE and container.get('Image') == IMAGE_ID and image.get('Id') == IMAGE_ID
                and IMAGE in (image.get('RepoDigests') or []), 'Reviewed immutable backend required')
        require(not config.get('Volumes') and not image.get('Config', {}).get('Volumes'), 'Image VOLUME declarations denied')
        require(config.get('Entrypoint') == ['/bin/fake-gcs-server'] and config.get('Cmd') == lease.arguments()
                and config.get('Env') == image.get('Config', {}).get('Env'), 'Immutable process configuration differs')
        require(labels_match(config.get('Labels'), lease.labels('storage')), 'Storage ownership differs')
        require(host.get('NetworkMode') == lease.network_id and host.get('ReadonlyRootfs') is True and host.get('Privileged') is False
                and host.get('CapDrop') == ['ALL'] and not host.get('CapAdd')
                and 'no-new-privileges:true' in (host.get('SecurityOpt') or [])
                and host.get('PidMode') == '' and host.get('IpcMode') == 'private' and host.get('CgroupnsMode') == 'private'
                and host.get('RestartPolicy', {}).get('Name') == 'no' and host.get('AutoRemove') is False, 'Storage isolation differs')
        require(type(host.get('Memory')) is int and 64*1024**2 <= host['Memory'] <= 1024**3
                and type(host.get('NanoCpus')) is int and 0 < host['NanoCpus'] <= 2_000_000_000, 'Finite storage limits required')
        require(not any(host.get(k) for k in ('Binds', 'Devices', 'Tmpfs', 'PortBindings')) and not container.get('Mounts'), 'Mount/publication denied')
        settings = container.get('NetworkSettings', {})
        require(not any(settings.get('Ports', {}).values()), 'Actual published storage endpoint denied')
        attachments = list((settings.get('Networks') or {}).values())
        require(len(attachments) == 1 and attachments[0].get('NetworkID') == lease.network_id
                and attachments[0].get('IPAddress') == lease.bridge_ip, 'Storage bridge differs')

    def checked_network(network):
        if type(network.get('Containers')) is not dict or type(network.get('IPAM')) is not dict:
            raise AdmissionError('Listener evidence network shape differs')
        require(network.get('Id') == lease.network_id and network.get('Created') == lease.network_created
                and network.get('Internal') is True and network.get('Driver') == 'bridge'
                and network.get('EnableIPv6') is False and network.get('Scope') == 'local'
                and labels_match(network.get('Labels'), {'financial.acceptance.run': lease.lease_id[5:]}), 'Owned internal network required')
        require(set(network.get('Containers') or {}) == {lease.container_id, lease.scanner_id}, 'Exact two-resource census required')

    def checked_scanner(scanner):
        if type(scanner.get('Config')) is not dict or type(scanner.get('NetworkSettings')) is not dict:
            raise AdmissionError('Listener evidence scanner shape differs')
        require(scanner.get('Id') == lease.scanner_id and labels_match(scanner.get('Config', {}).get('Labels'), {'financial.acceptance.run': lease.lease_id[5:]}),
                'Foreign scanner handle denied')
        scanner_networks = list((scanner.get('NetworkSettings', {}).get('Networks') or {}).values())
        require(len(scanner_networks) == 1 and scanner_networks[0].get('NetworkID') == lease.network_id, 'Scanner network differs')
        scanner_generation(scanner)

    def finish(clause):
        if not time.monotonic() < deadline or not datetime.now(timezone.utc) < instant(lease.expires):
            raise AdmissionError('Listener evidence classification expired')
        if zero_error is not None:
            import pair_failure_diagnostic as diagnostic
            diagnostic.listener_evidence(zero_error, clause)
        return clause

    image = snapshot('image', IMAGE_ID)
    checked_image(image)
    before, network_before, scanner_before = generation()
    validate_snapshots(lease, before, image, network_before, scanner_before, datetime.now(timezone.utc))
    scanner_identity = scanner_generation(scanner_before)
    ticks_before = start_ticks()
    if ticks_before != lease.kernel_ticks:
        return finish('zero-ipv4-generation-changed')
    fd_before = fd_inodes()
    tcp4 = listeners('/proc/1/net/tcp', 8)
    if tcp4:
        return finish('zero-ipv4-later-ipv4-present')
    tcp6 = listeners('/proc/1/net/tcp6', 32)
    if not tcp6:
        return finish('zero-ipv4-no-selected-tcp6-listener')
    if len(tcp6) != 1:
        return finish('zero-ipv4-multiple-tcp6-listeners')
    owning_before = {fd for fd, inode in fd_before.items() if inode == tcp6[0]}
    if not owning_before:
        return finish('zero-ipv4-tcp6-fd-unowned')
    fd_after = fd_inodes()
    owning_after = {fd for fd, inode in fd_after.items() if inode == tcp6[0]}
    if owning_after != owning_before:
        return finish('zero-ipv4-tcp6-fd-unowned')
    ticks_after = start_ticks()
    if ticks_after != ticks_before:
        return finish('zero-ipv4-generation-changed')
    after, network_after, scanner_after = generation(before['State']['Pid'], network_before['IPAM'])
    if network_after is None or scanner_after is None:
        return finish('zero-ipv4-generation-changed')
    validate_snapshots(lease, after, image, network_after, scanner_after, datetime.now(timezone.utc))
    if not time.monotonic() < deadline:
        raise AdmissionError('Listener evidence deadline expired')
    if scanner_generation(scanner_after) != scanner_identity:
        return finish('zero-ipv4-generation-changed')
    return finish('zero-ipv4-tcp6-single-init-owned-stable')


def observe(lease, shared_scanner_network, command_runner=None):
    """Read-only native observation, available only in the admitted hosted Linux run.

    Returns private source-bound observations, never File configuration. Existing
    front network admission requires owner integration before these are usable.
    """
    from storage_owner_command import storage_command
    require(command_runner is None or command_runner is storage_command,
            'Exact imported source-owned command runner required')
    runner = command if command_runner is None else command_runner
    require(sys.platform == 'linux' and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'
            and os.environ.get('GITHUB_RUN_ID') == lease.run_id and os.environ.get('GITHUB_RUN_ATTEMPT') == lease.attempt,
            'Exact hosted Linux run required')
    lease.validate(datetime.now(timezone.utc))
    # Source-reviewed scanner owner supplies this non-owning handle. Its refresh
    # independently rechecks the held fixture, source and kernel generation.
    # No caller-supplied snapshot can replace the live Docker observations below.
    handle = shared_scanner_network
    require(handle.network_id == lease.network_id and handle.scanner_container_id == lease.scanner_id
            and handle.created_utc == lease.network_created
            and handle.ownership_labels == {'financial.acceptance.run': lease.lease_id[5:]}, 'Held scanner network differs')
    context = handle.context
    require(context.run_id == lease.run_id and str(context.attempt) == lease.attempt and context.file_sha == lease.file_sha
            and context.lease_id == lease.lease_id and context.expires_utc == lease.expires
            and context.issued_utc == lease.issued, 'Scanner context differs')
    def refresh():
        value = handle.refresh()
        require(value['networkId'] == lease.network_id and value['createdUtc'] == lease.network_created
                and value['scannerContainerId'] == lease.scanner_id and value['internal'] is True and value['driver'] == 'bridge'
                and value['ownershipLabels'] == handle.ownership_labels
                and value['members'] == sorted([lease.container_id, lease.scanner_id]), 'Held shared network census differs')
    refresh()
    def snapshots():
        c = inspect('container', lease.container_id, runner)
        i = inspect('image', IMAGE_ID, runner)
        n = inspect('network', lease.network_id, runner)
        s = inspect('container', lease.scanner_id, runner)
        validate_snapshots(lease, c, i, n, s, datetime.now(timezone.utc))
        return c
    before = snapshots()
    def execute(*args, maximum=65536):
        return runner(['docker', 'exec', lease.container_id, *args], maximum)
    def ticks():
        stat = execute('cat', '/proc/1/stat').decode('ascii')
        fields = stat[stat.rfind(')')+1:].split()
        require(stat.startswith('1 (') and len(fields) >= 20 and fields[0] not in ('Z', 'X', 'x'), 'Live kernel generation required')
        return int(fields[19])
    require(ticks() == lease.kernel_ticks, 'Kernel generation differs')
    require(execute('readlink', '/proc/1/exe').decode('ascii').strip() == '/bin/fake-gcs-server', 'Executable path differs')
    require(execute('sha256sum', '/proc/1/exe').decode('ascii').split()[0] == lease.executable_sha, 'Executable seal differs')
    require(execute('cat', '/proc/1/cmdline').split(b'\0')[:-1] == [x.encode() for x in ['/bin/fake-gcs-server', *lease.arguments()]], 'Actual process command differs')
    rows = execute('cat', '/proc/1/net/tcp', maximum=1048576).decode('ascii').splitlines()
    require(rows and 'local_address' in rows[0], 'Kernel TCP table required')
    inodes = []
    for row in rows[1:]:
        fields = row.split()
        require(len(fields) >= 10, 'Malformed kernel TCP row')
        if fields[1] == (ipaddress.IPv4Address(lease.bridge_ip).packed[::-1].hex().upper() if lease.bind_owned_ipv4 else '00000000') + f':{lease.port:04X}' and fields[3] == '0A':
            require(fields[9].isdigit() and int(fields[9]) > 0, 'Actual socket inode required')
            inodes.append(fields[9])
    if len(inodes) == 0:
        try:
            require(False, 'One exact backend listener required')
        except AdmissionError as zero_error:
            import pair_failure_diagnostic as diagnostic
            diagnostic.failure(zero_error)
            _zero_listener_evidence(lease, storage_command, zero_error)
            raise
    else:
        require(len(inodes) == 1, 'One exact backend listener required')
    fd = execute('ls', '-l', '/proc/1/fd', maximum=262144).decode('ascii').splitlines()
    require(any(line.rstrip().endswith('-> socket:[' + inodes[0] + ']') for line in fd), 'Backend init must own its listener')
    require(ticks() == lease.kernel_ticks and snapshots()['State']['Pid'] == before['State']['Pid'], 'Observed process changed')
    refresh()
    return {'privateBackendObserved': True, 'fileSdkAccepted': False, 'genuineEightHostFinancialAccepted': False,
            'integrationRequired': 'ObservedStorageBackend.VerifyNetwork exact two-member owner admission',
            'executableSha256': lease.executable_sha, 'imageReference': IMAGE}
