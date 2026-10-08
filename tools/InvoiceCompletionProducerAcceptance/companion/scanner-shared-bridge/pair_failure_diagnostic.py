"""Fixed first-failure projection only; never an owner or cleanup receipt.
All strings are source-controlled allowlist members. Exception text is never read.
Original owners and their failure fences remain solely responsible for custody.
"""
import re

STAGES = ('unentered', 'public-worker', 'oracle-acquire', 'oracle-pull', 'oracle-image-metadata', 'oracle-private-file', 'oracle-create', 'oracle-isolation', 'oracle-copy', 'oracle-read', 'oracle-image-recheck', 'oracle-release', 'pair-acquire', 'scanner-start', 'scanner-bridge-acquire', 'storage-image', 'storage-create', 'storage-start', 'storage-process', 'bridge-borrow', 'storage-observe', 'pair-release', 'public-ledger', 'public-projection', 'raw-git')
GUARDS = {'Actual backend observation missing': 'guard-dc8a14b774e7', 'Actual bounded CLI ledger required': 'guard-5c4b2d4db9b5', 'Actual live backend kernel birth required': 'guard-baf16da965a0', 'Actual successful absence probe required': 'guard-33c420749544', 'Ambiguous oracle allocation': 'guard-8cb98bc58b69', 'Backend absence not observed': 'guard-f31fa2d16bfe', 'Backend allocation generation differs': 'guard-945a68f1f28c', 'Backend census differs': 'guard-7301e1938fde', 'Backend start generation differs': 'guard-88709483a035', 'Borrowed backend census differs': 'guard-bf471f5218e1', 'Bounded source-owned associations required': 'guard-822a35db2c10', 'Clean current retained pair owner required': 'guard-22d445786082', 'Current retained oracle owner required': 'guard-8e05c1b7dff8', 'Current retained pair owner required': 'guard-16933c837bb9', 'Exact backend ownership required': 'guard-b1d65e62901d', 'Exact created backend ID required': 'guard-5632985fde2c', 'Exact oracle container ID required': 'guard-363be4e499e9', 'Exact oracle ownership differs': 'guard-639ab1cbdd43', 'Exact recovered oracle ID required': 'guard-8f25f83ff802', 'Exact stopped backend required': 'guard-4258c2d25405', 'Exact unique discovery association required': 'guard-b755ec9d3a54', 'Exclusive file-size cap fence required': 'guard-b882594844f0', 'Exclusive original hosted image oracle required': 'guard-3f9a8f18e064', 'File cap restoration differs': 'guard-b38c50a9e985', 'Fresh independently qualified worker required': 'guard-0ba06455d414', 'Image file cap exceeded': 'guard-3b12c2f14fa2', 'Image file changed during bounded read': 'guard-2d2fa9c70ee7', 'Image qualification refused; retained oracle requires living owner': 'guard-90dcb7722911', 'Immutable layer association changed': 'guard-fa5afdbea9cc', 'Immutable volume-free image differs': 'guard-6a2a14437738', 'Independent executable expectation required': 'guard-7989bf8d605e', 'One actual bridge subnet required': 'guard-132d7b108c98', 'One actual oracle object required': 'guard-3b957a64c218', 'One clean current oracle owner required': 'guard-c0f95fc14644', 'One clean pair start required': 'guard-04841cf6b044', 'One exact Engine object required': 'guard-5a835afc98ca', 'One retained pair owner required': 'guard-f107e334cdee', 'One settled original staging writer required': 'guard-2f0488fc2790', 'Oracle absence not verified': 'guard-5bc7f7f7ea06', 'Oracle exact census differs': 'guard-ce311d31b68f', 'Oracle generation differs': 'guard-2f7404df26cf', 'Oracle isolation policy differs': 'guard-65d5f83b2709', 'Oracle must never be a running host': 'guard-e6f06d43ce4b', 'Oracle owner cleanup incomplete': 'guard-350a8c91a2c5', 'Oracle staging target must be absent before writer': 'guard-4caadc41076d', 'Oracle staging writer must complete successfully': 'guard-66a30cd6aee6', 'Original CLI cleanup attempts refused': 'guard-3eb7f7df5d64', 'Original CLI lifecycle ledger refused': 'guard-335adbcd0069', 'Original CLI owners remain quarantined': 'guard-87f965e2f4d7', 'Original copy helper must be settled before file observation': 'guard-4b06738d5d5b', 'Original descriptor write did not progress': 'guard-e6387c24d816', 'Original empty destination descriptor differs': 'guard-992cf589e2e5', 'Original file cap restoration differs': 'guard-4d23142949cd', 'Original file close remains uncertain': 'guard-69ae9d37e0b0', 'Original file descriptor differs': 'guard-49a357cd8a35', 'Original hosted context required': 'guard-32ef6b783c3b', 'Original oracle failure remains sticky': 'guard-c291a7bfb54d', 'Original pair failure remains sticky': 'guard-b8ff86f6e65c', 'Original private staging parent differs': 'guard-c8ea2321cdae', 'Original scanner cleanup not verified': 'guard-5b7db7b833d4', 'Original staging close remains uncertain': 'guard-103f81f24319', 'Original staging descriptor differs': 'guard-2ed693f7e850', 'Owned bounded staging file differs': 'guard-e4589b7970ce', 'Owned directory differs': 'guard-d95f8d3e3db6', 'Owned file path differs': 'guard-2f49b067be1b', 'Owned finite image file differs': 'guard-0e8029404a4a', 'Owned runner temporary root required': 'guard-13540a79b850', 'Owned staging cleanup identity differs': 'guard-8595b96a06e9', 'Pair qualification refused; retained resources require living owner': 'guard-e29f80de7151', 'Private IPv4 bridge required': 'guard-7c7a580f1784', 'Qualified volume-free backend image differs': 'guard-df58fa4cefa7', 'Recovered exact backend ID required': 'guard-ba3b8dedd447', 'Reviewed owner command seam unavailable': 'guard-1eca28e6b28b', 'Staging CLI owners remain quarantined': 'guard-c1c98e9c3e68', 'Staging copy byte bound exceeded': 'guard-fffc9648cdf4', 'Staging file changed during bounded transfer': 'guard-fc9ec869d05a', 'Uncertain allocation is ambiguous': 'guard-87a4d4b1048a'}
CATEGORIES = ('source-guard', 'owner-refusal', 'cli-nonzero', 'os-failure', 'interrupted', 'unclassified')


