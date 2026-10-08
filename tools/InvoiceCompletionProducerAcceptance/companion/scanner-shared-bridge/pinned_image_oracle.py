"""Independent fixed-image file oracle; qualification container is NEVER started.

Returns only a digest of image content copied from a separately owned stopped
container, not a live-backend hash or BuildReceipt. Outer loader pins every module.
All operations are forbidden outside one-thread hosted Linux qualification.
"""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import threading
from functools import wraps

from borrowed_scanner_bridge import BridgeRefused

ORACLE = None
LIMIT = 64 * 1024 * 1024


def require(ok, message):
    if not ok:
        raise BridgeRefused(message)


def oracle_operation(function):
    @wraps(function)
    def guarded(self, *args, **kwargs):
        try:
            return function(self, *args, **kwargs)
        except BaseException:
            self.failure = True
            raise
    return guarded


def checked_image(image, reference, image_id):
    layers = image.get('RootFS', {}).get('Layers')
    require(image.get('Id') == image_id and reference in (image.get('RepoDigests') or [])
            and type(layers) is list and len(layers) > 0
            and all(type(value) is str and re.fullmatch('sha256:[0-9a-f]{64}', value) for value in layers)
            and not image.get('Config', {}).get('Volumes'), 'Immutable volume-free image differs')
    return tuple(layers)


