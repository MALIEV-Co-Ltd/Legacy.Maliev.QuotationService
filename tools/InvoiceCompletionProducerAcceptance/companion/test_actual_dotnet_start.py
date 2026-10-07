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

    def test_private_request_and_stream_are_released_even_when_generation_cleanup_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            owned = root / "TestResults/C821ProducerProfiles"
            owned.mkdir(parents=True)
            observer = root / "observer.dll"
            observer.write_bytes(b"source-only-test-observer")
            spec = SimpleNamespace(repository=str(root), dotnet_executable="/runtime/dotnet", dotnet_sha256="a" * 64,
                executable_dll="/source/front.dll", executable_sha256="b" * 64)
            context = SimpleNamespace(validate=lambda *_: None, lease_id="c821-11111111-1111-4111-8111-111111111111",
                expires_utc=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat(), run_id="123", attempt=1)
            parent = {"ParentPid": 10, "ParentKernelStartTicks": 20, "ParentExecutablePath": "/runtime/python",
                "ParentExecutableSha256": "c" * 64, "ParentScriptPath": "/source/parent.py", "ParentScriptSha256": "d" * 64}
            helper = SimpleNamespace(pid=987, stdout=io.BytesIO(), poll=lambda: None)
            target = SimpleNamespace(pid=123, poll=lambda: None)
            with patch.object(a.os, "O_NOFOLLOW", 0, create=True), \
                 patch.object(a.signal, "SIGALRM", 14, create=True), \
                 patch.object(a.signal, "SIG_BLOCK", 0, create=True), \
                 patch.object(a.signal, "SIG_SETMASK", 2, create=True), \
                 patch.object(a.signal, "pthread_sigmask", return_value=set(), create=True), \
                 patch.object(a, "regular_hash", return_value=hashlib.sha256(observer.read_bytes()).hexdigest()), \
                 patch.object(a.subprocess, "Popen", return_value=helper), \
                 patch.object(h, "bounded_file", side_effect=OSError("unit observation unavailable")):
                with self.assertRaises(h.AdmissionError) as caught:
                    a.observe_start("Front", target, 40, spec, {}, context, {}, parent,
                                    str(observer), hashlib.sha256(observer.read_bytes()).hexdigest(), "/source/front.json")
            self.assertEqual(987, caught.exception.resource["pid"])
            self.assertTrue(helper.stdout.closed)
            self.assertEqual([], list(owned.iterdir()))


if __name__ == "__main__":
    unittest.main()
