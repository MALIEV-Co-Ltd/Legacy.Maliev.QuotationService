"""Synthetic parser/held-read models only. Never native lifecycle receipts."""
import copy
import json
import stat
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import check_storage_command_receipt as checker

HEAD, RUN, ATTEMPT = "a" * 40, "17", "1"


def model(case, recovery=False):
    first = {key: False for key in checker.BOOL_KEYS}
    for key in checker.PHYSICAL:
        first[key] = True
    first.update(ExitCode=0, CleanupAttempts=1, StdoutBytesRead=10, StderrBytesRead=0)
    if case == "nonzero-next-read":
        first.update(ExitCode=7, StdoutBytesRead=0)
        second = copy.deepcopy(first)
        second.update(ExitCode=0, StdoutBytesRead=10)
        records = [first, second]
    else:
        records = [first]
    if case in ("cancel", "stdout-cap", "stderr-cap"):
        for key in checker.BOUND_SIGNAL:
            first[key] = True
        first.update(PidfdClosed=True, ExitCode=137, StdoutBytesRead=0)
        if case == "stdout-cap":
            first["StdoutBytesRead"] = 4096
        if case == "stderr-cap":
            first["StderrBytesRead"] = 20480
        if recovery:
            first.update(PhysicalReleased=False, RetainedOwner=True, StdoutClosed=False,
                StderrClosed=False, PidfdClosed=False, ProcessClosed=False, StdoutEof=False,
                CleanupFailureRecorded=True, AdmissionSticky=True)
            second = copy.deepcopy(first)
            for key in checker.PHYSICAL:
                second[key] = True
            second.update(RetainedOwner=False, PidfdClosed=True, CleanupAttempts=2)
            records = [first, second]
    row = {key: False for key in checker.FALSE_SCOPE}
    row.update(SchemaVersion=1, SourceHead=HEAD, RunId=RUN, Attempt=ATTEMPT, Case=case,
        Passed=True, Stage="completed", Category="None", Observations=records,
        ObservedLoadedRuntime="10.0.12")
    return row


