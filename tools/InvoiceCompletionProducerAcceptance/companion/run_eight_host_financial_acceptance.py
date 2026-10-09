"""Canonical source startup phase only. No invented producer authority or acceptance CLI."""
from dataclasses import dataclass, field
import hosted_companion_resources as h
import held_host_lifetime as lifetime
from retained_file_front_consumers import RetainedFileFrontConsumers
from borrowed_scanner_bridge import BorrowedScannerBridge
from shared_file_front_configuration import install_observed_file_configuration, observe_shared_declaration
from owned_front_host import load_public_profile


@dataclass(frozen=True)
class PreparedFileFrontStartup:
    capability: object
    bridge: object
    observed_configuration: object = field(repr=False)


class MissingOrdinaryFileAuthority(ValueError):
    def __init__(self):
        super().__init__("Qualified ordinary File create/read authority and current native producer proof required")


def prepare_file_front_startup(original_lifetime, normal, front, bridge, observer_dll, observer_sha256):
    """Retain under original timer before Front birth; publish observed File config before File birth."""
    h.require(type(original_lifetime) is lifetime.HostLifetime
              and type(normal) is lifetime.LifetimeNormalHosts and type(front) is lifetime.LifetimeFrontHost
              and type(bridge) is BorrowedScannerBridge, "Exact original held graph required")
    h.require(normal.timer_owned and not normal.closed and not normal.cleanup_failures,
              "Original base graph and finite timer must already be admitted")
    capability = RetainedFileFrontConsumers(original_lifetime, normal, front)
    bridge.retain_file_front_consumers(capability)
    try:
        # The original bounded/private/source/parent/bootstrap loader supplies the
        # profile. No copied validation or caller PID/resource dictionaries bypass it.
        profile, _ = load_public_profile(front.spec, front.context, front.pipe)
        backend, shared = observe_shared_declaration(bridge)
        h.require(profile.get("SharedScannerBridge") == shared and profile["Backend"] == backend,
                  "Prebirth profile must match actual original shared owners")
        front.start()  # Original unsealed bootstrap and timer/source/kernel admission.
        bridge.refresh()  # Original first listener binds the SAME retained Front Child.
        configuration = install_observed_file_configuration(capability, bridge, observer_dll, observer_sha256)
        return PreparedFileFrontStartup(capability, bridge, configuration)
    except BaseException:
        capability.failed = True
        bridge._failure = True
        # Objects remain held; original global physical cleanup is mandatory and
        # cannot authorize backend release after failed/unadmitted preparation.
        raise


def start_authenticated_file(capability, bridge):
    """No accepted ordinary File authority API exists in current producer evidence."""
    raise MissingOrdinaryFileAuthority()


def main():
    # No graph/key/claim/file inputs guessed from command line or local defaults.
    # Owner-qualified producer loader and authority must be integrated before CLI execution.
    raise MissingOrdinaryFileAuthority()


if __name__ == "__main__":
    main()
