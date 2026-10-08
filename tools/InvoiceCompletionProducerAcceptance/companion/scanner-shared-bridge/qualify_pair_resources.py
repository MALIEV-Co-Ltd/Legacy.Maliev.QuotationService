"""Root-owned callable for actual stopped-image oracle then standalone held pair.

Outer immutable module/source preflight MUST happen before this import/execution.
Caller must hold this worker alive if ORACLE/OWNER remain; no parent-death proof.
Public projection happens after independent cleanup, never before an owner fence.
"""
import pair_failure_diagnostic as diagnostic

from borrowed_scanner_bridge import BridgeRefused
import held_pair_launcher as launcher
import pinned_image_oracle as oracle_module


def require(ok, message):
    if not ok:
        diagnostic.guard(message)
        raise BridgeRefused(message)


def validate_original_command_ledger(rows, handled):
    require(type(rows) is tuple and 0 < len(rows) <= 256, 'Actual bounded CLI ledger required')
    positive = ('returnedProcessObserved', 'generationBound', 'originalReaped', 'bothReadersEof',
                'bothReadersClosed', 'selectorCloseCompleted', 'pidfdCloseCompleted', 'cleanupVerified')
    negative = ('quarantined', 'descendantCleanupProved', 'kernelCapsObserved', 'attemptLedgerCapped')
    require(type(handled) is tuple and len(handled) <= 256, 'Bounded source-owned associations required')
    associated = {}
    for item in handled:
        require(type(item) is tuple and len(item) == 3 and type(item[0]) is int and type(item[1]) is int
                and item[2] == 'vanished-nonowner-read-query' and 0 <= item[0] < item[1] < len(rows)
                and item[0] not in associated, 'Exact unique discovery association required')
        associated[item[0]] = item[1]
    for index, row in enumerate(rows):
        require(all(row.get(key) is True for key in positive)
                and all(row.get(key) is False for key in negative)
                and type(row.get('originalExitCode')) is int
                and ((index in associated and row.get('originalFailure') is True and row['originalExitCode'] > 0)
                     or (index not in associated and row.get('originalFailure') is False and row['originalExitCode'] == 0)),
                'Original CLI lifecycle ledger refused')
        attempts = row.get('cleanupAttempts')
        require(type(attempts) is tuple and 0 < len(attempts) <= 64
                and all(type(item) is tuple and len(item) == 2 and item[1] == 'completed' for item in attempts),
                'Original CLI cleanup attempts refused')

    for inventory_index in associated.values():
        require(inventory_index not in associated and rows[inventory_index].get('originalFailure') is False
                and type(rows[inventory_index].get('originalExitCode')) is int
                and rows[inventory_index]['originalExitCode'] == 0, 'Actual successful absence probe required')


def qualify_pair(context):
    import scanner_docker_command as runner
    import owned_storage_backend as storage
    require(runner.command_receipts() == () and oracle_module.ORACLE is None and launcher.OWNER is None,
            'Fresh independently qualified worker required')
    diagnostic.stage('oracle-acquire')
    image_owner = oracle_module.PinnedImageOracle(context)
    errors = []
    expected = None
    try:
        expected = image_owner.qualify()
    except BaseException as error:
        diagnostic.failure(error)
        image_owner.failure = True
        errors.append(error)
    finally:
        if not image_owner.finished:
            try:
                image_owner.close()
            except BaseException as error:
                diagnostic.failure(error)
                errors.append(error)
    require(not errors and image_owner.finished and oracle_module.ORACLE is None,
            'Image qualification refused; retained oracle requires living owner')
    diagnostic.stage('pair-acquire')
    owner = launcher.HeldPairLauncher(context, expected)
    private = None
    try:
        owner.start()
        private = owner.observe()
        require(private.get('privateBackendObserved') is True, 'Actual backend observation missing')
    except BaseException as error:
        diagnostic.failure(error)
        owner.failure = True
        errors.append(error)
    finally:
        try:
            owner.close()
        except BaseException as error:
            diagnostic.failure(error)
            errors.append(error)
    require(not errors and owner.finished and launcher.OWNER is None,
            'Pair qualification refused; retained resources require living owner')
    diagnostic.stage('public-ledger')
    rows = runner.command_receipts()
    import hosted_companion_resources as h
    handled = h.handled_discovery_queries()
    validate_original_command_ledger(rows, handled)
    return {'schemaVersion': 1, 'backendImageReference': storage.IMAGE, 'backendImageId': storage.IMAGE_ID,
            'independentImageExecutableSha256': expected, 'fileSource': context.file_sha,
            'handledReadQueryAssociations': handled, 'originalCliCommandsObserved': len(rows), 'originalCliCleanupObserved': True,
            'independentStoppedImageFileObserved': True, 'actualSharedBridgeAndBackendProcessObserved': True,
            'scopedOracleAndPairCleanupObserved': True, 'fileBoundaryAccepted': False,
            'frontNativeAccepted': False, 'financialBusinessGraphAccepted': False,
            'genuineEightHostFinancialAccepted': False, 'allChildFdCensusAccepted': False,
            'kernelCliCapsObserved': False, 'parentDeathCleanupProved': False}