def expected_code(module_code, qualified_name):
    import types
    current = module_code
    for name in qualified_name.split('.'):
        matches = tuple(value for value in current.co_consts
                        if type(value) is types.CodeType and value.co_name == name)
        if len(matches) != 1:
            raise ValueError('Qualified source code path refused')
        current = matches[0]
    return current


def same_constant(expected, actual, depth=0):
    import types
    if depth > 32 or type(expected) is not type(actual):
        return False
    if type(expected) is types.CodeType:
        return same_code(expected, actual, depth+1)
    if type(expected) is tuple:
        return len(expected) == len(actual) and all(same_constant(a,b,depth+1) for a,b in zip(expected,actual))
    if type(expected) is slice:
        return all(same_constant(a,b,depth+1) for a,b in zip(
            (expected.start,expected.stop,expected.step),
            (actual.start,actual.stop,actual.step)))
    if type(expected) is frozenset:
        primitives=(str,bytes,int,float,complex,bool,type(None))
        return len(expected)==len(actual) and all(type(value) in primitives for value in expected) and all(type(value) in primitives for value in actual) and expected==actual
    if type(expected) in (str,bytes,int,float,complex,bool,type(None)):
        return expected == actual
    return False


def same_code(expected, actual, depth=0):
    import types
    if depth > 32 or type(expected) is not types.CodeType or type(actual) is not types.CodeType:
        return False
    # Filename is deliberately excluded. Line/exception tables and all semantic
    # code fields are compared; no code executes and no frame globals are read.
    fields=('co_argcount','co_posonlyargcount','co_kwonlyargcount','co_nlocals',
            'co_stacksize','co_flags','co_code','co_consts','co_names','co_varnames',
            'co_freevars','co_cellvars','co_name','co_qualname','co_firstlineno',
            'co_linetable','co_exceptiontable')
    return all(same_constant(getattr(expected,name),getattr(actual,name),depth+1) for name in fields)


