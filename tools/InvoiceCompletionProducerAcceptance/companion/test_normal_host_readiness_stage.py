import unittest
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace
from unittest.mock import Mock, patch

import hosted_companion_resources as h
import normal_host_readiness_stage as stage


class NormalReadinessStageTests(unittest.TestCase):
    def setUp(self):
        self.context = SimpleNamespace(expires_utc=(datetime.now(timezone.utc)+timedelta(minutes=5)).isoformat(), validate=Mock())
        self.owner = SimpleNamespace(context=self.context, environment={}, closed=False,
            cleanup_failures=[], owned=[], start=Mock(side_effect=lambda name: {"owner": name, "observedStartUtc": "observation-only"}),
            close=Mock(), dotnet="/owned/dotnet")
        self.driver = stage.NormalHostReadinessStage(self.owner, {}, "/observer.dll", "a"*64)
        self.observations = {name: {"StartedUtc": "2026-10-07T00:00:00.1234567Z", "Pid": number}
                             for number, name in enumerate(sorted(stage.normal.OWNERS), 1)}

    def start_all(self):
        with patch.object(stage, "observe_normal_starts", return_value=self.observations):
            self.driver.start_phase(sorted(stage.normal.OWNERS))
        self.owner.owned = [[SimpleNamespace(owner=name), Mock(), 123, "observation"]
                            for name in sorted(stage.normal.OWNERS)]

    def test_phase_preserves_exact_native_utc_and_observation_timestamp(self):
        with patch.object(stage, "observe_normal_starts", return_value={"File": self.observations["File"]}):
            result = self.driver.start_phase(["File"])
        self.assertEqual(result["File"]["actualStartedUtc"], "2026-10-07T00:00:00.1234567Z")
        self.assertEqual(result["File"]["observedStartUtc"], "observation-only")

    def test_duplicate_phase_rejected_before_second_start(self):
        with patch.object(stage, "observe_normal_starts", return_value={"File": self.observations["File"]}):
            self.driver.start_phase(["File"])
        with self.assertRaises(h.AdmissionError):
            self.driver.start_phase(["File"])
        self.assertEqual(self.owner.start.call_count, 1)

    def test_native_observation_failure_closes_owned_children(self):
        with patch.object(stage, "observe_normal_starts", side_effect=h.AdmissionError("unavailable")):
            with self.assertRaises(h.AdmissionError): self.driver.start_phase(["File"])
        self.owner.close.assert_called_once()

    def test_ready_graph_requires_all_owners(self):
        with self.assertRaises(h.AdmissionError): self.driver.admit_ready_graph()

    def test_actual_routes_and_second_census_invoked_for_every_held_owner(self):
        self.start_all()
        with patch.object(stage, "observe_health", return_value={"applicationHealthObserved": True}) as health, \
             patch.object(stage, "observe_normal_starts", return_value=self.observations) as census:
            result = self.driver.admit_ready_graph()
        self.assertEqual(health.call_count, 8)
        census.assert_called_once()
        self.assertFalse(result["genuineEightHostFinancialAccepted"])
        self.assertTrue(result["normalHostReadinessObserved"])

    def test_changed_generation_after_health_closes_every_held_child(self):
        self.start_all()
        changed = {name: dict(value) for name, value in self.observations.items()}
        changed["IAM"]["Pid"] = 999
        with patch.object(stage, "observe_health", return_value={}), \
             patch.object(stage, "observe_normal_starts", return_value=changed):
            with self.assertRaises(h.AdmissionError): self.driver.admit_ready_graph()
        self.owner.close.assert_called_once()

    def test_exited_host_cannot_be_declared_ready(self):
        self.start_all()
        self.owner.owned[0][1].poll.return_value = 7
        with patch.object(stage, "observe_health", side_effect=h.AdmissionError("degraded")):
            with self.assertRaises(h.AdmissionError): self.driver.admit_ready_graph()
        self.owner.close.assert_called_once()


if __name__ == "__main__": unittest.main()
