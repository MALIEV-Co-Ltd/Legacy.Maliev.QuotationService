import json
import unittest

from actual_application_health import parse_readiness
import hosted_companion_resources as h


class ApplicationHealthControls(unittest.TestCase):
    def test_healthy_dependencies_yield_only_opaque_digest_and_count(self):
        body = b'{"status":"Healthy","checks":{"database":{"status":"Healthy","duration":1}},"totalDuration":2}'
        result = parse_readiness(body)
        self.assertEqual({"checkCount", "bodySha256"}, set(result))
        self.assertEqual(1, result["checkCount"])

    def test_degraded_unhealthy_or_empty_dependency_reports_do_not_qualify(self):
        for status, checks in (("Degraded", {"db": {"status": "Healthy", "duration": 1}}),
                               ("Healthy", {"db": {"status": "Unhealthy", "duration": 1}}), ("Healthy", {})):
            with self.assertRaises(h.AdmissionError):
                parse_readiness(json.dumps({"status": status, "checks": checks, "totalDuration": 1}).encode())

    def test_duplicate_status_and_unbounded_or_non_numeric_duration_are_rejected(self):
        with self.assertRaises(h.AdmissionError):
            parse_readiness(b'{"status":"Unhealthy","status":"Healthy","checks":{},"totalDuration":1}')
        for duration in (True, -1, 60001, "1"):
            with self.assertRaises(h.AdmissionError):
                parse_readiness(json.dumps({"status": "Healthy", "checks": {"db": {"status": "Healthy", "duration": duration}},
                    "totalDuration": 1}).encode())

    def test_error_details_and_oversized_responses_are_rejected_before_retention(self):
        with self.assertRaises(h.AdmissionError):
            parse_readiness(b'{"status":"Healthy","checks":{"db":{"status":"Healthy","duration":1,"error":"not retained"}},"totalDuration":1}')
        with self.assertRaises(h.AdmissionError):
            parse_readiness(b" " * 65537)

    def test_invalid_encoding_and_deep_json_are_sanitized(self):
        for body in (b"\xff", b"[" * 2000 + b"0" + b"]" * 2000):
            with self.assertRaises(h.AdmissionError) as caught:
                parse_readiness(body)
            self.assertIn(str(caught.exception), ("Actual readiness JSON rejected", "Actual nonempty healthy dependency report required"))
            self.assertFalse(hasattr(caught.exception, "object"))


if __name__ == "__main__":
    unittest.main()