# Exact qualified source table, not runtime exception text or supplied metadata.
H_SOURCE_SHA256 = '58c1050a64b84096b84800a567fb7054d4915dd9f611ac1698b90513a392b26f'
H_DECLARATIONS = {'require': 37, '_record_handled_discovery': 234, 'command': 265, 'ScannerPlan.validate': 461, 'scanner_process': 479, 'process_start_ticks': 683}
H_SITES = (('require', 39, 39, 'h-raise-291678113aa7', 'direct-raise'), ('_record_handled_discovery', 241, 242, 'h-guard-0bc651b9b5e3', 'require-call'), ('_record_handled_discovery', 244, 244, 'h-guard-6b2e2d214598', 'require-call'), ('_record_handled_discovery', 246, 247, 'h-guard-bbff7d5cb380', 'require-call'), ('_record_handled_discovery', 249, 250, 'h-guard-71240921a90c', 'require-call'), ('_record_handled_discovery', 252, 259, 'h-guard-dd614e4ff8c6', 'require-call'), ('_record_handled_discovery', 260, 261, 'h-guard-dc4b74dfbc6b', 'require-call'), ('command', 267, 267, 'h-guard-cbf9c032c82f', 'require-call'), ('command', 268, 269, 'h-guard-f07463149642', 'require-call'), ('command', 270, 271, 'h-guard-da7e7227c0e7', 'require-call'), ('command', 290, 290, 'h-guard-17fbc07da869', 'require-call'), ('command', 325, 325, 'h-guard-f5f11706b2c6', 'require-call'), ('command', 327, 327, 'h-guard-138a5f2a6abc', 'require-call'), ('command', 330, 330, 'h-raise-1bd1ff222b04', 'direct-raise'), ('command', 295, 296, 'h-guard-8c782c3f5da6', 'require-call'), ('command', 313, 313, 'h-guard-f5f11706b2c6', 'require-call'), ('command', 322, 322, 'h-guard-17fbc07da869', 'require-call'), ('command', 340, 340, 'h-guard-dde83516eb10', 'require-call'), ('ScannerPlan.validate', 462, 465, 'h-guard-09a587e87f0d', 'require-call'), ('ScannerPlan.validate', 466, 468, 'h-guard-1337d7318a2e', 'require-call'), ('ScannerPlan.validate', 469, 470, 'h-guard-08af4d97e841', 'require-call'), ('ScannerPlan.validate', 471, 476, 'h-guard-aa2372243d6d', 'require-call'), ('scanner_process', 481, 481, 'h-guard-9998ae78f9cf', 'require-call'), ('scanner_process', 483, 485, 'h-guard-e869493280c4', 'require-call'), ('scanner_process', 499, 499, 'h-guard-ac720b813ea9', 'require-call'), ('scanner_process', 523, 523, 'h-guard-a1b902a0d750', 'require-call'), ('scanner_process', 526, 526, 'h-guard-f1a2dda65112', 'require-call'), ('scanner_process', 528, 528, 'h-guard-21bdc7a04e96', 'require-call'), ('scanner_process', 530, 530, 'h-guard-d312debbb7ed', 'require-call'), ('scanner_process', 532, 532, 'h-guard-5f51911e9102', 'require-call'), ('scanner_process', 533, 534, 'h-guard-29e9a9ad59fb', 'require-call'), ('scanner_process', 535, 536, 'h-guard-061f10216560', 'require-call'), ('scanner_process', 538, 538, 'h-guard-e5a56d741fc6', 'require-call'), ('scanner_process', 543, 543, 'h-guard-30213404c611', 'require-call'), ('scanner_process', 545, 546, 'h-guard-d6a7d472d991', 'require-call'), ('scanner_process', 547, 548, 'h-guard-1c1dabdcbd23', 'require-call'), ('scanner_process', 549, 550, 'h-guard-4d37839334df', 'require-call'), ('scanner_process', 492, 492, 'h-guard-6bbfaca7ef9b', 'require-call'), ('scanner_process', 542, 542, 'h-guard-e383c8afd27f', 'require-call'), ('scanner_process', 495, 495, 'h-guard-a64dd22724dd', 'require-call'), ('scanner_process', 509, 509, 'h-guard-d021ad0df7cf', 'require-call'), ('scanner_process', 497, 497, 'h-guard-ca8698020d94', 'require-call'), ('scanner_process', 515, 515, 'h-guard-b3cd135bee51', 'require-call'), ('scanner_process', 520, 521, 'h-guard-c82a446ffe22', 'require-call'), ('process_start_ticks', 684, 684, 'h-guard-24e4c71ae932', 'require-call'), ('process_start_ticks', 689, 690, 'h-guard-31cf144b9e68', 'require-call'), ('process_start_ticks', 693, 693, 'h-raise-810032268ad7', 'direct-raise'))
H_SITE_TABLE_SHA256 = 'c00aba56c681457c7dc4a52a7d17ae601d5a2dc34be8b847d96e1e9562dffdc2'
H_CAPTURE = None
TRACE_LIMIT = 32


