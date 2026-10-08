"""Fixed first-failure projection only; never an owner or cleanup receipt.
All strings are source-controlled allowlist members. Exception text is never read.
Original owners and their failure fences remain solely responsible for custody.
"""
import re

STAGES = ('unentered', 'public-worker', 'oracle-acquire', 'oracle-pull', 'oracle-image-metadata', 'oracle-private-file', 'oracle-create', 'oracle-isolation', 'oracle-copy', 'oracle-read', 'oracle-image-recheck', 'oracle-release', 'pair-acquire', 'scanner-start', 'storage-image', 'storage-create', 'storage-start', 'storage-process', 'bridge-borrow', 'storage-observe', 'pair-release', 'public-ledger', 'public-projection', 'raw-git')
GUARDS = {'Actual backend observation missing': 'guard-dc8a14b774e7', 'Actual bounded CLI ledger required': 'guard-5c4b2d4db9b5', 'Actual live backend kernel birth required': 'guard-baf16da965a0', 'Actual successful absence probe required': 'guard-33c420749544', 'Ambiguous oracle allocation': 'guard-8cb98bc58b69', 'Backend absence not observed': 'guard-f31fa2d16bfe', 'Backend allocation generation differs': 'guard-945a68f1f28c', 'Backend census differs': 'guard-7301e1938fde', 'Backend start generation differs': 'guard-88709483a035', 'Borrowed backend census differs': 'guard-bf471f5218e1', 'Bounded source-owned associations required': 'guard-822a35db2c10', 'Clean current retained pair owner required': 'guard-22d445786082', 'Current retained oracle owner required': 'guard-8e05c1b7dff8', 'Current retained pair owner required': 'guard-16933c837bb9', 'Exact backend ownership required': 'guard-b1d65e62901d', 'Exact created backend ID required': 'guard-5632985fde2c', 'Exact oracle container ID required': 'guard-363be4e499e9', 'Exact oracle ownership differs': 'guard-639ab1cbdd43', 'Exact recovered oracle ID required': 'guard-8f25f83ff802', 'Exact stopped backend required': 'guard-4258c2d25405', 'Exact unique discovery association required': 'guard-b755ec9d3a54', 'Exclusive file-size cap fence required': 'guard-b882594844f0', 'Exclusive original hosted image oracle required': 'guard-3f9a8f18e064', 'File cap restoration differs': 'guard-b38c50a9e985', 'Fresh independently qualified worker required': 'guard-0ba06455d414', 'Image file cap exceeded': 'guard-3b12c2f14fa2', 'Image file changed during bounded read': 'guard-2d2fa9c70ee7', 'Image qualification refused; retained oracle requires living owner': 'guard-90dcb7722911', 'Immutable layer association changed': 'guard-fa5afdbea9cc', 'Immutable volume-free image differs': 'guard-6a2a14437738', 'Independent executable expectation required': 'guard-7989bf8d605e', 'One actual bridge subnet required': 'guard-132d7b108c98', 'One actual oracle object required': 'guard-3b957a64c218', 'One clean current oracle owner required': 'guard-c0f95fc14644', 'One clean pair start required': 'guard-04841cf6b044', 'One exact Engine object required': 'guard-5a835afc98ca', 'One retained pair owner required': 'guard-f107e334cdee', 'One settled original staging writer required': 'guard-2f0488fc2790', 'Oracle absence not verified': 'guard-5bc7f7f7ea06', 'Oracle exact census differs': 'guard-ce311d31b68f', 'Oracle generation differs': 'guard-2f7404df26cf', 'Oracle isolation policy differs': 'guard-65d5f83b2709', 'Oracle must never be a running host': 'guard-e6f06d43ce4b', 'Oracle owner cleanup incomplete': 'guard-350a8c91a2c5', 'Oracle staging target must be absent before writer': 'guard-4caadc41076d', 'Oracle staging writer must complete successfully': 'guard-66a30cd6aee6', 'Original CLI cleanup attempts refused': 'guard-3eb7f7df5d64', 'Original CLI lifecycle ledger refused': 'guard-335adbcd0069', 'Original CLI owners remain quarantined': 'guard-87f965e2f4d7', 'Original copy helper must be settled before file observation': 'guard-4b06738d5d5b', 'Original descriptor write did not progress': 'guard-e6387c24d816', 'Original empty destination descriptor differs': 'guard-992cf589e2e5', 'Original file cap restoration differs': 'guard-4d23142949cd', 'Original file close remains uncertain': 'guard-69ae9d37e0b0', 'Original file descriptor differs': 'guard-49a357cd8a35', 'Original hosted context required': 'guard-32ef6b783c3b', 'Original oracle failure remains sticky': 'guard-c291a7bfb54d', 'Original pair failure remains sticky': 'guard-b8ff86f6e65c', 'Original private staging parent differs': 'guard-c8ea2321cdae', 'Original scanner cleanup not verified': 'guard-5b7db7b833d4', 'Original staging close remains uncertain': 'guard-103f81f24319', 'Original staging descriptor differs': 'guard-2ed693f7e850', 'Owned bounded staging file differs': 'guard-e4589b7970ce', 'Owned directory differs': 'guard-d95f8d3e3db6', 'Owned file path differs': 'guard-2f49b067be1b', 'Owned finite image file differs': 'guard-0e8029404a4a', 'Owned runner temporary root required': 'guard-13540a79b850', 'Owned staging cleanup identity differs': 'guard-8595b96a06e9', 'Pair qualification refused; retained resources require living owner': 'guard-e29f80de7151', 'Private IPv4 bridge required': 'guard-7c7a580f1784', 'Qualified volume-free backend image differs': 'guard-df58fa4cefa7', 'Recovered exact backend ID required': 'guard-ba3b8dedd447', 'Reviewed owner command seam unavailable': 'guard-1eca28e6b28b', 'Staging CLI owners remain quarantined': 'guard-c1c98e9c3e68', 'Staging copy byte bound exceeded': 'guard-fffc9648cdf4', 'Staging file changed during bounded transfer': 'guard-fc9ec869d05a', 'Uncertain allocation is ambiguous': 'guard-87a4d4b1048a'}
CATEGORIES = ('source-guard', 'owner-refusal', 'cli-nonzero', 'os-failure', 'interrupted', 'unclassified')
CURRENT_STAGE = 'unentered'
FIRST = None
BINDING = None


