"""Hosted resource proof callable; caller supplies retained original owner objects.

No CLI creates unverifiable ownership from JSON. Root hosted launcher pins modules,
starts the genuine Scanner, allocates its exact storage Lease and holds bridge.
This callable executes the real storage observer, then quiescence-bound cleanup.
On failure caller retains bridge and its exact borrow for independent recovery.
"""
from borrowed_scanner_bridge import BorrowedScannerBridge, BridgeRefused


def qualify_held_pair(bridge):
    if type(bridge) is not BorrowedScannerBridge or bridge._borrow is None:
        raise BridgeRefused('Original retained bridge borrow required')
    observed = False
    failures = []
    try:
        if bridge._failure:
            raise BridgeRefused('Failed bridge quarantined for cleanup only')
        import owned_storage_backend as storage
        private = storage.observe(bridge._borrow, bridge)
        if private.get('privateBackendObserved') is not True:
            raise BridgeRefused('Actual private backend observation absent')
        observed = True
    except BaseException as error:
        failures.append(error)
        bridge._failure = True
    try:
        bridge.remove_backend_after_quiescence()
    except BaseException as error:
        failures.append(error)
        bridge._failure = True
    # Even after an original observation refusal, physically complete owned cleanup
    # if release succeeded. Original failure is sticky and public proof stays refused.
    if bridge._released:
        try:
            bridge.close_scanner()
        except BaseException as error:
            failures.append(error)
    if failures or not observed:
        raise BridgeRefused('Held pair qualification failed; retained owner required') from None
    return {'schemaVersion': 1, 'privateBackendProcessObserved': True,
            'exactSharedBridgeObserved': True, 'ownedPairCleanupVerified': True,
            'fileBoundaryAccepted': False, 'frontNativeAccepted': False,
            'financialBusinessGraphAccepted': False, 'genuineEightHostFinancialAccepted': False}
