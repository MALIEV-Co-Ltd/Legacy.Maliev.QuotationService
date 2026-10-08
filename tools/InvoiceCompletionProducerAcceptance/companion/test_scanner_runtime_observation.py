"""Mocked Engine response controls only; no Docker, sockets, children or kernel proof."""
import copy
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch


# Existing dependency definitions only; the new scanner is imported from this file's folder.
spec = importlib.util.spec_from_file_location("runtime_observation_subject", Path(__file__).with_name("hosted_scanner_readiness.py"))
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


def fixture(scanner, running=True):
    return {"Id": scanner.container_id, "Image": scanner.image_id,
            "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}},
            "Created": "2026-10-08T00:00:00Z",
            "State": {"StartedAt": "2026-10-08T00:00:01Z", "Running": running,
                      "Paused": False, "Restarting": False, "Pid": 42},
            "HostConfig": {"Memory": 1610612736, "NanoCpus": 2000000000,
                           "CapDrop": ["ALL"], "CapAdd": None, "ReadonlyRootfs": True,
                           "PortBindings": {}, "PublishAllPorts": False},
            "NetworkSettings": {"Ports": {}, "Networks": {}}}


def owner():
    scanner = subject.Scanner("pure-policy-control")
    scanner.container_id = "a" * 64
    scanner.image_id = "sha256:" + "b" * 64
    scanner.receipt["resources"] = [{"kind": "container", "owned": True,
                                     "name": scanner.name, "containerId": scanner.container_id}]
    scanner.receipt["containerGeneration"] = {"createdUtc": "2026-10-08T00:00:00Z",
                                               "startedUtc": "2026-10-08T00:00:01Z"}
    return scanner