def stage(value):
    global CURRENT_STAGE
    if value not in STAGES:
        raise ValueError('Diagnostic stage refused')
    CURRENT_STAGE = value


def record(category, clause):
    global FIRST
    if category not in CATEGORIES or clause not in tuple(GUARDS.values()) + ('unclassified-source-clause',):
        raise ValueError('Diagnostic code refused')
    if FIRST is None:
        FIRST = (CURRENT_STAGE, category, clause)


def guard(message):
    record('source-guard', GUARDS.get(message, 'unclassified-source-clause'))


def failure(error):
    # Names determine fixed categories only; arbitrary names/text are never emitted.
    name = type(error).__name__
    category = {'BridgeRefused': 'owner-refusal', 'AdmissionError': 'owner-refusal',
                'CalledProcessError': 'cli-nonzero', 'OSError': 'os-failure',
                'FileNotFoundError': 'os-failure', 'PermissionError': 'os-failure',
                'KeyboardInterrupt': 'interrupted', 'SystemExit': 'interrupted'}.get(name, 'unclassified')
    record(category, 'unclassified-source-clause')


def bind(head, run, attempt, manifest_digest, mode):
    global BINDING
    if BINDING is not None or mode not in ('pair', 'raw-git') or re.fullmatch('[0-9a-f]{40}', head) is None or re.fullmatch('[0-9a-f]{64}', manifest_digest) is None or type(run) is not str or re.fullmatch('[1-9][0-9]{0,19}', run) is None or int(run) > 2**63-1 or type(attempt) is not int or not 0 < attempt <= 2**31-1:
        raise ValueError('Diagnostic association refused')
    BINDING = (head, run, attempt, manifest_digest, mode)


def projection():
    if FIRST is None or BINDING is None:
        raise ValueError('Actual bound first failure required')
    head, run, attempt, digest, mode = BINDING
    stage_code, category, clause = FIRST
    return {'schemaVersion': 1, 'sourceHead': head, 'runId': run, 'runAttempt': attempt,
            'sourceManifestSha256': digest, 'mode': mode, 'firstStage': stage_code,
            'firstCategory': category, 'firstDenialClause': clause,
            'diagnosticOnly': True, 'pairAccepted': False, 'cleanupAccepted': False,
            'fileRuntimeAccepted': False, 'genuineEightHostFinancialAccepted': False}
