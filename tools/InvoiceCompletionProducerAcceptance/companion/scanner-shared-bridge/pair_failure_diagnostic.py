"""Fixed first-failure projection only; never an owner or cleanup receipt.
All emitted strings are source-controlled allowlist members. Formatted exception text is never read.
Source-qualified bounded stderr is inspected privately and never projected.
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


NETWORK_SOURCE_SHA256 = '9bc5ca10c4d872224a3f096aa305a1893d5b3edc89e3042141c90f3b448af33d'
NETWORK_SITES = (('configured_network_census', 141, 141, 'scanner-network-guard-2cb16c6f486e', 'ValueError', 'Configured network census inspect identity differs'), ('configured_network_census', 144, 144, 'scanner-network-guard-a6e3d5fb3523', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 147, 147, 'scanner-network-guard-877630af149f', 'ValueError', 'Configured network census driver differs'), ('configured_network_census', 149, 149, 'scanner-network-guard-0338d87e116a', 'ValueError', 'Configured network census unknown empty IPAM refused'), ('configured_network_census', 153, 153, 'scanner-network-guard-01e2d20df250', 'ValueError', 'Configured network census subnet shape differs'), ('configured_network_census', 156, 156, 'scanner-network-guard-f718864206c4', 'ValueError', 'Configured network census subnet differs'), ('configured_network_census', 162, 162, 'scanner-network-guard-726400773bb5', 'ValueError', 'Configured network census IPAM value differs'), ('configured_network_census', 166, 166, 'scanner-network-guard-f867d540644d', 'ValueError', 'Configured network census gateway differs'), ('configured_network_census', 170, 170, 'scanner-network-guard-d22aabe4c0b7', 'ValueError', 'Configured network census address range differs'), ('configured_network_census', 174, 174, 'scanner-network-guard-d1a237746ca5', 'ValueError', 'Configured network census changed before selection'), ('configured_network_census', 180, 180, 'scanner-network-guard-5a534e6cf4bb', 'ValueError', 'Configured network source candidate differs'), ('configured_network_census', 183, 183, 'scanner-network-guard-203e6061190e', 'ValueError', 'Configured network finite candidates exhausted'), ('configured_network_census.observed', 116, 116, 'scanner-network-guard-334d6f9b4133', 'TimeoutError', 'Configured network census deadline expired'), ('configured_network_census.observed', 119, 119, 'scanner-network-guard-b8ae81983907', 'TimeoutError', 'Configured network census deadline expired'), ('configured_network_census.observed', 121, 121, 'scanner-network-guard-7e9af2c3c599', 'ValueError', 'Configured network census output type differs'), ('configured_network_census.observed', 125, 125, 'scanner-network-guard-2136b999aba8', 'ValueError', 'Configured network census output bound exceeded'), ('configured_network_census.listed', 133, 133, 'scanner-network-guard-592095591b73', 'ValueError', 'Configured network census identity differs'), ('Scanner.validate_configured_network', 219, 219, 'scanner-network-guard-465d0ff76a70', 'ValueError', 'Original configured network plan required'), ('Scanner.validate_configured_network', 226, 226, 'scanner-network-guard-c3a3ac1bd8f9', 'ValueError', 'Configured network original ownership differs'), ('Scanner.validate_configured_network', 230, 230, 'scanner-network-guard-39d755f9f730', 'ValueError', 'Configured network generation changed'), ('Scanner.validate_configured_network', 232, 232, 'scanner-network-guard-3b957a47bb5c', 'ValueError', 'Configured network exact census differs'), ('Scanner.validate_configured_network', 236, 236, 'scanner-network-guard-a0b9285acfda', 'ValueError', 'Configured network prior admission failed'), ('Scanner.validate_configured_network', 249, 249, 'scanner-network-guard-e35eef59e833', 'ValueError', 'Configured network actual IPAM or isolation differs'), ('Scanner.start', 330, 330, 'scanner-network-guard-c30acc1c7fe6', 'ValueError', 'Pulled image digest not observed'), ('Scanner.start', 341, 341, 'scanner-network-guard-1ff631aabc32', 'ValueError', 'Invalid observed derived image identity'), ('Scanner.start', 344, 344, 'scanner-network-guard-57920f6ada6e', 'ValueError', 'Derived image ownership differs'), ('Scanner.start', 360, 360, 'scanner-network-guard-8b44c405e750', 'ValueError', 'Invalid owned network identity'), ('Scanner.start', 366, 366, 'scanner-network-guard-df936985f2c3', 'ValueError', 'One original configured network observation required'), ('Scanner.start', 379, 379, 'scanner-network-guard-46cbd54ccbcd', 'ValueError', 'Invalid observed created container identity'), ('Scanner.start', 387, 387, 'scanner-network-guard-72718f3fae76', 'ValueError', 'Actual scanner container is not running immediately after start'), ('Scanner.start', 398, 398, 'scanner-network-guard-fb25793c4983', 'ValueError', 'Runtime image or immutable root policy differs'), ('Scanner.start', 402, 402, 'scanner-network-guard-a4de6ff1d2ed', 'ValueError', 'Unexpected runtime mounts or persistent volumes'), ('Scanner.start', 405, 405, 'scanner-network-guard-f82e1dc3b803', 'ValueError', 'Dedicated network observation differs'), ('Scanner.start', 425, 425, 'scanner-network-guard-8906a5ab029a', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 430, 430, 'scanner-network-guard-434f936aaa47', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 433, 433, 'scanner-network-guard-a72fadacf1d7', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 436, 436, 'scanner-network-guard-6dfd480f244d', 'ValueError', 'Original startup PING result refused'), ('Scanner.start', 456, 456, 'scanner-network-guard-f6641f4db154', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 460, 460, 'scanner-network-guard-8d41df129ce3', 'ValueError', 'Engine/database readiness not observed'), ('Scanner.start', 475, 475, 'scanner-network-guard-47510acf8d63', 'ValueError', 'Loaded VERSION differs from observed daily database'), ('Scanner.start', 478, 478, 'scanner-network-guard-8ea2856fefa6', 'ValueError', 'Benign complete-file control failed'), ('Scanner.start', 480, 480, 'scanner-network-guard-8105029986f7', 'ValueError', 'EICAR complete-file control failed'), ('Scanner.start', 482, 482, 'scanner-network-guard-300d2232efd3', 'ValueError', 'Actual EICAR signature detection not observed'), ('Scanner.start', 485, 485, 'scanner-network-guard-8eaa8a84128a', 'ValueError', 'Scanner executable/config/databases changed during controls'), ('Scanner.start', 487, 487, 'scanner-network-guard-5e0d7c43dd28', 'ValueError', 'Scanner socket owner/generation changed during controls'))
NETWORK_SITE_TABLE_SHA256 = '877ff5fc70368224b3f353870513acb4816864c0df18627c6baf5106118898cd'
NETWORK_CAPTURE = None


def capture_network_sites(scanner, source):
    global NETWORK_CAPTURE
    import hashlib
    import types
    if (NETWORK_CAPTURE is not None or type(scanner) is not types.ModuleType
            or type(source) is not bytes or hashlib.sha256(source).hexdigest() != NETWORK_SOURCE_SHA256):
        raise ValueError('Qualified network diagnostic source refused')
    expected = compile(source, '<qualified-network-structure>', 'exec', dont_inherit=True)
    functions = {'configured_network_census': scanner.configured_network_census,
                 'Scanner.validate_configured_network': scanner.Scanner.validate_configured_network,
                 'Scanner.start': scanner.Scanner.start}
    captured = {}
    for name, function in functions.items():
        if (type(function) is not types.FunctionType or function.__globals__ is not scanner.__dict__
                or not same_code(expected_code(expected, name), function.__code__)):
            raise ValueError('Qualified network diagnostic declaration differs')
        captured[name] = function.__code__
    for name in ('observed', 'listed'):
        path = 'configured_network_census.' + name
        actual = expected_code(captured['configured_network_census'], name)
        if not same_code(expected_code(expected, path), actual):
            raise ValueError('Qualified network nested declaration differs')
        captured[path] = actual
    NETWORK_CAPTURE = (tuple(captured.items()), scanner.__dict__)


def network_clause(error):
    # Only original fixed literal raises; never infer a compound subpredicate.
    if CURRENT_STAGE != 'scanner-start' or NETWORK_CAPTURE is None or type(error) not in (ValueError, TimeoutError):
        return 'unclassified-source-clause'
    trace = BaseException.__traceback__.__get__(error, BaseException)
    frames = []
    while trace is not None:
        if len(frames) == TRACE_LIMIT:
            return 'unclassified-source-clause'
        frames.append((trace.tb_frame.f_code, trace.tb_frame.f_globals, trace.tb_lineno))
        trace = trace.tb_next
    if not frames:
        return 'unclassified-source-clause'
    code, namespace, line = frames[-1]
    codes, original_namespace = NETWORK_CAPTURE
    if namespace is not original_namespace:
        return 'unclassified-source-clause'
    codes = dict(codes)
    for name, first, last, clause, error_name, literal in NETWORK_SITES:
        if codes[name] is code and first <= line <= last:
            expected_type = ValueError if error_name == 'ValueError' else TimeoutError
            if type(error) is not expected_type:
                return 'unclassified-source-clause'
            arguments = BaseException.args.__get__(error, BaseException)
            if type(arguments) is not tuple or len(arguments) != 1 or type(arguments[0]) is not str or len(arguments[0]) != len(literal) or arguments[0] != literal:
                return 'unclassified-source-clause'
            return clause
    return 'unclassified-source-clause'



# This classifier is diagnostic only. The original runner propagates this exact
# error after cleanup; lifecycle quarantine errors never enter this lane.
STORAGE_SOURCE_SHA256 = {'launcher': '4fc19580f9b36063e9808efc87e18bd1aa0ee5db955c15a9fee2f65eb419a8d9', 'scanner': '9bc5ca10c4d872224a3f096aa305a1893d5b3edc89e3042141c90f3b448af33d', 'runner': '89daed400342a5d43a5c62bafbac50f024bf3ced95c6aa2d482d4e540b15a6d6'}
STORAGE_SITES = {'create': (151, 151), 'docker': (257, 257), 'propagate': (385, 385), 'original': (360, 361)}
STORAGE_CAPTURE = None
STORAGE_CLAUSE = 'docker-static-ip-requires-configured-subnet'
STORAGE_WRAPPED_CLAUSE = 'docker-wrapped-static-ip-requires-configured-subnet'
STORAGE_CREATE_WRAPPED_CLAUSE = 'docker-create-wrapped-static-ip-requires-configured-subnet'
STORAGE_CREATE_WRAPPED_PATTERN = 'Error response from daemon: invalid config for network [0-9a-f]{64}: invalid endpoint settings:\\nuser specified IP address is supported only when connecting to networks with user configured subnets\\n?'
STORAGE_WRAPPED_LINE = 'Error response from daemon: invalid endpoint settings:\nuser specified IP address is supported only when connecting to networks with user configured subnets'
STORAGE_DENIALS = ('storage-denial-source-not-captured', 'storage-denial-error-type', 'storage-denial-trace-limit', 'storage-denial-trace-short', 'storage-denial-frame-code', 'storage-denial-frame-globals', 'storage-denial-frame-line', 'storage-denial-stderr-type', 'storage-denial-stderr-bound', 'storage-denial-stderr-grammar')
STORAGE_ENGINE_LINE = 'Error response from daemon: user specified IP address is supported only when connecting to networks with user configured subnets'


def capture_storage_sites(launcher, scanner, runner, sources):
    global STORAGE_CAPTURE
    import hashlib
    import subprocess
    import types
    if STORAGE_CAPTURE is not None or type(sources) is not dict or set(sources) != set(STORAGE_SOURCE_SHA256):
        raise ValueError('Qualified storage source association refused')
    modules = {'launcher': launcher, 'scanner': scanner, 'runner': runner}
    declarations = (('launcher','HeldPairLauncher.start',launcher.HeldPairLauncher.start.__wrapped__),
                    ('scanner','Scanner.docker',scanner.Scanner.docker),
                    ('runner','run_docker',runner.run_docker),
                    ('runner','_run_fenced',runner._run_fenced))
    compiled = {}
    for name,module in modules.items():
        data = sources[name]
        if type(module) is not types.ModuleType or type(data) is not bytes or hashlib.sha256(data).hexdigest() != STORAGE_SOURCE_SHA256[name]:
            raise ValueError('Qualified storage source association refused')
        compiled[name] = compile(data, '<qualified-storage-structure>', 'exec', dont_inherit=True)
    codes = {}
    namespaces = {}
    for name,path,value in declarations:
        if type(value) is not types.FunctionType or value.__globals__ is not modules[name].__dict__ or not same_code(expected_code(compiled[name],path),value.__code__):
            raise ValueError('Qualified storage declaration differs')
        codes[path] = value.__code__
        namespaces[path] = value.__globals__
    if scanner.run_docker is not runner.run_docker or runner.subprocess is not subprocess:
        raise ValueError('Qualified storage route differs')
    STORAGE_CAPTURE = (subprocess.CalledProcessError,tuple(codes.items()),tuple(namespaces.items()))


def storage_clause(error):
    if CURRENT_STAGE != 'storage-create':
        return 'unclassified-source-clause'
    if STORAGE_CAPTURE is None:
        return 'storage-denial-source-not-captured'
    if type(error) is not STORAGE_CAPTURE[0]:
        return 'storage-denial-error-type'
    codes = dict(STORAGE_CAPTURE[1])
    namespaces = dict(STORAGE_CAPTURE[2])
    trace = error.__traceback__
    frames = []
    while trace is not None:
        if len(frames) == TRACE_LIMIT:
            return 'storage-denial-trace-limit'
        frames.append((trace.tb_frame.f_code,trace.tb_lineno,trace.tb_frame.f_globals))
        trace = trace.tb_next
    # Ordered original call route and the two _run_fenced raise sites. No caller
    # metadata or error text establishes provenance. Other intervening code is
    # allowed only outside this exact contiguous source-owned suffix.
    if len(frames) < 5:
        return 'storage-denial-trace-short'
    route = frames[-5:]
    paths = ('HeldPairLauncher.start','Scanner.docker','run_docker','_run_fenced','_run_fenced')
    ranges = (STORAGE_SITES['create'],STORAGE_SITES['docker'],None,STORAGE_SITES['propagate'],STORAGE_SITES['original'])
    for (code,line,namespace),path,bounds in zip(route,paths,ranges):
        if code is not codes[path]:
            return 'storage-denial-frame-code'
        if namespace is not namespaces[path]:
            return 'storage-denial-frame-globals'
        if bounds is not None and not bounds[0] <= line <= bounds[1]:
            return 'storage-denial-frame-line'
    # Exact original class uses the built-in dict; no descriptor, args, command,
    # exception formatting, frame locals/globals or private output is projected.
    data = BaseException.__dict__['__dict__'].__get__(error,type(error))
    value = dict.get(data,'stderr')
    if type(value) is not str:
        return 'storage-denial-stderr-type'
    if not 0 < len(value) < 4096:
        return 'storage-denial-stderr-bound'
    if re.fullmatch(STORAGE_CREATE_WRAPPED_PATTERN, value) is not None:
        return STORAGE_CREATE_WRAPPED_CLAUSE
    if value in (STORAGE_WRAPPED_LINE, STORAGE_WRAPPED_LINE+'\n'):
        return STORAGE_WRAPPED_CLAUSE
    if value not in (STORAGE_ENGINE_LINE,STORAGE_ENGINE_LINE+'\n',STORAGE_ENGINE_LINE+'.',STORAGE_ENGINE_LINE+'.\n'):
        return 'storage-denial-stderr-grammar'
    return STORAGE_CLAUSE

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
    if category not in CATEGORIES or clause not in tuple(GUARDS.values()) + tuple(site[3] for site in H_SITES+B_SITES+NETWORK_SITES) + (STORAGE_CLAUSE, STORAGE_WRAPPED_CLAUSE, STORAGE_CREATE_WRAPPED_CLAUSE, *STORAGE_DENIALS, 'unclassified-source-clause',):
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
    clause = storage_clause(error) if category == 'cli-nonzero' else owner_clause(error)
    if clause == 'unclassified-source-clause':
        clause = network_clause(error)
    record(category, clause)


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
