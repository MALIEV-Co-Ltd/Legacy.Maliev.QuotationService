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


NETWORK_SOURCE_SHA256 = '34f794ca4d05dcd8664fbb8a919975fd0864802bb0ecbd95edae9d67acbd63b6'
NETWORK_SITES = (('configured_network_census', 141, 141, 'scanner-network-guard-2cb16c6f486e', 'ValueError', 'Configured network census inspect identity differs'), ('configured_network_census', 144, 144, 'scanner-network-guard-a6e3d5fb3523', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 148, 148, 'scanner-network-guard-1b25d86798f0', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 156, 156, 'scanner-network-guard-e123f94ef90d', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 159, 159, 'scanner-network-guard-9956608cad52', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 161, 161, 'scanner-network-guard-41d9fe5a07b4', 'ValueError', 'Configured network census IPAM shape differs'), ('configured_network_census', 164, 164, 'scanner-network-guard-dd9f15d3778b', 'ValueError', 'Configured network census driver differs'), ('configured_network_census', 166, 166, 'scanner-network-guard-76e2d40580b4', 'ValueError', 'Configured network census unknown empty IPAM refused'), ('configured_network_census', 170, 170, 'scanner-network-guard-eaed2984b173', 'ValueError', 'Configured network census subnet shape differs'), ('configured_network_census', 173, 173, 'scanner-network-guard-c73bd7b51f03', 'ValueError', 'Configured network census subnet differs'), ('configured_network_census', 179, 179, 'scanner-network-guard-0ffcb0b97f70', 'ValueError', 'Configured network census IPAM value differs'), ('configured_network_census', 183, 183, 'scanner-network-guard-66b40c4377e7', 'ValueError', 'Configured network census gateway differs'), ('configured_network_census', 187, 187, 'scanner-network-guard-cbb516e12484', 'ValueError', 'Configured network census address range differs'), ('configured_network_census', 191, 191, 'scanner-network-guard-e40a4553f4a4', 'ValueError', 'Configured network census changed before selection'), ('configured_network_census', 197, 197, 'scanner-network-guard-1010331a3c78', 'ValueError', 'Configured network source candidate differs'), ('configured_network_census', 200, 200, 'scanner-network-guard-d0e8ce8daf5b', 'ValueError', 'Configured network finite candidates exhausted'), ('configured_network_census.observed', 116, 116, 'scanner-network-guard-334d6f9b4133', 'TimeoutError', 'Configured network census deadline expired'), ('configured_network_census.observed', 119, 119, 'scanner-network-guard-b8ae81983907', 'TimeoutError', 'Configured network census deadline expired'), ('configured_network_census.observed', 121, 121, 'scanner-network-guard-7e9af2c3c599', 'ValueError', 'Configured network census output type differs'), ('configured_network_census.observed', 125, 125, 'scanner-network-guard-2136b999aba8', 'ValueError', 'Configured network census output bound exceeded'), ('configured_network_census.listed', 133, 133, 'scanner-network-guard-592095591b73', 'ValueError', 'Configured network census identity differs'), ('Scanner.validate_configured_network', 236, 236, 'scanner-network-guard-1303c4b81deb', 'ValueError', 'Original configured network plan required'), ('Scanner.validate_configured_network', 243, 243, 'scanner-network-guard-1189c411d432', 'ValueError', 'Configured network original ownership differs'), ('Scanner.validate_configured_network', 247, 247, 'scanner-network-guard-27903677fe15', 'ValueError', 'Configured network generation changed'), ('Scanner.validate_configured_network', 249, 249, 'scanner-network-guard-ba82ad87d05e', 'ValueError', 'Configured network exact census differs'), ('Scanner.validate_configured_network', 253, 253, 'scanner-network-guard-2a4f94230e78', 'ValueError', 'Configured network prior admission failed'), ('Scanner.validate_configured_network', 266, 266, 'scanner-network-guard-a7df60c9c72c', 'ValueError', 'Configured network actual IPAM or isolation differs'), ('Scanner.start', 347, 347, 'scanner-network-guard-ca8f85be19ab', 'ValueError', 'Pulled image digest not observed'), ('Scanner.start', 358, 358, 'scanner-network-guard-b56ddcf44b56', 'ValueError', 'Invalid observed derived image identity'), ('Scanner.start', 361, 361, 'scanner-network-guard-58b2ce56eefe', 'ValueError', 'Derived image ownership differs'), ('Scanner.start', 377, 377, 'scanner-network-guard-1143b8ff2599', 'ValueError', 'Invalid owned network identity'), ('Scanner.start', 383, 383, 'scanner-network-guard-27bbb085ed1a', 'ValueError', 'One original configured network observation required'), ('Scanner.start', 396, 396, 'scanner-network-guard-67c0dac8be7f', 'ValueError', 'Invalid observed created container identity'), ('Scanner.start', 404, 404, 'scanner-network-guard-ae28410a5f14', 'ValueError', 'Actual scanner container is not running immediately after start'), ('Scanner.start', 415, 415, 'scanner-network-guard-d9c9fce3bc29', 'ValueError', 'Runtime image or immutable root policy differs'), ('Scanner.start', 419, 419, 'scanner-network-guard-cad050b06a6a', 'ValueError', 'Unexpected runtime mounts or persistent volumes'), ('Scanner.start', 422, 422, 'scanner-network-guard-70a293a6ac03', 'ValueError', 'Dedicated network observation differs'), ('Scanner.start', 442, 442, 'scanner-network-guard-3da28db77291', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 447, 447, 'scanner-network-guard-fa83b6afc7aa', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 450, 450, 'scanner-network-guard-b3675c7a634b', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 453, 453, 'scanner-network-guard-79b90767ef7b', 'ValueError', 'Original startup PING result refused'), ('Scanner.start', 473, 473, 'scanner-network-guard-1a608dc6196d', 'TimeoutError', 'Actual clamd readiness deadline expired'), ('Scanner.start', 477, 477, 'scanner-network-guard-6f1c83db3fb4', 'ValueError', 'Engine/database readiness not observed'), ('Scanner.start', 492, 492, 'scanner-network-guard-d458a5755a58', 'ValueError', 'Loaded VERSION differs from observed daily database'), ('Scanner.start', 495, 495, 'scanner-network-guard-f0bd75f6ac53', 'ValueError', 'Benign complete-file control failed'), ('Scanner.start', 497, 497, 'scanner-network-guard-e068fbbc438b', 'ValueError', 'EICAR complete-file control failed'), ('Scanner.start', 499, 499, 'scanner-network-guard-c64d02995b74', 'ValueError', 'Actual EICAR signature detection not observed'), ('Scanner.start', 502, 502, 'scanner-network-guard-ead7f66a2f0b', 'ValueError', 'Scanner executable/config/databases changed during controls'), ('Scanner.start', 504, 504, 'scanner-network-guard-174d06490598', 'ValueError', 'Scanner socket owner/generation changed during controls'))
NETWORK_SITE_TABLE_SHA256 = 'a3afd03cc43d377e4884ddb7900514b948ae5ab6a8a8d4f7d635022addb90b9a'
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
STORAGE_SOURCE_SHA256 = {'launcher': '985d7adefea26cc24fb40580c7336f20dc0e505fe64c0ca025e078f4bd033825', 'scanner': '34f794ca4d05dcd8664fbb8a919975fd0864802bb0ecbd95edae9d67acbd63b6', 'runner': '89daed400342a5d43a5c62bafbac50f024bf3ced95c6aa2d482d4e540b15a6d6'}
STORAGE_SITES = {'create': (151, 151), 'docker': (274, 274), 'propagate': (385, 385), 'original': (360, 361)}
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

