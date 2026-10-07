import json
import socket
import struct
import threading
import time
from datetime import datetime, timezone
import unittest
from unittest.mock import patch

from hosted_scanner_readiness import Scanner, database_hashes, IMAGE, CONFIG, EICAR


class ScannerTests(unittest.TestCase):
    def test_startup_diagnostic_records_exact_owned_runtime_state_and_ports(self):
        scanner = Scanner("diagnostic-run")
        scanner.container_id = "a" * 64
        scanner.image_id = "sha256:" + "b" * 64
        state = {"Status": "exited", "Running": False, "Paused": False, "Restarting": False, "Pid": 0, "ExitCode": 2, "OOMKilled": False}
        container = {"Id": scanner.container_id, "Image": scanner.image_id, "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}},
                     "State": state, "HostConfig": {"PortBindings": {"3310/tcp": [{"HostIp": "127.0.0.1", "HostPort": "0"}]}},
                     "NetworkSettings": {"Ports": {}, "Networks": {}}}
        with patch.object(scanner, "docker", side_effect=[json.dumps([container]), "synthetic startup failure"]):
            diagnostic = scanner.startup_diagnostic()
        self.assertTrue(diagnostic["observed"])
        self.assertEqual(2, diagnostic["state"]["ExitCode"])
        self.assertEqual({}, diagnostic["actualPortBindings"])
        self.assertEqual("synthetic startup failure", diagnostic["startupLogTail"])

    def test_startup_diagnostic_refuses_foreign_container_and_does_not_read_logs(self):
        scanner = Scanner("diagnostic-run")
        scanner.container_id = "a" * 64
        scanner.image_id = "sha256:" + "b" * 64
        container = {"Id": scanner.container_id, "Image": scanner.image_id, "Config": {"Labels": {"financial.acceptance.run": "foreign"}}}
        with patch.object(scanner, "docker", return_value=json.dumps([container])) as docker:
            self.assertFalse(scanner.startup_diagnostic()["observed"])
        self.assertEqual(1, docker.call_count)

    def test_startup_diagnostic_tail_is_bounded_and_log_failure_is_secondary(self):
        scanner = Scanner("diagnostic-run")
        scanner.container_id = "a" * 64
        scanner.image_id = "sha256:" + "b" * 64
        container = {"Id": scanner.container_id, "Image": scanner.image_id, "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}}}
        with patch.object(scanner, "docker", side_effect=[json.dumps([container]), "x" * 10000]):
            diagnostic = scanner.startup_diagnostic()
        self.assertEqual(8192, len(diagnostic["startupLogTail"]))
        self.assertTrue(diagnostic["startupLogTailTruncated"])
        with patch.object(scanner, "docker", side_effect=[json.dumps([container]), TimeoutError()]):
            diagnostic = scanner.startup_diagnostic()
        self.assertTrue(diagnostic["observed"])
        self.assertEqual("TimeoutError", diagnostic["startupLogErrorType"])

    def test_policy_rejects_unbounded_deadline_and_unowned_identity(self):
        for identity in ("../other", "", "a" * 65):
            with self.assertRaises(ValueError):
                Scanner(identity)
        for deadline in (0, 301):
            with self.assertRaises(ValueError):
                Scanner(deadline_seconds=deadline)

    def test_selected_image_is_immutable_and_config_has_no_updates(self):
        self.assertRegex(IMAGE, r"^clamav/clamav@sha256:[0-9a-f]{64}$")
        self.assertIn("SelfCheck 0\n", CONFIG)
        self.assertNotIn("freshclam", CONFIG)
        self.assertEqual(68, len(EICAR))

    def test_database_measurements_fail_closed(self):
        line = "a" * 64 + "  /var/lib/clamav/main.cvd"
        self.assertEqual("main.cvd", database_hashes(line)[0]["path"].split("/")[-1])
        for malformed in ("", line + "\n" + line, "a" * 64 + "  /tmp/main.cvd", "x" * 64 + "  /var/lib/clamav/main.cvd"):
            with self.assertRaises(ValueError):
                database_hashes(malformed)

    def test_scan_counts_observed_results_and_rejects_unknown(self):
        scanner = Scanner()
        with patch.object(scanner, "command", side_effect=["stream: OK", "stream: Eicar FOUND", "stream: scanner unavailable ERROR"]):
            self.assertEqual("clean", scanner.scan(b"clean"))
            self.assertEqual("infected", scanner.scan(b"synthetic control"))
            with self.assertRaises(ValueError):
                scanner.scan(b"failure")
        self.assertEqual({"attempted": 3, "clean": 1, "infected": 1}, scanner.counters())

    def test_actual_socket_framing_contains_complete_bytes(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        observed = []
        def server():
            with listener, listener.accept()[0] as connection:
                def read(size):
                    result = b""
                    while len(result) < size:
                        result += connection.recv(size - len(result))
                    return result
                observed.append(read(len(b"zINSTREAM\0")))
                data = b""
                while True:
                    length = struct.unpack("!I", read(4))[0]
                    if not length:
                        break
                    data += read(length)
                observed.append(data)
                connection.sendall(b"stream: OK\0")
        thread = threading.Thread(target=server)
        thread.start()
        scanner = Scanner()
        scanner.port = listener.getsockname()[1]
        payload = b"synthetic" * 12000
        self.assertEqual("clean", scanner.scan(payload))
        thread.join(timeout=3)
        self.assertFalse(thread.is_alive())
        self.assertEqual([b"zINSTREAM\0", payload], observed)

    def test_cleanup_refuses_foreign_container(self):
        scanner = Scanner("test-run")
        scanner.receipt["resources"] = [{"name": scanner.name}]
        calls = []
        def docker(*args, **kwargs):
            calls.append(args)
            if args[0] == "inspect":
                return json.dumps([{"Config": {"Labels": {"financial.acceptance.run": "foreign"}}}])
            return scanner.name
        with patch.object(scanner, "docker", side_effect=docker):
            self.assertFalse(scanner.close())
        self.assertFalse(any(args[0] in ("rm", "stop") for args in calls))
        self.assertFalse(scanner.receipt["genuineEightHostFinancialAccepted"])

    def test_uncertain_create_recovers_only_exact_owned_container(self):
        scanner = Scanner("recover-run")
        scanner.image_id = "sha256:" + "b" * 64
        scanner.receipt["allocationIssuedUtc"] = "2000-01-01T00:00:00+00:00"
        scanner.receipt["resources"] = [{"name": scanner.name}]
        observed = {"Id": "a" * 64, "Name": "/" + scanner.name, "Image": scanner.image_id,
                    "Created": datetime.now(timezone.utc).isoformat(),
                    "State": {"Running": False},
                    "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}}}
        calls = []
        census = iter(["a" * 64, ""])
        def docker(*args, **kwargs):
            calls.append(args)
            if args[0] == "ps":
                return next(census)
            if args[0] == "inspect":
                return json.dumps([observed])
            return ""
        with patch.object(scanner, "docker", side_effect=docker):
            self.assertTrue(scanner.close())
        self.assertIn(("rm", "a" * 64), calls)
        self.assertTrue(scanner.receipt["resources"][0]["reconciledAfterUncertainCreate"])

    def test_unavailable_probe_error_does_not_skip_exact_removal(self):
        scanner = Scanner("probe-error")
        scanner.container_id = "a" * 64
        scanner.port = 12345
        scanner.receipt["resources"] = [{"name": scanner.name}]
        calls = []
        def docker(*args, **kwargs):
            calls.append(args)
            if args[0] == "inspect":
                return json.dumps([{"Id": scanner.container_id, "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}}}])
            return ""
        with patch.object(scanner, "docker", side_effect=docker), patch.object(scanner, "command", side_effect=ValueError("bad framing")):
            self.assertFalse(scanner.close())
        self.assertIn(("rm", scanner.container_id), calls)

    def test_protocol_deadline_cannot_be_reset_by_trickle_response(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        scanner = Scanner()
        scanner.port = listener.getsockname()[1]
        def server():
            with listener, listener.accept()[0] as connection:
                connection.recv(64)
                try:
                    for _ in range(10):
                        connection.sendall(b"x")
                        time.sleep(0.04)
                except OSError:
                    pass
        thread = threading.Thread(target=server)
        thread.start()
        started = time.monotonic()
        with self.assertRaises(TimeoutError):
            scanner.command(b"PING", timeout=0.12)
        self.assertLess(time.monotonic() - started, 0.3)
        thread.join(timeout=1)
        self.assertFalse(thread.is_alive())

    def test_uncertain_network_create_recovers_single_owned_empty_network(self):
        scanner = Scanner("network-run")
        scanner.receipt["network"] = {"name": scanner.name + "-network", "allocationIssuedUtc": "2000-01-01T00:00:00+00:00"}
        observed = {"Id": "c" * 64, "Name": scanner.name + "-network", "Labels": {"financial.acceptance.run": scanner.run_id},
                    "Created": datetime.now(timezone.utc).isoformat(), "Containers": {}}
        calls = []
        census = iter(["c" * 64, ""])
        def docker(*args, **kwargs):
            calls.append(args)
            if args[:2] == ("network", "ls"):
                return next(census)
            if args[:2] == ("network", "inspect"):
                return json.dumps([observed])
            return ""
        with patch.object(scanner, "docker", side_effect=docker):
            self.assertTrue(scanner.close())
        self.assertIn(("network", "rm", "c" * 64), calls)
        self.assertTrue(scanner.receipt["network"]["reconciledAfterUncertainCreate"])


if __name__ == "__main__":
    unittest.main()
