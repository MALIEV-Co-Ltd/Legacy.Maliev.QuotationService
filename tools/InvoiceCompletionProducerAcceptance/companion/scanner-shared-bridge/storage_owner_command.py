"""Explicit dependency seam for original storage observer; never monkeypatch globals.

Root integrates the corresponding optional command_runner argument into the
unpublished storage observer before this module can be used. The runner module is
independently byte-qualified by the outer loader; identity is not provenance.
"""
import re
import math
import time
from borrowed_scanner_bridge import BridgeRefused


def storage_command(args, maximum=2 * 1024 * 1024, *, absolute_deadline=None):
    import scanner_docker_command
    if (type(args) is not list or not args or args[0] != 'docker'
            or type(maximum) is not int or not 0 < maximum <= 2 * 1024 * 1024):
        raise BridgeRefused('Bounded owner Docker command required')
    # Original observer's read commands are textual /proc metadata; no arbitrary
    # private binary payload is converted through this stripped-text interface.
    require_strings = all(type(value) is str for value in args)
    if not require_strings:
        raise BridgeRefused('Textual source-owned arguments required')
    handle = args[3] if len(args) >= 4 else ''
    inspect_allowed = (len(args) == 4 and args[1:3] in
        (['container', 'inspect'], ['image', 'inspect'], ['network', 'inspect'])
        and re.fullmatch(r'(?:sha256:)?[0-9a-f]{64}', handle) is not None)
    exec_allowed = (len(args) in (5, 6) and args[1] == 'exec'
        and re.fullmatch('[0-9a-f]{64}', args[2]) is not None
        and args[3:] in (['cat', '/proc/1/stat'], ['cat', '/proc/1/cmdline'],
            ['cat', '/proc/1/net/tcp'], ['cat', '/proc/1/net/tcp6'], ['readlink', '/proc/1/exe'],
            ['sha256sum', '/proc/1/exe'], ['ls', '-l', '/proc/1/fd']))
    allowed = require_strings and (inspect_allowed or exec_allowed)
    if not allowed:
        raise BridgeRefused('Unreviewed storage observation command')
    timeout = 10
    if absolute_deadline is not None:
        if type(absolute_deadline) not in (int, float) or (type(absolute_deadline) is int and absolute_deadline.bit_length() > 53) or not math.isfinite(absolute_deadline):
            raise BridgeRefused('Finite listener observation deadline required')
        remaining = absolute_deadline - time.monotonic()
        if not remaining > 0:
            raise BridgeRefused('Listener observation deadline expired')
        timeout = min(10, remaining)
    result = scanner_docker_command.run_docker(args[1:], timeout=timeout).encode('utf-8')
    if absolute_deadline is not None and not time.monotonic() < absolute_deadline:
        raise BridgeRefused('Listener observation returned after deadline')
    if len(result) > maximum:
        raise BridgeRefused('Owner observation exceeds read bound')
    return result