OBSERVER_SOURCE_SHA256 = {'storage': '0c3dd559961f61b51bd25afa7489d593822fd877816fd7514c75bd7dbc72cbbf', 'command': '5d063f59ad00c8071e962ebfe34651e343f5c1e2ae6add58cc9a72f837dc1d09'}
OBSERVER_DECLARATIONS = {'storage': ('Lease.validate', '_zero_listener_evidence', '_zero_listener_evidence.call', '_zero_listener_evidence.checked_container', '_zero_listener_evidence.checked_image', '_zero_listener_evidence.checked_network', '_zero_listener_evidence.checked_scanner', '_zero_listener_evidence.fd_inodes', '_zero_listener_evidence.finish', '_zero_listener_evidence.listeners', '_zero_listener_evidence.scanner_generation', '_zero_listener_evidence.snapshot', '_zero_listener_evidence.start_ticks', 'inspect', 'instant', 'observe', 'observe.refresh', 'observe.ticks', 'require', 'validate_snapshots', 'wait_listener_ready', 'wait_listener_ready.call', 'wait_listener_ready.checked_container', 'wait_listener_ready.checked_image', 'wait_listener_ready.checked_network', 'wait_listener_ready.checked_scanner', 'wait_listener_ready.current', 'wait_listener_ready.execute', 'wait_listener_ready.fd_inodes', 'wait_listener_ready.generation', 'wait_listener_ready.listener', 'wait_listener_ready.scanner_generation', 'wait_listener_ready.snapshot', 'wait_listener_ready.start_ticks'), 'command': ('storage_command',)}
OBSERVER_SITES = (('storage', 'require', 28, 28, 'storage-observer-raise-1952d2c9c0cf', 'direct-raise'), ('storage', 'instant', 36, 36, 'storage-observer-guard-edbc0b38823c', 'require-call'), ('storage', 'Lease.validate', 70, 70, 'storage-observer-guard-d51c78de514b', 'require-call'), ('storage', 'Lease.validate', 71, 71, 'storage-observer-guard-56e29cb41c63', 'require-call'), ('storage', 'Lease.validate', 72, 72, 'storage-observer-guard-14b3e0824ccb', 'require-call'), ('storage', 'Lease.validate', 73, 73, 'storage-observer-guard-2c2af4352e5f', 'require-call'), ('storage', 'Lease.validate', 74, 74, 'storage-observer-guard-90bacac03768', 'require-call'), ('storage', 'Lease.validate', 75, 76, 'storage-observer-guard-fde20bb9caa9', 'require-call'), ('storage', 'Lease.validate', 78, 78, 'storage-observer-guard-bd19c65e2e27', 'require-call'), ('storage', 'Lease.validate', 79, 80, 'storage-observer-guard-3ccb4700856b', 'require-call'), ('storage', 'Lease.validate', 82, 82, 'storage-observer-guard-2045d6eefa70', 'require-call'), ('storage', 'Lease.validate', 83, 83, 'storage-observer-guard-c55b719ba993', 'require-call'), ('storage', 'validate_snapshots', 98, 98, 'storage-observer-guard-86e3569a7270', 'require-call'), ('storage', 'validate_snapshots', 100, 102, 'storage-observer-guard-a305ddc938ae', 'require-call'), ('storage', 'validate_snapshots', 103, 104, 'storage-observer-guard-6a8c0ad2f0fa', 'require-call'), ('storage', 'validate_snapshots', 105, 105, 'storage-observer-guard-016d5ba8eb82', 'require-call'), ('storage', 'validate_snapshots', 106, 107, 'storage-observer-guard-479df8bbe28a', 'require-call'), ('storage', 'validate_snapshots', 108, 108, 'storage-observer-guard-33ec8d19db48', 'require-call'), ('storage', 'validate_snapshots', 109, 113, 'storage-observer-guard-a9c2356b7620', 'require-call'), ('storage', 'validate_snapshots', 114, 115, 'storage-observer-guard-d224eaa6aecd', 'require-call'), ('storage', 'validate_snapshots', 116, 116, 'storage-observer-guard-0a2ccf3129e2', 'require-call'), ('storage', 'validate_snapshots', 118, 118, 'storage-observer-guard-c89b90240256', 'require-call'), ('storage', 'validate_snapshots', 120, 121, 'storage-observer-guard-afe49d4909a5', 'require-call'), ('storage', 'validate_snapshots', 122, 125, 'storage-observer-guard-3a88807052e0', 'require-call'), ('storage', 'validate_snapshots', 126, 126, 'storage-observer-guard-60fda6137678', 'require-call'), ('storage', 'validate_snapshots', 127, 128, 'storage-observer-guard-18397742c98a', 'require-call'), ('storage', 'validate_snapshots', 130, 130, 'storage-observer-guard-065e8da8d779', 'require-call'), ('storage', 'inspect', 141, 141, 'storage-observer-guard-7de6d2e4c629', 'require-call'), ('storage', '_zero_listener_evidence', 151, 151, 'storage-observer-raise-c9b3c630c386', 'direct-raise'), ('storage', '_zero_listener_evidence.call', 158, 158, 'storage-observer-raise-cc9709f50a30', 'direct-raise'), ('storage', '_zero_listener_evidence.snapshot', 168, 168, 'storage-observer-raise-cd05ee06a318', 'direct-raise'), ('storage', '_zero_listener_evidence.start_ticks', 188, 188, 'storage-observer-raise-7493c9318b25', 'direct-raise'), ('storage', '_zero_listener_evidence.fd_inodes', 199, 199, 'storage-observer-raise-f7c8396c876c', 'direct-raise'), ('storage', '_zero_listener_evidence.fd_inodes', 201, 201, 'storage-observer-raise-025a65703871', 'direct-raise'), ('storage', '_zero_listener_evidence.fd_inodes', 206, 206, 'storage-observer-raise-e62130b3fc10', 'direct-raise'), ('storage', '_zero_listener_evidence.listeners', 213, 213, 'storage-observer-raise-b521d996dfca', 'direct-raise'), ('storage', '_zero_listener_evidence.listeners', 218, 218, 'storage-observer-raise-e75ab004807c', 'direct-raise'), ('storage', '_zero_listener_evidence.listeners', 222, 222, 'storage-observer-raise-c87a661a837e', 'direct-raise'), ('storage', '_zero_listener_evidence.scanner_generation', 229, 229, 'storage-observer-raise-4147c5e1daeb', 'direct-raise'), ('storage', '_zero_listener_evidence.checked_image', 239, 239, 'storage-observer-raise-cc663ad9f1f3', 'direct-raise'), ('storage', '_zero_listener_evidence.checked_container', 243, 243, 'storage-observer-raise-6062137ca34f', 'direct-raise'), ('storage', '_zero_listener_evidence.checked_container', 244, 244, 'storage-observer-guard-c1299d0bac13', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 246, 248, 'storage-observer-guard-fbbe3186490c', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 249, 250, 'storage-observer-guard-dc8ecd01ea3d', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 251, 251, 'storage-observer-guard-72ad748f8e2c', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 252, 253, 'storage-observer-guard-ffaa91f70aef', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 254, 254, 'storage-observer-guard-2a2c6703b6dd', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 255, 259, 'storage-observer-guard-fc0c0e9cf46d', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 260, 261, 'storage-observer-guard-0deb00204786', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 262, 262, 'storage-observer-guard-e069477c101e', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 264, 264, 'storage-observer-guard-dc35b837418a', 'require-call'), ('storage', '_zero_listener_evidence.checked_container', 266, 267, 'storage-observer-guard-b7b07ae5fd5e', 'require-call'), ('storage', '_zero_listener_evidence.checked_network', 271, 271, 'storage-observer-raise-76a7add2e955', 'direct-raise'), ('storage', '_zero_listener_evidence.checked_network', 272, 275, 'storage-observer-guard-a8728ff7332b', 'require-call'), ('storage', '_zero_listener_evidence.checked_network', 276, 276, 'storage-observer-guard-a0cb4be5d959', 'require-call'), ('storage', '_zero_listener_evidence.checked_scanner', 280, 280, 'storage-observer-raise-e7e857edebf3', 'direct-raise'), ('storage', '_zero_listener_evidence.checked_scanner', 281, 282, 'storage-observer-guard-f8609e31d69b', 'require-call'), ('storage', '_zero_listener_evidence.checked_scanner', 284, 284, 'storage-observer-guard-174bbb0471c4', 'require-call'), ('storage', '_zero_listener_evidence.finish', 289, 289, 'storage-observer-raise-0f29bb3a329c', 'direct-raise'), ('storage', '_zero_listener_evidence', 327, 327, 'storage-observer-raise-66738180af07', 'direct-raise'), ('storage', 'observe', 340, 341, 'storage-observer-guard-792257ca7d5e', 'require-call'), ('storage', 'observe', 343, 345, 'storage-observer-guard-1e4fdf4a3db1', 'require-call'), ('storage', 'observe', 351, 353, 'storage-observer-guard-9074953ee3eb', 'require-call'), ('storage', 'observe', 355, 357, 'storage-observer-guard-1dfd0c779b89', 'require-call'), ('storage', 'observe.refresh', 360, 363, 'storage-observer-guard-e3877372ba6b', 'require-call'), ('storage', 'observe.ticks', 378, 378, 'storage-observer-guard-9f605379f4f0', 'require-call'), ('storage', 'observe', 380, 380, 'storage-observer-guard-69c6f178856e', 'require-call'), ('storage', 'observe', 381, 381, 'storage-observer-guard-48d955f855c3', 'require-call'), ('storage', 'observe', 382, 382, 'storage-observer-guard-340986445256', 'require-call'), ('storage', 'observe', 383, 383, 'storage-observer-guard-1ddee0d34824', 'require-call'), ('storage', 'observe', 385, 385, 'storage-observer-guard-1fa3ac5854e3', 'require-call'), ('storage', 'observe', 389, 389, 'storage-observer-guard-71bdd224264f', 'require-call'), ('storage', 'observe', 391, 391, 'storage-observer-guard-8763354b3c86', 'require-call'), ('storage', 'observe', 395, 395, 'storage-observer-guard-304c7340a3d7', 'require-call'), ('storage', 'observe', 402, 402, 'storage-observer-guard-86048d095a7c', 'require-call'), ('storage', 'observe', 404, 404, 'storage-observer-guard-ce22c5e9bc9e', 'require-call'), ('storage', 'observe', 405, 405, 'storage-observer-guard-8d2c7b502bda', 'require-call'), ('storage', 'wait_listener_ready', 419, 420, 'storage-observer-guard-435ec723f51d', 'require-call'), ('storage', 'wait_listener_ready', 421, 421, 'storage-observer-guard-7e8da8f65e14', 'require-call'), ('storage', 'wait_listener_ready', 423, 424, 'storage-observer-guard-4f5d74aa108f', 'require-call'), ('storage', 'wait_listener_ready', 425, 426, 'storage-observer-guard-162274318970', 'require-call'), ('storage', 'wait_listener_ready', 430, 430, 'storage-observer-guard-4c761e7ce190', 'require-call'), ('storage', 'wait_listener_ready.current', 436, 437, 'storage-observer-guard-ccb5330ece5b', 'require-call'), ('storage', 'wait_listener_ready.current', 438, 439, 'storage-observer-guard-069f6dc33cfa', 'require-call'), ('storage', 'wait_listener_ready.call', 444, 444, 'storage-observer-guard-fa34ee665369', 'require-call'), ('storage', 'wait_listener_ready.call', 448, 448, 'storage-observer-guard-b037243c58e9', 'require-call'), ('storage', 'wait_listener_ready.snapshot', 456, 457, 'storage-observer-guard-fa9d8aebd82d', 'require-call'), ('storage', 'wait_listener_ready.scanner_generation', 463, 463, 'storage-observer-raise-6beda56dc2fc', 'direct-raise'), ('storage', 'wait_listener_ready.checked_image', 473, 473, 'storage-observer-raise-e32c4918ffdd', 'direct-raise'), ('storage', 'wait_listener_ready.checked_container', 477, 477, 'storage-observer-raise-b84263bedbc5', 'direct-raise'), ('storage', 'wait_listener_ready.checked_container', 478, 478, 'storage-observer-guard-77f3502a9c82', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 480, 482, 'storage-observer-guard-35a2a49f6a88', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 483, 484, 'storage-observer-guard-b7a2745863e2', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 485, 485, 'storage-observer-guard-8ef7909b124a', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 486, 487, 'storage-observer-guard-eaaa169451fe', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 488, 488, 'storage-observer-guard-747d85a0eefb', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 489, 493, 'storage-observer-guard-1f2780597d36', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 494, 495, 'storage-observer-guard-110902396543', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 496, 496, 'storage-observer-guard-830be558aeda', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 498, 498, 'storage-observer-guard-582cb3333523', 'require-call'), ('storage', 'wait_listener_ready.checked_container', 500, 501, 'storage-observer-guard-da7e19ddc9b0', 'require-call'), ('storage', 'wait_listener_ready.checked_network', 505, 505, 'storage-observer-raise-238f31bbbd98', 'direct-raise'), ('storage', 'wait_listener_ready.checked_network', 506, 509, 'storage-observer-guard-d2233b5c6cf0', 'require-call'), ('storage', 'wait_listener_ready.checked_network', 510, 510, 'storage-observer-guard-d7f79108b109', 'require-call'), ('storage', 'wait_listener_ready.checked_scanner', 514, 514, 'storage-observer-raise-b042fe76a7e8', 'direct-raise'), ('storage', 'wait_listener_ready.checked_scanner', 515, 516, 'storage-observer-guard-bb11d5b3632c', 'require-call'), ('storage', 'wait_listener_ready.checked_scanner', 518, 518, 'storage-observer-guard-0d1dde230334', 'require-call'), ('storage', 'wait_listener_ready.checked_scanner', 520, 521, 'storage-observer-guard-13f451b0dfa2', 'require-call'), ('storage', 'wait_listener_ready.start_ticks', 530, 530, 'storage-observer-raise-e6995aee3729', 'direct-raise'), ('storage', 'wait_listener_ready.fd_inodes', 541, 541, 'storage-observer-raise-2a3f9c04287e', 'direct-raise'), ('storage', 'wait_listener_ready.fd_inodes', 543, 543, 'storage-observer-raise-59e4683552fb', 'direct-raise'), ('storage', 'wait_listener_ready.fd_inodes', 548, 548, 'storage-observer-raise-3028633748be', 'direct-raise'), ('storage', 'wait_listener_ready.generation', 556, 556, 'storage-observer-guard-4d69dca0a25e', 'require-call'), ('storage', 'wait_listener_ready.generation', 559, 559, 'storage-observer-guard-9fe40a462bf7', 'require-call'), ('storage', 'wait_listener_ready.generation', 562, 563, 'storage-observer-guard-bf5b58796f01', 'require-call'), ('storage', 'wait_listener_ready.listener', 569, 569, 'storage-observer-guard-d7a673bf445f', 'require-call'), ('storage', 'wait_listener_ready.listener', 574, 575, 'storage-observer-guard-f413365ada90', 'require-call'), ('storage', 'wait_listener_ready.listener', 577, 577, 'storage-observer-guard-3cb51e4c2d6b', 'require-call'), ('storage', 'wait_listener_ready.listener', 578, 578, 'storage-observer-guard-bee62a95d9ed', 'require-call'), ('storage', 'wait_listener_ready.listener', 580, 580, 'storage-observer-guard-22e083666763', 'require-call'), ('storage', 'wait_listener_ready', 597, 597, 'storage-observer-guard-01e3387e6af1', 'require-call'), ('storage', 'wait_listener_ready', 598, 598, 'storage-observer-guard-089bf4ba67a0', 'require-call'), ('storage', 'wait_listener_ready', 599, 599, 'storage-observer-guard-b76f0a29bbc9', 'require-call'), ('storage', 'wait_listener_ready', 600, 601, 'storage-observer-guard-9ec4bace678d', 'require-call'), ('storage', 'wait_listener_ready', 604, 604, 'storage-observer-guard-82b722ef9762', 'require-call'), ('storage', 'wait_listener_ready', 605, 605, 'storage-observer-guard-4718bdca0ccc', 'require-call'), ('command', 'storage_command', 17, 17, 'storage-observer-raise-73b41ddb0696', 'direct-raise'), ('command', 'storage_command', 22, 22, 'storage-observer-raise-85801c7b7a15', 'direct-raise'), ('command', 'storage_command', 34, 34, 'storage-observer-raise-f4a08245930c', 'direct-raise'), ('command', 'storage_command', 38, 38, 'storage-observer-raise-410ad1b1bcc6', 'direct-raise'), ('command', 'storage_command', 41, 41, 'storage-observer-raise-21f40627e5cf', 'direct-raise'), ('command', 'storage_command', 45, 45, 'storage-observer-raise-c17fd89a20ac', 'direct-raise'), ('command', 'storage_command', 47, 47, 'storage-observer-raise-c856b51019b2', 'direct-raise'))
OBSERVER_CAPTURE = None


