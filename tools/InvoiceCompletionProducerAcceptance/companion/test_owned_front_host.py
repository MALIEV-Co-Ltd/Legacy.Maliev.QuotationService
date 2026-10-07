"""Real disposable pipe controls; no .NET host, socket worker or container started."""
import os
import unittest

from owned_front_host import PublicBootstrapPipe
import hosted_companion_resources as h


def public_frame():
    return {"Algorithm": "GOOG4-RSA-SHA256", "PublicKey": "public-spki-only",
            "File": {"Pid": 123, "StartedUtc": "2026-10-07T00:00:00Z", "ExecutableDll": "/source/File.dll",
                     "ExecutableSha256": "a" * 64, "RunId": "c821-11111111-1111-4111-8111-111111111111",
                     "ExpiresUtc": "2026-10-07T00:10:00Z"}}


class PublicBootstrapPipeTests(unittest.TestCase):
    def test_eof_seals_frame_while_parent_read_descriptor_survives(self):
        with PublicBootstrapPipe() as pipe:
            retained = pipe.read_fd
            pipe.seal(public_frame())
            self.assertTrue(os.read(retained, 4096).startswith(b'{"Algorithm":'))
            self.assertEqual(b"", os.read(retained, 1))
            self.assertIsNone(pipe.write_fd)
            os.fstat(retained)
        with self.assertRaises(OSError):
            os.fstat(retained)

    def test_cancellation_before_seal_closes_both_exact_descriptors(self):
        pipe = PublicBootstrapPipe()
        descriptors = (pipe.read_fd, pipe.write_fd)
        try:
            with pipe:
                raise RuntimeError("cancelled")
        except RuntimeError:
            pass
        for descriptor in descriptors:
            with self.assertRaises(OSError):
                os.fstat(descriptor)

    def test_second_seal_is_rejected_without_another_frame(self):
        with PublicBootstrapPipe() as pipe:
            pipe.seal(public_frame())
            with self.assertRaises(h.AdmissionError):
                pipe.seal(public_frame())
            data = os.read(pipe.read_fd, 4096)
            self.assertEqual(1, data.count(b'"Algorithm"'))
            self.assertEqual(b"", os.read(pipe.read_fd, 1))

    def test_secret_or_unknown_property_rejected_before_pipe_write(self):
        with PublicBootstrapPipe() as pipe:
            frame = public_frame()
            frame["PrivateKey"] = "forbidden"
            with self.assertRaises(h.AdmissionError):
                pipe.seal(frame)
            self.assertFalse(pipe.sealed)
            del frame["PrivateKey"]
            frame["File"]["BearerToken"] = "forbidden"
            with self.assertRaises(h.AdmissionError):
                pipe.seal(frame)
            self.assertFalse(pipe.sealed)

    def test_oversized_frame_rejected_without_partial_write(self):
        with PublicBootstrapPipe() as pipe:
            frame = public_frame()
            frame["PublicKey"] = "a" * 4096
            with self.assertRaises(h.AdmissionError):
                pipe.seal(frame)
            self.assertFalse(pipe.sealed)
            pipe.seal(public_frame())
            self.assertLess(len(os.read(pipe.read_fd, 4096)), 4096)
            self.assertEqual(b"", os.read(pipe.read_fd, 1))

    def test_disposal_is_idempotent_and_cannot_reopen(self):
        pipe = PublicBootstrapPipe()
        pipe.close()
        pipe.close()
        with self.assertRaises(h.AdmissionError):
            pipe.seal(public_frame())


if __name__ == "__main__":
    unittest.main()