class RuntimeObservationTests(unittest.TestCase):
    def test_exact_engine_fields_retained_without_kernel_claim(self):
        scanner = owner()
        container = fixture(scanner)
        for cap_add in (None, []):
            container["HostConfig"]["CapAdd"] = cap_add
            result = subject.observed_runtime_policy(container)
            self.assertEqual(1610612736, result["memoryBytes"])
            self.assertEqual(2000000000, result["nanoCpus"])
            self.assertEqual(["ALL"], result["capDrop"])
            self.assertEqual(cap_add, result["capAdd"])
            self.assertIs(result["engineConfigurationObserved"], True)
            self.assertIs(result["kernelEnforcementObserved"], False)

    def test_missing_or_wrong_typed_policy_fields_refuse(self):
        container = fixture(owner())
        for key in ("Memory", "NanoCpus", "CapDrop", "CapAdd", "ReadonlyRootfs"):
            changed = copy.deepcopy(container)
            del changed["HostConfig"][key]
            with self.subTest(missing=key), self.assertRaises(ValueError):
                subject.observed_runtime_policy(changed)
        for key, value in (("Memory", 0), ("Memory", True), ("Memory", "1610612736"),
                           ("NanoCpus", 0), ("NanoCpus", True), ("NanoCpus", 2000000001),
                           ("CapDrop", []), ("CapDrop", "ALL"), ("CapDrop", ["ALL", "NET_ADMIN"]),
                           ("CapAdd", ["NET_ADMIN"]), ("CapAdd", False),
                           ("ReadonlyRootfs", False), ("ReadonlyRootfs", 1)):
            changed = copy.deepcopy(container)
            changed["HostConfig"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                subject.observed_runtime_policy(changed)

    def test_all_phases_bind_observations_to_same_owner_and_generation(self):
        scanner = owner()
        for phase in ("start", "current", "preStop", "preRemoval"):
            result = scanner.observe_runtime_boundary(fixture(scanner), phase)
            self.assertEqual(scanner.container_id, result["containerId"])
            self.assertEqual(scanner.image_id, result["imageId"])
            self.assertEqual(scanner.run_id, result["runLabel"])
            self.assertIs(result["portIsolation"]["absenceVerified"], True)
        self.assertEqual(4, len(scanner.receipt["runtimeBoundaryObservations"]))

    def test_foreign_owner_or_generation_cannot_publish_snapshot(self):
        for kind in ("id", "image", "label", "created", "started"):
            scanner = owner()
            container = fixture(scanner)
            if kind == "id": container["Id"] = "c" * 64
            if kind == "image": container["Image"] = "sha256:" + "c" * 64
            if kind == "label": container["Config"]["Labels"]["financial.acceptance.run"] = "foreign"
            if kind == "created": container["Created"] = "2026-10-08T00:00:02Z"
            if kind == "started": container["State"]["StartedAt"] = "2026-10-08T00:00:02Z"
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                scanner.observe_runtime_boundary(container, "current")
            self.assertNotIn("runtimeBoundaryObservations", scanner.receipt)
            self.assertEqual(["currentRefused"], scanner.receipt["runtimeBoundaryFailures"])

    def test_each_phase_refuses_configured_and_actual_publication(self):
        for phase in ("start", "current", "preStop", "preRemoval"):
            for mode in ("configured", "actual", "publishAll", "missing"):
                scanner = owner()
                container = fixture(scanner)
                if mode == "configured": container["HostConfig"]["PortBindings"] = {"3310/tcp": []}
                if mode == "actual": container["NetworkSettings"]["Ports"] = {"3310/tcp": [{"HostPort": "12345"}]}
                if mode == "publishAll": container["HostConfig"]["PublishAllPorts"] = True
                if mode == "missing": del container["NetworkSettings"]["Ports"]
                with self.subTest(phase=phase, mode=mode), self.assertRaises(ValueError):
                    scanner.observe_runtime_boundary(container, phase)
                self.assertNotIn("runtimeBoundaryObservations", scanner.receipt)

    def test_startup_diagnostic_records_policy_or_fixed_refusal(self):
        for valid in (True, False):
            scanner = owner()
            container = fixture(scanner)
            if not valid: container["HostConfig"]["Memory"] = 0
            with patch.object(scanner, "docker", side_effect=[json.dumps([container]), ""]):
                result = scanner.startup_diagnostic()
            self.assertIs(result["observed"], True)
            self.assertIs(result["runtimeBoundaryObserved"], valid)
            if valid: self.assertIn("start", scanner.receipt["runtimeBoundaryObservations"])
            else: self.assertEqual("ValueError", result["runtimeBoundaryErrorType"])

    def close_with(self, scanner, before, stopped):
        calls = []
        def docker(*args, **kwargs):
            calls.append(args)
            if args[0] == "inspect":
                return json.dumps([before if sum(c[0] == "inspect" for c in calls) == 1 else stopped])
            return ""
        with patch.object(scanner, "docker", side_effect=docker):
            result = scanner.close()
        return result, calls

    def test_cleanup_records_before_stop_and_before_removal(self):
        scanner = owner()
        result, calls = self.close_with(scanner, fixture(scanner), fixture(scanner, False))
        self.assertTrue(result)
        self.assertEqual({"preStop", "preRemoval"}, set(scanner.receipt["runtimeBoundaryObservations"]))
        self.assertIn(("stop", "--time", "5", scanner.container_id), calls)
        self.assertIn(("rm", scanner.container_id), calls)

    def test_policy_drift_stays_failed_but_exact_owned_cleanup_attempted(self):
        for phase in ("preStop", "preRemoval"):
            scanner = owner()
            before, stopped = fixture(scanner), fixture(scanner, False)
            (before if phase == "preStop" else stopped)["HostConfig"]["NanoCpus"] = 0
            result, calls = self.close_with(scanner, before, stopped)
            self.assertFalse(result)
            self.assertIn(("rm", scanner.container_id), calls)
            self.assertEqual([phase + "Refused"], scanner.receipt["runtimeBoundaryFailures"])
            result, _ = self.close_with(scanner, fixture(scanner), fixture(scanner, False))
            self.assertFalse(result)  # Recovery cannot erase the original admission failure.

    def test_final_publication_drift_cannot_claim_cleanup_success(self):
        scanner = owner()
        stopped = fixture(scanner, False)
        stopped["HostConfig"]["PortBindings"] = {"3310/tcp": []}
        result, calls = self.close_with(scanner, fixture(scanner), stopped)
        self.assertFalse(result)
        self.assertIn(("rm", scanner.container_id), calls)
        self.assertNotIn("preRemoval", scanner.receipt.get("runtimeBoundaryObservations", {}))

    def test_stopped_state_requires_explicit_not_running_paused_or_restarting(self):
        for flag in ("Running", "Paused", "Restarting"):
            for bad in (True, None, 0, "false", "missing"):
                scanner = owner()
                stopped = fixture(scanner, False)
                if bad == "missing": del stopped["State"][flag]
                else: stopped["State"][flag] = bad
                result, calls = self.close_with(scanner, fixture(scanner), stopped)
                self.assertFalse(result)
                self.assertNotIn(("rm", scanner.container_id), calls)
                self.assertNotIn("preRemoval", scanner.receipt.get("runtimeBoundaryObservations", {}))

    def test_failed_start_without_generation_still_fences_final_removal(self):
        for changed in (None, "created", "started"):
            scanner = owner()
            del scanner.receipt["containerGeneration"]
            before, stopped = fixture(scanner), fixture(scanner, False)
            before["HostConfig"]["Memory"] = 0  # Admission failed before successful startup.
            if changed == "created": stopped["Created"] = "2026-10-08T00:00:02Z"
            if changed == "started": stopped["State"]["StartedAt"] = "2026-10-08T00:00:02Z"
            result, calls = self.close_with(scanner, before, stopped)
            self.assertFalse(result)  # Original policy failure remains sticky in every branch.
            if changed is None:
                self.assertIn(("rm", scanner.container_id), calls)
                self.assertIn("preRemoval", scanner.receipt["runtimeBoundaryObservations"])
            else:
                self.assertNotIn(("rm", scanner.container_id), calls)
                self.assertNotIn("preRemoval", scanner.receipt.get("runtimeBoundaryObservations", {}))
            self.assertNotIn("containerGeneration", scanner.receipt)

    def test_uncertain_stopped_ownership_or_generation_refuses_removal(self):
        for kind in ("id", "image", "label", "generation", "running"):
            scanner = owner()
            stopped = fixture(scanner, False)
            if kind == "id": stopped["Id"] = "c" * 64
            if kind == "image": stopped["Image"] = "sha256:" + "c" * 64
            if kind == "label": stopped["Config"]["Labels"]["financial.acceptance.run"] = "foreign"
            if kind == "generation": stopped["Created"] = "2026-10-08T00:00:02Z"
            if kind == "running": stopped["State"]["Running"] = True
            result, calls = self.close_with(scanner, fixture(scanner), stopped)
            self.assertFalse(result)
            self.assertNotIn(("rm", scanner.container_id), calls)
            self.assertIn("containerId", scanner.receipt["resources"][0])


if __name__ == "__main__":
    unittest.main()
