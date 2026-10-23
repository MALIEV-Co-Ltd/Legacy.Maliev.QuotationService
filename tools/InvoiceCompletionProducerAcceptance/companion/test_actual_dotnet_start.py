from datetime import datetime, timedelta, timezone
import hashlib
import io
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import actual_dotnet_start as a
import hosted_companion_resources as h


class ActualDotnetStartControls(unittest.TestCase):
    def test_environment_framing_matches_native_byte_order_and_detects_change(self):
        environment = {"z": "last", "A": "first", "???": "value"}
        framed = b"".join(sorted((key + "=" + value + "\0").encode() for key, value in environment.items()))
        self.assertEqual(hashlib.sha256(framed).hexdigest().upper(), a.environment_digest(environment))
        self.assertEqual(a.environment_digest(environment), a.environment_digest(dict(reversed(list(environment.items())))))
        self.assertNotEqual(a.environment_digest(environment), a.environment_digest({**environment, "z": "changed"}))

    def test_ambiguous_or_non_string_environment_is_rejected(self):
        for environment in ({"A=B": "value"}, {"A": "nul\0value"}, {"A": 1}, {"": "value"}):
            with self.assertRaises(h.AdmissionError):
                a.environment_digest(environment)

    def test_original_adapter_quarantine_propagates_without_public_pid_cleanup_fiction(self):
        root = Path("/source")
        spec = SimpleNamespace(repository=str(root), dotnet_executable="/runtime/dotnet", dotnet_sha256="a" * 64,
            executable_dll="/source/front.dll", executable_sha256="b" * 64)
        context = SimpleNamespace(validate=lambda *_: None, lease_id="c821-11111111-1111-4111-8111-111111111111",
            expires_utc=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat(), run_id="123", attempt=1)
        parent = {"ParentPid": 10, "ParentKernelStartTicks": 20, "ParentExecutablePath": "/runtime/python",
            "ParentExecutableSha256": "c" * 64, "ParentScriptPath": "/source/parent.py", "ParentScriptSha256": "d" * 64}
        target = SimpleNamespace(pid=123, poll=lambda: None)
        failure = a.sdk_observer.ObserverLifecycleError("SDK observer original owner quarantined")
        with patch.object(Path, "is_absolute", return_value=True), patch.object(Path, "resolve", lambda path: path), \
             patch.object(Path, "is_symlink", return_value=False), \
             patch.object(Path, "is_dir", return_value=True), patch.object(a, "regular_hash", return_value="e" * 64), \
             patch.object(a.sdk_observer, "observe", side_effect=failure) as observe:
            with self.assertRaises(h.AdmissionError) as caught:
                a.observe_start("Front", target, 40, spec, {}, context, {}, parent,
                    "/source/observer.dll", "e" * 64, "/source/front.json")
        self.assertEqual(str(caught.exception), "Exact observer original lifecycle refused")
        self.assertEqual(observe.call_count, 1)
        self.assertFalse(hasattr(caught.exception, "resource"))



if __name__ == "__main__":
    unittest.main()
