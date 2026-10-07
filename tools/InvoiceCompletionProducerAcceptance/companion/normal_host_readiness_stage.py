"""Real held-host launch/readiness stage for the canonical financial parent.

Resources, source admission, front bootstrap and financial execution remain the
parent's obligations. Transport startup is separate from application readiness:
Auth/IAM registration may require other hosts to be listening first.
"""
from datetime import datetime, timezone
import time

import hosted_companion_resources as h
import owned_normal_hosts as normal
from actual_dotnet_start import observe_normal_starts
from actual_application_health import observe_health


class NormalHostReadinessStage:
    def __init__(self, owner, parent_profile, observer_dll, observer_sha256):
        self.owner = owner
        self.parent_profile = parent_profile
        self.observer_dll = observer_dll
        self.observer_sha256 = observer_sha256
        self.started = {}

    def start_phase(self, owners):
        """Launch a nonempty explicit phase and capture real native UTC times.

        File can be launched alone before its signing handshake. No health or
        financial acceptance is asserted until admit_ready_graph runs.
        """
        requested = tuple(owners)
        h.require(requested and len(set(requested)) == len(requested)
                  and set(requested).issubset(normal.OWNERS)
                  and not set(requested).intersection(self.started),
                  "Explicit unique unstarted normal-host phase required")
        try:
            for name in requested:
                self.started[name] = self.owner.start(name)
            observations = observe_normal_starts(self.owner, self.parent_profile,
                self.observer_dll, self.observer_sha256, owners=requested)
            h.require(set(observations) == set(requested), "Actual phase start observations incomplete")
            for name in requested:
                # Preserve the seven-digit native UTC; observedStartUtc remains
                # a separate observation timestamp, never promoted to StartTime.
                self.started[name]["actualStartedUtc"] = observations[name]["StartedUtc"]
                self.started[name]["nativeStartObservation"] = observations[name]
            return {name: dict(self.started[name]) for name in requested}
        except BaseException:
            self.owner.close()
            raise

    def admit_ready_graph(self, budget=30):
        """Bounded real dependency health, followed by a second native census.

        Call only after the File front bootstrap and upstream fixture readiness.
        One total deadline covers retries across all eight actual health routes.
        A degraded/cyclic registration cannot produce a ready graph receipt.
        """
        h.require(type(budget) in (int, float) and 0 < budget <= 60
                  and set(self.started) == normal.OWNERS
                  and not self.owner.closed and not self.owner.cleanup_failures,
                  "Live exact eight-host stage and bounded health deadline required")
        context = self.owner.context
        deadline = time.monotonic() + min(budget,
            (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds())
        health = {}
        try:
            rows = tuple(self.owner.owned)
            h.require(len(rows) == 8 and {row[0].owner for row in rows} == normal.OWNERS,
                      "Actual held eight-host graph differs")
            for spec, process, ticks, _ in rows:
                while True:
                    remaining = h.remaining_time(deadline)
                    try:
                        health[spec.owner] = observe_health(spec, process, ticks,
                            self.owner.dotnet, context, self.owner.environment,
                            budget=min(5, remaining))
                        break
                    except (OSError, h.AdmissionError):
                        h.require(process.poll() is None,
                                  "Normal host exited before application readiness")
                        context.validate(self.owner.environment, datetime.now(timezone.utc))
                        remaining = h.remaining_time(deadline)
                        time.sleep(min(0.05, remaining))
            observations = observe_normal_starts(self.owner, self.parent_profile,
                self.observer_dll, self.observer_sha256)
            h.require(set(observations) == normal.OWNERS, "Actual ready graph census incomplete")
            for name in normal.OWNERS:
                h.require(observations[name] == self.started[name]["nativeStartObservation"],
                          "Normal host identity changed during application readiness")
            context.validate(self.owner.environment, datetime.now(timezone.utc))
            return {"normalHostReadinessObserved": True,
                    "genuineEightHostFinancialAccepted": False,
                    "starts": {name: dict(value) for name, value in self.started.items()},
                    "health": health}
        except BaseException:
            self.owner.close()
            raise