class ReceiptModels(unittest.TestCase):
    def admit(self, row):
        return checker.validate_bytes(json.dumps(row).encode(), HEAD, RUN, ATTEMPT, row.get("Case", "natural"))

    def refuse(self, row):
        with self.assertRaises(checker.ReceiptRefused):
            self.admit(row)

    def test_five_clean_case_models_and_three_supported_recovery_shapes(self):
        for case in checker.CASES:
            with self.subTest(case=case):
                self.assertEqual(self.admit(model(case))["StorageCommandCaseAccepted"], case)
        for case in ("cancel", "stdout-cap", "stderr-cap"):
            with self.subTest(case=case, recovery=True):
                row = model(case, True)
                self.assertTrue(self.admit(row)["HistoricalCleanupFailureRetained"])
                self.assertFalse(row["Observations"][0]["PhysicalReleased"])

    def test_every_top_field_missing_or_unknown_is_refused(self):
        for key in checker.ROOT_KEYS:
            row = model("natural")
            row.pop(key)
            self.refuse(row)
        row = model("natural")
        row["PrivatePid"] = 17
        self.refuse(row)

    def test_failed_quarantined_or_uncertain_records_never_admit(self):
        for key, value in (("Passed", False), ("ActualRegistryRetainedOwner", True),
                           ("Stage", "quarantine"), ("Category", "CleanupUnsettled")):
            row = model("stdout-cap", True)
            row[key] = value
            self.refuse(row)
        row = model("stdout-cap", True)
        row["Observations"] = row["Observations"][:1]
        self.refuse(row)

    def test_context_types_overflow_and_foreign_head_are_refused(self):
        for key, value in (("SourceHead", "b" * 40), ("RunId", 17), ("RunId", "018"),
                           ("Attempt", True), ("Case", "other"), ("SchemaVersion", True)):
            row = model("natural")
            row[key] = value
            self.refuse(row)
        for run, attempt in ((str(2**63), "1"), ("1", str(2**31)), ("01", "1"),
                             ("1", "0"), ("x" * 100, "1")):
            with self.assertRaises(checker.ReceiptRefused):
                checker.validate_bytes(json.dumps(model("natural")).encode(), HEAD, run, attempt, "natural")

    def test_all_false_scope_fields_must_remain_exact_false(self):
        for key in checker.FALSE_SCOPE:
            for value in (True, 0, None, "false"):
                row = model("natural")
                row[key] = value
                self.refuse(row)

    def test_every_observation_field_is_required_and_strictly_typed(self):
        for key in checker.OBS_KEYS:
            row = model("natural")
            row["Observations"][0].pop(key)
            self.refuse(row)
        for key in checker.BOOL_KEYS:
            row = model("natural")
            row["Observations"][0][key] = 1
            self.refuse(row)
        for key in ("ExitCode", "CleanupAttempts", "StdoutBytesRead", "StderrBytesRead"):
            row = model("natural")
            row["Observations"][0][key] = True
            self.refuse(row)

    def test_terminal_managed_fields_and_exact_known_nonzero_next_read(self):
        for key in checker.PHYSICAL:
            row = model("natural")
            row["Observations"][0][key] = False
            self.refuse(row)
        for index, key, value in ((0, "ExitCode", 8), (1, "ExitCode", 7),
                                 (1, "StdoutBytesRead", 9), (0, "StderrBytesRead", 1)):
            row = model("nonzero-next-read")
            row["Observations"][index][key] = value
            self.refuse(row)

    def test_actual_cap_bytes_and_bound_signal_required_not_category_only(self):
        for case, key, bound in (("stdout-cap", "StdoutBytesRead", 64),
                                 ("stderr-cap", "StderrBytesRead", 16384)):
            row = model(case)
            row["Observations"][0][key] = bound
            self.refuse(row)
        for key in checker.BOUND_SIGNAL:
            row = model("cancel")
            row["Observations"][0][key] = False
            self.refuse(row)

    def test_silent_opposite_channels_stay_zero_in_every_initial_and_recovery_record(self):
        for case, keys in (("stdout-cap", ("StderrBytesRead",)),
                           ("stderr-cap", ("StdoutBytesRead",)),
                           ("cancel", ("StdoutBytesRead", "StderrBytesRead"))):
            for recovery in (False, True):
                for index in range(2 if recovery else 1):
                    for key in keys:
                        row = model(case, recovery)
                        row["Observations"][index][key] = 1
                        self.refuse(row)

    def test_recovery_must_preserve_original_failure_counters_and_attempt_limit(self):
        for index, key, value in ((0, "PhysicalReleased", True), (0, "RetainedOwner", False),
                (0, "CleanupAttempts", 2), (1, "CleanupAttempts", 3),
                (1, "CleanupFailureRecorded", False), (1, "AdmissionSticky", False),
                (1, "StdoutBytesRead", 1), (1, "ExitCode", 0), (1, "RetainedOwner", True)):
            row = model("stdout-cap", True)
            row["Observations"][index][key] = value
            self.refuse(row)

    def test_duplicate_nonfinite_oversize_and_unknown_nested_fields(self):
        for raw in (b'{"SchemaVersion":1,"SchemaVersion":1}', b'{"x":NaN}',
                    b" " * (checker.MAX_BYTES + 1), b"\xff"):
            with self.assertRaises(checker.ReceiptRefused):
                checker.validate_bytes(raw, HEAD, RUN, ATTEMPT, "natural")
        row = model("natural")
        row["Observations"][0]["PrivateDescriptor"] = 31
        self.refuse(row)

    def test_overlong_json_integer_normalizes_to_fixed_receipt_refusal(self):
        raw = b'{"x":' + b"9" * 5000 + b'}'
        with self.assertRaises(checker.ReceiptRefused):
            checker.validate_bytes(raw, HEAD, RUN, ATTEMPT, "natural")

    def test_native_runtime_field_finite_not_installer_or_private_path(self):
        for value in (None, "10", "11.0.0", "/private/runtime", "10.0." + "1" * 50):
            row = model("natural")
            row["ObservedLoadedRuntime"] = value
            self.refuse(row)

    def test_source_gate_reads_only_fixed_original_source_pins_and_refuses_changed_bytes(self):
        with patch.object(checker, "held_bytes", return_value=b"changed") as held:
            with self.assertRaises(checker.ReceiptRefused):
                checker.verify_sources("synthetic-checkout")
            held.assert_called_once()
        paths = [row[0] for row in checker.SOURCE_PINS]
        self.assertEqual(len(paths), 3)
        self.assertTrue(paths[0].endswith("/BoundedOwnedCommand.cs"))


class HeldReadModels(unittest.TestCase):
    def read(self, before, after, chunks):
        with patch.object(checker.os, "O_NONBLOCK", 2048, create=True), \
             patch.object(checker.os, "O_NOFOLLOW", 131072, create=True), \
             patch.object(checker.os, "open", return_value=31) as opened, \
             patch.object(checker.os, "fstat", side_effect=[before, after]), \
             patch.object(checker.os, "read", side_effect=chunks), \
             patch.object(checker.os, "close") as closed:
            try:
                return checker.held_bytes("private-model-input")
            finally:
                opened.assert_called_once()
                closed.assert_called_once_with(31)

    def metadata(self, **changes):
        row = dict(st_mode=stat.S_IFREG | 0o600, st_nlink=1, st_size=3,
            st_dev=1, st_ino=2, st_mtime_ns=3, st_ctime_ns=4)
        row.update(changes)
        return SimpleNamespace(**row)

    def test_held_regular_exact_snapshot_and_close(self):
        self.assertEqual(self.read(self.metadata(), self.metadata(), [b"abc", b""]), b"abc")

    def test_fifo_links_oversize_or_changed_generation_refuse_and_close(self):
        for changes in (dict(st_mode=stat.S_IFIFO), dict(st_nlink=2),
                        dict(st_size=checker.MAX_BYTES + 1)):
            with self.assertRaises(checker.ReceiptRefused):
                self.read(self.metadata(**changes), self.metadata(), [])
        for changes in (dict(st_ino=9), dict(st_mtime_ns=9), dict(st_ctime_ns=9)):
            with self.assertRaises(checker.ReceiptRefused):
                self.read(self.metadata(), self.metadata(**changes), [b"abc", b""])

    def test_short_read_or_extra_bytes_cannot_admit(self):
        for chunks in ([b"ab", b""], [b"abcd", b""]):
            with self.assertRaises(checker.ReceiptRefused):
                self.read(self.metadata(), self.metadata(), chunks)


if __name__ == "__main__":
    unittest.main()