def capture_observer_sites(storage, command, bridge, sources, original_classes):
    global OBSERVER_CAPTURE
    import hashlib
    import types
    modules = {'storage': storage, 'command': command}
    if (OBSERVER_CAPTURE is not None or type(sources) is not dict or set(sources) != set(modules)
            or any(type(module) is not types.ModuleType for module in modules.values())
            or any(type(sources[name]) is not bytes
                   or hashlib.sha256(sources[name]).hexdigest() != OBSERVER_SOURCE_SHA256[name]
                   for name in modules)):
        raise ValueError('Qualified observer source association refused')
    if (type(original_classes) is not dict
            or set(original_classes) != {'owned_storage_backend', 'borrowed_scanner_bridge'}
            or any(type(value) is not tuple or len(value) != 2 for value in original_classes.values())
            or original_classes['owned_storage_backend'][0] is not storage
            or original_classes['borrowed_scanner_bridge'][0] is not bridge
            or storage.AdmissionError is not original_classes['owned_storage_backend'][1]
            or bridge.BridgeRefused is not original_classes['borrowed_scanner_bridge'][1]
            or command.BridgeRefused is not original_classes['borrowed_scanner_bridge'][1]):
        raise ValueError('Qualified observer original class birth differs')
    if (type(storage.AdmissionError) is not type or storage.AdmissionError.__bases__ != (ValueError,)
            or storage.AdmissionError.__module__ != storage.__name__
            or storage.AdmissionError.__qualname__ != 'AdmissionError'
            or command.BridgeRefused is not bridge.BridgeRefused):
        raise ValueError('Qualified observer original class differs')
    captured = {}
    for name, module in modules.items():
        expected_module = compile(sources[name], '<qualified-observer-structure>', 'exec', dont_inherit=True)
        for path in OBSERVER_DECLARATIONS[name]:
            parts = path.split('.')
            if parts[0] == 'Lease':
                value = getattr(module.Lease, parts[1])
                nested = ()
            else:
                value = getattr(module, parts[0])
                nested = parts[1:]
            if type(value) is not types.FunctionType or value.__globals__ is not module.__dict__:
                raise ValueError('Qualified observer declaration differs')
            code = value.__code__
            for child in nested:
                code = expected_code(code, child)
            if not same_code(expected_code(expected_module, path), code):
                raise ValueError('Qualified observer declaration differs')
            captured[(name, path)] = code
    OBSERVER_CAPTURE = (storage.AdmissionError, command.BridgeRefused, tuple(captured.items()),
                        tuple((name, module.__dict__) for name, module in modules.items()))