class PinnedImageOracle:
    def __init__(self, context):
        global ORACLE
        import hosted_companion_resources as h
        import sys
        require(ORACLE is None and type(context) is h.Context and sys.platform == 'linux'
                and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'
                and threading.active_count() == 1, 'Exclusive original hosted image oracle required')
        context.validate(os.environ, datetime.now(timezone.utc))
        self.context = context
        self.container_id = None
        self.name = 'financial-image-oracle-' + context.lease_id[5:]
        self.issued = None
        self.create_attempted = False
        self.created = None
        self.layers = None
        self.directory = None
        self.directory_identity = None
        self.path = None
        self.fd = None
        self.file_identity = None
        self.fd_close_attempted = False
        self.fd_close_attempted = False
        self.rlimit_prior = None
        self.cap_restore_required = False
        self.rlimit_prior = None
        self.cap_restore_required = False
        self.failure = False
        self.finished = False
        ORACLE = self

    def docker(self, *args, timeout=10):
        import scanner_docker_command as runner
        return runner.run_docker(list(args), timeout=timeout)

    def inspect(self, kind, handle):
        rows = json.loads(self.docker(kind, 'inspect', handle, timeout=3))
        require(type(rows) is list and len(rows) == 1, 'One actual oracle object required')
        return rows[0]

    def require_container(self, container):
        import owned_storage_backend as storage
        require(container.get('Id') == self.container_id and container.get('Name') == '/' + self.name
                and container.get('Image') == storage.IMAGE_ID
                and container.get('Config', {}).get('Image') == storage.IMAGE
                and container.get('Config', {}).get('Labels', {}).get('financial.oracle.lease') == self.context.lease_id,
                'Exact oracle ownership differs')
        created = datetime.fromisoformat(container['Created'].replace('Z', '+00:00'))
        require(self.issued <= created <= datetime.now(timezone.utc)
                and (self.created is None or container['Created'] == self.created), 'Oracle generation differs')
        state, host = container.get('State', {}), container.get('HostConfig', {})
        require(state.get('Running') is False and state.get('Paused') is False
                and state.get('Restarting') is False and state.get('Pid') == 0
                and state.get('Status') == 'created'
                and state.get('StartedAt') == '0001-01-01T00:00:00Z'
                and container.get('RestartCount') == 0 and state.get('Status') == 'created'
                and state.get('StartedAt') == '0001-01-01T00:00:00Z', 'Oracle must never be a running host')
        require(host.get('NetworkMode') == 'none' and host.get('ReadonlyRootfs') is True
                and host.get('CapDrop') == ['ALL'] and not host.get('CapAdd')
                and host.get('Memory') == 64 * 1024 * 1024 and host.get('NanoCpus') == 250_000_000
                and 'no-new-privileges:true' in (host.get('SecurityOpt') or [])
                and host.get('Privileged') is False and host.get('AutoRemove') is False
                and not any(host.get(key) for key in ('Binds', 'Devices', 'Tmpfs', 'PortBindings'))
                and not container.get('Mounts') and not container.get('Config', {}).get('Volumes'),
                'Oracle isolation policy differs')

    @oracle_operation
    def qualify(self):
        require(ORACLE is self and not self.finished and not self.failure and not self.create_attempted,
                'One clean current oracle owner required')
        import owned_storage_backend as storage
        import scanner_docker_command as runner
        import resource
        try:
            self.docker('pull', storage.IMAGE, timeout=120)
            self.layers = checked_image(self.inspect('image', storage.IMAGE), storage.IMAGE, storage.IMAGE_ID)
            base = Path(os.environ['RUNNER_TEMP']).resolve()
            require(base.is_absolute() and base.is_dir(), 'Owned runner temporary root required')
            self.directory = base / self.name
            # Declare the exact target before allocation; never search/delete by prefix.
            self.directory.mkdir(mode=0o700)
            directory_stat = self.directory.stat()
            self.directory_identity = (directory_stat.st_dev, directory_stat.st_ino)
            self.path = self.directory / 'image-executable'
            self.fd = os.open(self.path, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            initial = os.fstat(self.fd)
            self.file_identity = (initial.st_dev, initial.st_ino)
            self.issued = datetime.now(timezone.utc)
            self.create_attempted = True
            candidate = self.docker('create', '--name', self.name, '--network', 'none', '--read-only',
                '--memory', '64m', '--cpus', '0.25', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
                '--label', 'financial.oracle.lease=' + self.context.lease_id,
                '--entrypoint', '/bin/fake-gcs-server', storage.IMAGE, timeout=10)
            require(re.fullmatch('[0-9a-f]{64}', candidate) is not None, 'Exact oracle container ID required')
            self.container_id = candidate
            observed = self.inspect('container', self.container_id)
            self.require_container(observed)
            self.created = observed['Created']
            require(threading.active_count() == 1, 'Exclusive file-size cap fence required')
            prior = resource.getrlimit(resource.RLIMIT_FSIZE)
            self.rlimit_prior = prior
            self.cap_restore_required = True
            self.rlimit_prior = prior
            self.cap_restore_required = True
            soft = LIMIT if prior[0] == resource.RLIM_INFINITY else min(prior[0], LIMIT)
            try:
                resource.setrlimit(resource.RLIMIT_FSIZE, (soft, prior[1]))
                self.docker('cp', self.container_id + ':/bin/fake-gcs-server', str(self.path), timeout=30)
            finally:
                resource.setrlimit(resource.RLIMIT_FSIZE, prior)
                require(resource.getrlimit(resource.RLIMIT_FSIZE) == prior, 'File cap restoration differs')
                self.cap_restore_required = False
            ledger = runner.command_receipts()
            require(ledger and ledger[-1]['cleanupVerified'] is True and ledger[-1]['quarantined'] is False,
                    'Original copy helper must be settled before file observation')
            current, linked = os.fstat(self.fd), self.path.lstat()
            require(stat.S_ISREG(current.st_mode) and stat.S_ISREG(linked.st_mode)
                    and (current.st_dev, current.st_ino) == self.file_identity == (linked.st_dev, linked.st_ino)
                    and 0 < current.st_size <= LIMIT, 'Owned finite image file differs')
            result = hashlib.sha256()
            os.lseek(self.fd, 0, os.SEEK_SET)
            count = 0
            while True:
                block = os.read(self.fd, min(65536, LIMIT + 1 - count))
                if not block:
                    break
                count += len(block)
                require(count <= LIMIT, 'Image file cap exceeded')
                result.update(block)
            after = os.fstat(self.fd)
            require(count == current.st_size == after.st_size and after.st_mtime_ns == current.st_mtime_ns,
                    'Image file changed during bounded read')
            self.require_container(self.inspect('container', self.container_id))
            require(checked_image(self.inspect('image', storage.IMAGE_ID), storage.IMAGE, storage.IMAGE_ID) == self.layers,
                    'Immutable layer association changed')
            digest = result.hexdigest()
            self.close()
            require(self.finished, 'Oracle owner cleanup incomplete')
            return digest
        except BaseException:
            self.failure = True
            raise

    @oracle_operation
    def close(self):
        global ORACLE
        if self.finished:
            require(not self.failure, 'Original oracle failure remains sticky')
            return
        require(ORACLE is self, 'Current retained oracle owner required')
        import scanner_docker_command as runner
        errors = []
        if self.cap_restore_required:
            try:
                import resource
                resource.setrlimit(resource.RLIMIT_FSIZE, self.rlimit_prior)
                require(resource.getrlimit(resource.RLIMIT_FSIZE) == self.rlimit_prior,
                        'Original file cap restoration differs')
                self.cap_restore_required = False
            except BaseException as error:
                errors.append(error)
        # An unresolved helper may still write the file: retain it rather than
        # unlinking/closing its independent owner underneath active copy.
        ledger = runner.command_receipts()
        helpers_settled = all(row['cleanupVerified'] is True and row['quarantined'] is False for row in ledger)
        try:
            require(helpers_settled, 'Original CLI owners remain quarantined')
            if self.create_attempted and self.container_id is None:
                ids = self.docker('ps', '-a', '--no-trunc', '--filter', 'name=^/' + self.name + '$',
                    '--filter', 'label=financial.oracle.lease=' + self.context.lease_id, '--format', '{{.ID}}', timeout=3).splitlines()
                require(len(ids) <= 1, 'Ambiguous oracle allocation')
                if ids:
                    require(re.fullmatch('[0-9a-f]{64}', ids[0]) is not None, 'Exact recovered oracle ID required')
                    self.container_id = ids[0]
            if self.container_id is not None:
                ids = self.docker('ps', '-a', '--no-trunc', '--filter', 'id=' + self.container_id,
                    '--format', '{{.ID}}', timeout=3).splitlines()
                require(ids in ([], [self.container_id]), 'Oracle exact census differs')
                if ids:
                    self.require_container(self.inspect('container', self.container_id))
                    self.docker('rm', self.container_id, timeout=5)
                    require(self.docker('ps', '-a', '--no-trunc', '--filter', 'id=' + self.container_id,
                        '--format', '{{.ID}}', timeout=3).strip() == '', 'Oracle absence not verified')
                self.container_id = None
        except BaseException as error:
            errors.append(error)
        if helpers_settled:
            def close_file_fd():
                if self.fd is not None:
                    require(not self.fd_close_attempted, 'Original file close remains uncertain')
                    current = os.fstat(self.fd)
                    require((current.st_dev, current.st_ino) == self.file_identity, 'Original file descriptor differs')
                    self.fd_close_attempted = True
                    os.close(self.fd)
                    self.fd = None
            def unlink_file():
                if self.path is not None:
                    try:
                        current = self.path.lstat()
                    except FileNotFoundError:
                        current = None
                    if current is not None:
                        require(stat.S_ISREG(current.st_mode)
                                and (current.st_dev, current.st_ino) == self.file_identity, 'Owned file path differs')
                        self.path.unlink()
            def remove_directory():
                if self.directory is not None:
                    try:
                        current = self.directory.lstat()
                    except FileNotFoundError:
                        current = None
                    if current is not None:
                        require(stat.S_ISDIR(current.st_mode)
                                and (current.st_dev, current.st_ino) == self.directory_identity, 'Owned directory differs')
                        self.directory.rmdir()
                    self.directory = None
            for action in (close_file_fd, unlink_file, remove_directory):
                try:
                    action()
                except BaseException as error:
                    errors.append(error)
        if errors:
            self.failure = True
            raise BridgeRefused('Oracle owner cleanup incomplete; retained handles required') from None
        self.finished = True
        ORACLE = None
        require(not self.failure, 'Original oracle failure remains sticky')


def qualify_image_file(context):
    """Root callable retains oracle across every admitted failure and cleanup attempt."""
    owner = PinnedImageOracle(context)
    failures = []
    digest = None
    try:
        digest = owner.qualify()
    except BaseException as error:
        failures.append(error)
    if not owner.finished:
        try:
            owner.close()
        except BaseException as error:
            failures.append(error)
    if failures:
        raise BridgeRefused('Independent image oracle refused; retained owner must be fenced') from None
    return digest
