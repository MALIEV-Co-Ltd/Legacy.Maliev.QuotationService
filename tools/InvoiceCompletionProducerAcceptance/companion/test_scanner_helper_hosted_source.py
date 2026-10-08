"""Modeled receipt-admission controls only. Never calls run_case/load_helper/main."""
import unittest
from unittest.mock import patch
import scanner_helper_hosted_controls as controls


def model(case):
    flags = {key: True for key in controls.FLAGS}
    for key in ("SourceOriginalFailure", "SourceQuarantined", "SelectorRegisterFaultInjected",
                "PostSignalFaultInjected", "PostWaitidFaultInjected", "ActualKillDelegated",
                "StickyQuarantineRetained", "RecoveryAttempted", "NaturalControlExitZeroObserved"):
        flags[key] = False
    if case == "natural":
        flags["NaturalControlExitZeroObserved"] = True
    elif case == "selector-register":
        flags.update(SourceOriginalFailure=True, SelectorRegisterFaultInjected=True)
    else:
        flags.update(SourceOriginalFailure=True, SourceCleanupVerified=False, SourceQuarantined=True,
                     ActualKillDelegated=True, StickyQuarantineRetained=True)
        flags["PostSignalFaultInjected" if case == "post-signal" else "PostWaitidFaultInjected"] = True
        if case == "post-waitid":
            flags.update(SourceOriginalReaped=False, RecoveryAttempted=True)
    return flags


class SourceAdmissionControls(unittest.TestCase):
    def test_four_modeled_matrices_and_setup_live_child_variation_admit(self):
        for case in controls.CASES:
            with self.subTest(case=case):
                self.assertTrue(controls.admitted(case, model(case)))
        flags = model("selector-register")
        flags["ActualKillDelegated"] = True
        self.assertTrue(controls.admitted("selector-register", flags))

    def test_missing_actual_delegation_generation_eof_and_closure_evidence_never_admits(self):
        common = ("ActualPopenDelegated", "ActualGenerationBound", "ActualSelectorReturned", "ActualWaitidDelegated",
                  "ActualWaitpidOriginalReaped", "PhysicalOriginalReaped", "OriginalReadersEof",
                  "OriginalReadersClosed", "OriginalSelectorClosed", "OriginalPidfdClosed",
                  "OriginalPipeIdentitiesAbsent", "OriginalFailurePreserved")
        for case in controls.CASES:
            for key in common:
                with self.subTest(case=case, key=key):
                    flags = model(case)
                    flags[key] = False
                    self.assertFalse(controls.admitted(case, flags))

    def test_fault_wrapper_installed_but_not_causally_invoked_never_admits(self):
        for case, key in (("selector-register", "SelectorRegisterFaultInjected"),
                          ("post-signal", "PostSignalFaultInjected"), ("post-waitid", "PostWaitidFaultInjected")):
            flags = model(case)
            flags[key] = False
            self.assertFalse(controls.admitted(case, flags))
        for case in ("post-signal", "post-waitid"):
            flags = model(case)
            flags["ActualKillDelegated"] = False
            self.assertFalse(controls.admitted(case, flags))

    def test_recovered_physical_state_cannot_rewrite_original_failed_snapshot_or_quarantine(self):
        for case in ("post-signal", "post-waitid"):
            for key in ("SourceCleanupVerified", "SourceQuarantined", "SourceOriginalFailure", "StickyQuarantineRetained"):
                flags = model(case)
                flags[key] = not flags[key]
                self.assertFalse(controls.admitted(case, flags))
        flags = model("post-waitid")
        flags["SourceOriginalReaped"] = True
        self.assertFalse(controls.admitted("post-waitid", flags))
        flags = model("post-waitid")
        flags["RecoveryAttempted"] = False
        self.assertFalse(controls.admitted("post-waitid", flags))

    def test_unknown_keys_missing_flags_wrong_types_and_unknown_cases_refuse(self):
        for value in (0, 1, None, "true", [], {}):
            flags = model("natural")
            flags["PhysicalOriginalReaped"] = value
            with self.assertRaises(controls.ControlRefused):
                controls.admitted("natural", flags)
        flags = model("natural")
        flags["PrivatePid"] = 123
        with self.assertRaises(controls.ControlRefused):
            controls.admitted("natural", flags)
        flags = model("natural")
        del flags["OriginalPidfdClosed"]
        with self.assertRaises(controls.ControlRefused):
            controls.admitted("natural", flags)
        with self.assertRaises(controls.ControlRefused):
            controls.admitted("unknown", model("natural"))

    def test_pure_validator_never_creates_children_or_descriptors(self):
        with patch.object(controls.subprocess, "Popen", side_effect=AssertionError("actor creation forbidden")), patch.object(controls.os, "open", side_effect=AssertionError("FD acquisition forbidden")):
            for case in controls.CASES:
                self.assertTrue(controls.admitted(case, model(case)))

    def test_hosted_envelope_requires_exact_canonical_source_run_attempt_and_runner(self):
        values = {"FINANCIAL_SOURCE_HEAD": "a" * 40, "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1",
                  "GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}
        self.assertEqual({"SourceHead": "a" * 40, "RunId": 123, "Attempt": 1, "RunnerEnvironment": "github-hosted"}, controls.hosted_context(values))
        for key, wrong in (("FINANCIAL_SOURCE_HEAD", "A" * 40), ("FINANCIAL_SOURCE_HEAD", "a" * 41),
                           ("GITHUB_RUN_ID", "01"), ("GITHUB_RUN_ID", str(2**63)), ("GITHUB_RUN_ID", "0"),
                           ("GITHUB_RUN_ATTEMPT", "01"), ("GITHUB_RUN_ATTEMPT", str(2**31)),
                           ("RUNNER_ENVIRONMENT", "self-hosted"), ("GITHUB_ACTIONS", "false")):
            with self.subTest(key=key), self.assertRaises(controls.ControlRefused):
                controls.hosted_context({**values, key: wrong})

    def test_failure_category_never_uses_exception_message_or_dynamic_type_name(self):
        for error, expected in ((None, "None"), (controls.ControlRefused("private-path"), "ControlRefused"),
                                (TimeoutError("private-payload"), "Timeout"), (controls.InjectedRegisterFault("private"), "InjectedRegister"),
                                (controls.InjectedPostSignalFault("private"), "InjectedPostSignal"),
                                (controls.InjectedPostWaitidFault("private"), "InjectedPostWaitid"), (OSError("private-secret"), "Other")):
            self.assertEqual(expected, controls.error_category(error))
        secret_type = type("private-secret-dynamic-name", (Exception,), {})
        self.assertEqual("Other", controls.error_category(secret_type("private-data")))


if __name__ == "__main__":
    unittest.main()