def observer_clause(error):
    if CURRENT_STAGE != 'storage-observe' or OBSERVER_CAPTURE is None:
        return 'unclassified-source-clause'
    storage_error, command_error, captured, namespaces = OBSERVER_CAPTURE
    if type(error) not in (storage_error, command_error):
        return 'unclassified-source-clause'
    codes, globals_by_module = dict(captured), dict(namespaces)
    trace = error.__traceback__
    frames = []
    while trace is not None:
        if len(frames) == TRACE_LIMIT:
            return 'unclassified-source-clause'
        frames.append((trace.tb_frame.f_code, trace.tb_lineno, trace.tb_frame.f_globals))
        trace = trace.tb_next
    if not frames:
        return 'unclassified-source-clause'
    code, line, namespace = frames[-1]
    if type(error) is command_error:
        for module, path, start, end, clause, kind in OBSERVER_SITES:
            if (module == 'command' and kind == 'direct-raise'
                    and code is codes[(module, path)] and namespace is globals_by_module[module]
                    and start <= line <= end):
                return clause
        return 'unclassified-source-clause'
    if (len(frames) < 2 or code is not codes[('storage', 'require')]
            or namespace is not globals_by_module['storage']
            or not any(module == 'storage' and path == 'require' and kind == 'direct-raise'
                       and start <= line <= end for module, path, start, end, clause, kind in OBSERVER_SITES)):
        return 'unclassified-source-clause'
    code, line, namespace = frames[-2]
    for module, path, start, end, clause, kind in OBSERVER_SITES:
        if (module == 'storage' and kind == 'require-call'
                and code is codes[(module, path)] and namespace is globals_by_module[module]
                and start <= line <= end):
            return clause
    return 'unclassified-source-clause'