def capture_h_sites(resources, source):
    global H_CAPTURE
    import hashlib
    import types
    if H_CAPTURE is not None or type(resources) is not types.ModuleType or type(source) is not bytes or hashlib.sha256(source).hexdigest() != H_SOURCE_SHA256:
        raise ValueError('Qualified owner source association refused')
    expected_module = compile(source, '<qualified-source-structure>', 'exec', dont_inherit=True)
    captured = {}
    for name, line in H_DECLARATIONS.items():
        value = resources.ScannerPlan.validate if name == 'ScannerPlan.validate' else getattr(resources, name)
        if type(value) is not types.FunctionType or value.__globals__ is not resources.__dict__ or value.__code__.co_firstlineno != line or not same_code(expected_code(expected_module,name),value.__code__):
            raise ValueError('Qualified owner declaration differs')
        captured[name] = value.__code__
    H_CAPTURE = (resources.AdmissionError, tuple(captured.items()))


B_SOURCE_SHA256 = '751963c8a55666577b7802ecb1ce67ab651cf1c6b05373e02a57a5d91367f6d5'
B_DECLARATIONS = {'require': 18, 'validate_image_chain': 34, 'validate_network': 49, 'BorrowedScannerBridge.__init__': 69, 'BorrowedScannerBridge._inspect': 94, 'BorrowedScannerBridge._relay': 100, 'BorrowedScannerBridge._refresh_members': 120}
B_SITES = (('require', 20, 20, 'bridge-raise-ec023fc96397', 'direct-raise'), ('validate_image_chain', 35, 35, 'bridge-guard-16c9dc7f4a64', 'require-call'), ('validate_image_chain', 36, 38, 'bridge-guard-0cd016f948a6', 'require-call'), ('validate_image_chain', 39, 40, 'bridge-guard-1f0aabb1a6da', 'require-call'), ('validate_image_chain', 43, 45, 'bridge-guard-25d16413de65', 'require-call'), ('validate_image_chain', 46, 46, 'bridge-guard-c01b0d51e5e2', 'require-call'), ('validate_network', 50, 54, 'bridge-guard-180e75f2be95', 'require-call'), ('validate_network', 55, 56, 'bridge-guard-a4e2735b50f0', 'require-call'), ('BorrowedScannerBridge.__init__', 72, 73, 'bridge-guard-97216e6dd872', 'require-call'), ('BorrowedScannerBridge.__init__', 74, 75, 'bridge-guard-d4b6625b6ccf', 'require-call'), ('BorrowedScannerBridge.__init__', 77, 78, 'bridge-guard-95e6f8e63b31', 'require-call'), ('BorrowedScannerBridge._inspect', 96, 97, 'bridge-guard-074efd88ecc0', 'require-call'), ('BorrowedScannerBridge._relay', 102, 102, 'bridge-guard-426bb2251498', 'require-call'), ('BorrowedScannerBridge._relay', 103, 103, 'bridge-guard-a78765adbafb', 'require-call'), ('BorrowedScannerBridge._relay', 104, 104, 'bridge-guard-0c58eebf9ef1', 'require-call'), ('BorrowedScannerBridge._relay', 105, 105, 'bridge-guard-c49b246f383b', 'require-call'), ('BorrowedScannerBridge._relay', 106, 106, 'bridge-guard-0175235ba001', 'require-call'), ('BorrowedScannerBridge._relay', 107, 107, 'bridge-guard-15d29a932c3a', 'require-call'), ('BorrowedScannerBridge._relay', 108, 108, 'bridge-guard-18d8ea9d1deb', 'require-call'), ('BorrowedScannerBridge._relay', 110, 113, 'bridge-guard-7e388dbcd8b1', 'require-call'), ('BorrowedScannerBridge._relay', 116, 117, 'bridge-guard-cc907fc212c8', 'require-call'), ('BorrowedScannerBridge._refresh_members', 122, 126, 'bridge-guard-c6961a9122c2', 'require-call'), ('BorrowedScannerBridge._refresh_members', 129, 134, 'bridge-guard-2715b8b8abd7', 'require-call'))
B_SITE_TABLE_SHA256 = 'eb6811b7ab5fa34897ae1e0d11f81231977bdf9dce05da1fd3911aa34c10b548'
B_CAPTURE = None