LISTENER_ZERO_CLAUSE = 'storage-observer-guard-304c7340a3d7'
LISTENER_EVIDENCE = None
LISTENER_CLAUSES = ('zero-ipv4-generation-changed', 'zero-ipv4-later-ipv4-present',
                    'zero-ipv4-no-selected-tcp6-listener', 'zero-ipv4-multiple-tcp6-listeners',
                    'zero-ipv4-tcp6-fd-unowned', 'zero-ipv4-tcp6-single-init-owned-stable')


def listener_evidence(error, clause):
    """Only original finish/helper/observe frames associate subordinate evidence."""
    import sys
    global LISTENER_EVIDENCE
    if OBSERVER_CAPTURE is None or LISTENER_EVIDENCE is not None or type(clause) is not str or clause not in LISTENER_CLAUSES:
        raise ValueError('Original listener evidence required')
    storage_error, command_error, captured, namespaces = OBSERVER_CAPTURE
    codes, originals = dict(captured), dict(namespaces)
    caller = sys._getframe(1)
    try:
        for path in ('_zero_listener_evidence.finish', '_zero_listener_evidence', 'observe'):
            if caller is None or caller.f_code is not codes[('storage', path)] or caller.f_globals is not originals['storage']:
                raise ValueError('Original listener evidence caller required')
            caller = caller.f_back
        if type(error) is not storage_error:
            raise ValueError('Original listener evidence caller required')
    finally:
        del caller
    original_clause = observer_clause(error)
    if original_clause != LISTENER_ZERO_CLAUSE or FIRST != ('storage-observe', 'owner-refusal', original_clause):
        raise ValueError('Original zero listener refusal required')
    LISTENER_EVIDENCE = (error, clause)

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
    if category not in CATEGORIES or clause not in tuple(GUARDS.values()) + tuple(site[3] for site in H_SITES+B_SITES+NETWORK_SITES) + tuple(site[4] for site in OBSERVER_SITES) + (STORAGE_CLAUSE, STORAGE_WRAPPED_CLAUSE, STORAGE_CREATE_WRAPPED_CLAUSE, *STORAGE_DENIALS, 'unclassified-source-clause',):
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
    if clause == 'unclassified-source-clause' and category == 'owner-refusal':
        clause = observer_clause(error)
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
    if LISTENER_EVIDENCE is not None and FIRST == ('storage-observe', 'owner-refusal', LISTENER_ZERO_CLAUSE):
        clause = LISTENER_EVIDENCE[1]
    return {'schemaVersion': 1, 'sourceHead': head, 'runId': run, 'runAttempt': attempt,
            'sourceManifestSha256': digest, 'mode': mode, 'firstStage': stage_code,
            'firstCategory': category, 'firstDenialClause': clause,
            'diagnosticOnly': True, 'pairAccepted': False, 'cleanupAccepted': False,
            'fileRuntimeAccepted': False, 'genuineEightHostFinancialAccepted': False}