def capture_bridge_sites(resources, source):
    global B_CAPTURE
    import hashlib
    import types
    if B_CAPTURE is not None or type(resources) is not types.ModuleType or type(source) is not bytes or hashlib.sha256(source).hexdigest() != B_SOURCE_SHA256:
        raise ValueError('Qualified owner source association refused')
    expected_module = compile(source, '<qualified-source-structure>', 'exec', dont_inherit=True)
    captured = {}
    for name, line in B_DECLARATIONS.items():
        value = getattr(resources.BorrowedScannerBridge, name.split('.')[1]) if name.startswith('BorrowedScannerBridge.') else getattr(resources, name)
        if type(value) is not types.FunctionType or value.__globals__ is not resources.__dict__ or value.__code__.co_firstlineno != line or not same_code(expected_code(expected_module,name),value.__code__):
            raise ValueError('Qualified owner declaration differs')
        captured[name] = value.__code__
    B_CAPTURE = (resources.BridgeRefused, tuple(captured.items()))


def owner_clause(error):
    # The caller captures the ORIGINAL admitted namespace before any actors.
    # No frame locals/globals/filenames or exception text are read.
    if H_CAPTURE is not None and type(error) is H_CAPTURE[0]:
        capture, source_sites = H_CAPTURE, H_SITES
    elif B_CAPTURE is not None and type(error) is B_CAPTURE[0]:
        capture, source_sites = B_CAPTURE, B_SITES
    else:
        return 'unclassified-source-clause'
    codes = dict(capture[1])
    trace = error.__traceback__
    frames = []
    while trace is not None:
        if len(frames) == TRACE_LIMIT:
            return 'unclassified-source-clause'
        frames.append((trace.tb_frame.f_code, trace.tb_lineno))
        trace = trace.tb_next
    if not frames:
        return 'unclassified-source-clause'
    final_code, final_line = frames[-1]
    # Direct source-owned raises (for example command's sanitized nonzero) have
    # their own finite locations, without treating a failure as handled success.
    for name, start, end, clause, kind in source_sites:
        if kind == 'direct-raise' and name != 'require' and codes[name] is final_code and start <= final_line <= end:
            return clause
    if final_code is not codes['require'] or len(frames) < 2:
        return 'unclassified-source-clause'
    original_raise = tuple(site for site in source_sites if site[0] == 'require' and site[4] == 'direct-raise')
    if not any(start <= final_line <= end for _, start, end, _, _ in original_raise):
        return 'unclassified-source-clause'
    caller_code, caller_line = frames[-2]
    for name, start, end, clause, kind in source_sites:
        if kind == 'require-call' and codes[name] is caller_code and start <= caller_line <= end:
            return clause
    return 'unclassified-source-clause'

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
    if category not in CATEGORIES or clause not in tuple(GUARDS.values()) + tuple(site[3] for site in H_SITES+B_SITES) + ('unclassified-source-clause',):
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
    record(category, owner_clause(error))


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
