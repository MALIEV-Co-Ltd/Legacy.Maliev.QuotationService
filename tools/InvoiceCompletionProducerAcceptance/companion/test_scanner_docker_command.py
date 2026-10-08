"""Pure causal fault controls; every process/FD/syscall below is mocked."""
import errno
import subprocess
import types
import unittest
from unittest.mock import patch

import scanner_docker_command as command


class Pipe:
    def __init__(self, fd):
        self.fd = fd
        self.closed = False

    def fileno(self):
        return self.fd

    def close(self):
        self.closed = True


class Process:
    pid = 700

    def __init__(self):
        self.stdout = Pipe(71)
        self.stderr = Pipe(72)
        self.returncode = None
        self.waits = 0

    def wait(self, timeout):
        self.waits += 1
        self.returncode = 0
        return 0


class Selector:
    def __init__(self):
        self.streams = []
        self.closed = False

    def register(self, stream, events):
        self.streams.append(stream)

    def unregister(self, stream):
        self.streams.remove(stream)

    def get_map(self):
        return self.streams

    def select(self, timeout):
        return [(types.SimpleNamespace(fileobj=stream), 1) for stream in list(self.streams)]

    def close(self):
        self.closed = True


class DockerCommandControls(unittest.TestCase):
    def setUp(self):
        command._OWNERS.clear()  # Mock owners only; never a production recovery API.
        command._HISTORY.clear()
        self.process = Process()
        self.selector = Selector()
        self.clock = 1.0
        self.exit_ready = True
        self.exit_code = 0
        self.reads = {71: [b"value\n", b""], 72: [b""]}
        self.patches = []
        self.mock("subprocess.Popen", return_value=self.process)
        self.mock("selectors.DefaultSelector", return_value=self.selector)
        self.mock("time.monotonic", side_effect=lambda: self.clock)
        self.mock("time.sleep", side_effect=self.advance)
        self.mock("os.getpid", return_value=10)
        self.mock("os.pidfd_open", return_value=90, create=True)
        self.mock("os.set_blocking")
        self.mock("os.close")
        self.mock("os.read", side_effect=self.read)
        self.mock("os.waitid", side_effect=self.waitid, create=True)
        self.mock("os.waitpid", side_effect=lambda pid, flags: (pid, self.exit_code << 8), create=True)
        self.mock("os.waitstatus_to_exitcode", side_effect=lambda status: status >> 8, create=True)
        self.mock("signal.pidfd_send_signal", side_effect=self.signal_exit, create=True)
        self.mock("signal.SIGKILL", 9, create=True)
        self.mock("_birth", return_value=(700, 900, 10, 700, 700))
        self.mock("_fd_pid", return_value=700)
        self.mock("_descriptor", return_value=(2, 90, 0, "anon_inode:[pidfd]"))
        self.mock("_pipe", side_effect=lambda stream: (stream.fd, 2, stream.fd + 1000))
        for key, value in {"P_PIDFD": 3, "WEXITED": 4, "WNOHANG": 1, "WNOWAIT": 0x1000000,
                           "CLD_EXITED": 1, "CLD_KILLED": 2, "CLD_DUMPED": 3}.items():
            self.mock("os." + key, value, create=True)

    def mock(self, target, *args, **kwargs):
        manager = patch("scanner_docker_command." + target, *args, **kwargs)
        result = manager.start()
        self.patches.append(manager)
        return result

    def advance(self, seconds):
        self.clock += seconds

    def waitid(self, kind, pidfd, flags):
        return types.SimpleNamespace(si_pid=700, si_code=1, si_status=self.exit_code) if self.exit_ready else None

    def signal_exit(self, *args):
        self.exit_ready = True

    def read(self, fd, maximum):
        return self.reads[fd].pop(0) if self.reads[fd] else b""

    def tearDown(self):
        for manager in reversed(self.patches):
            manager.stop()
        command._OWNERS.clear()
        command._HISTORY.clear()

    def assert_closed(self, pidfd=True):
        self.assertTrue(self.process.stdout.closed)
        self.assertTrue(self.process.stderr.closed)
        if pidfd:
            command.os.close.assert_called_with(90)

    def test_natural_exit_returns_private_text_and_releases_owner(self):
        self.assertEqual("value", command.run_docker(["version"]))
        self.assert_closed()
        self.assertTrue(self.selector.closed)
        self.assertEqual([], command._OWNERS)
        command.signal.pidfd_send_signal.assert_not_called()
        self.assertTrue(command.subprocess.Popen.call_args.kwargs["start_new_session"])

    def test_postbirth_selector_construction_fault_attempts_all_known_cleanup_but_quarantines_unknown_constructor(self):
        self.exit_ready = False
        command.selectors.DefaultSelector.side_effect = OSError("private setup failure")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.signal.pidfd_send_signal.assert_called_once_with(90, command.signal.SIGKILL, None, 0)
        self.assert_closed()
        lease = command._OWNERS[0]
        self.assertIsInstance(lease.original_failure, OSError)
        self.assertFalse(lease.receipt["selectorCloseCompleted"])
        self.assertTrue(lease.receipt["quarantined"])

    def test_register_failure_preserved_after_verified_known_handle_cleanup(self):
        self.selector.register = unittest.mock.Mock(side_effect=RuntimeError("register fault"))
        with self.assertRaisesRegex(RuntimeError, "register fault"):
            command.run_docker(["version"])
        self.assert_closed()
        self.assertTrue(self.selector.closed)
        self.assertEqual([], command._OWNERS)

    def timeout(self):
        self.exit_ready = False
        def select(_):
            self.clock = 40.0
            return []
        self.selector.select = select

    def test_timeout_kills_exact_pidfd_and_reaps_original_with_independent_closes(self):
        self.timeout()
        with self.assertRaises(TimeoutError):
            command.run_docker(["version"])
        command.signal.pidfd_send_signal.assert_called_once_with(90, command.signal.SIGKILL, None, 0)
        command.os.waitpid.assert_called_once_with(700, command.os.WNOHANG)
        self.assert_closed()
        self.assertEqual([], command._OWNERS)

    def test_kill_failure_does_not_skip_wait_readers_or_descriptor_closes_and_stays_sticky(self):
        self.timeout()
        command.signal.pidfd_send_signal.side_effect = OSError(errno.EPERM, "private kill fault")
        command.os.waitid.side_effect = [None, types.SimpleNamespace(si_pid=700, si_code=1, si_status=0)]
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.os.waitpid.assert_called_once_with(700, command.os.WNOHANG)
        self.assert_closed()
        self.assertTrue(self.selector.closed)
        lease = command._OWNERS[0]
        self.assertIsInstance(lease.original_failure, TimeoutError)
        self.assertEqual(1, len(lease.cleanup_failures))
        self.assertFalse(lease.receipt["cleanupVerified"])

    def test_wait_failure_does_not_skip_readers_selector_or_streams_but_retains_pidfd(self):
        self.timeout()
        command.os.waitpid.side_effect = ChildProcessError(errno.ECHILD, "original reaping absent")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed(pidfd=False)
        self.assertTrue(self.selector.closed)
        command.os.close.assert_not_called()
        self.assertFalse(command._OWNERS[0].receipt["originalReaped"])

    def test_selector_close_failure_does_not_skip_both_reader_and_pidfd_closes(self):
        self.selector.close = unittest.mock.Mock(side_effect=OSError("close selector fault"))
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed()
        self.assertFalse(command._OWNERS[0].receipt["selectorCloseCompleted"])

    def test_first_reader_close_failure_does_not_skip_second_or_pidfd(self):
        self.process.stdout.close = unittest.mock.Mock(side_effect=OSError("close reader fault"))
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assertTrue(self.process.stderr.closed)
        command.os.close.assert_called_with(90)
        self.assertFalse(command._OWNERS[0].receipt["bothReadersClosed"])

    def test_overflow_remains_original_error_after_cleanup_and_never_accumulates_over_cap(self):
        self.reads[71] = [b"x" * 4096] * 65 + [b""]
        with self.assertRaisesRegex(ValueError, "measurement bound"):
            command.run_docker(["version"])
        self.assert_closed()
        self.assertEqual([], command._OWNERS)

    def test_pidfd_binding_refusal_avoids_signal_and_retains_exact_known_handles(self):
        command._fd_pid.return_value = 701
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.signal.pidfd_send_signal.assert_not_called()
        command.os.close.assert_not_called()
        self.assert_closed(pidfd=False)
        self.assertIs(command._OWNERS[0].process, self.process)

    def test_partial_popen_birth_is_unknown_retained_and_blocks_next_birth(self):
        command.subprocess.Popen.side_effect = OSError("constructor may have partially acquired")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assertFalse(command._OWNERS[0].receipt["returnedProcessObserved"])
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.subprocess.Popen.assert_called_once()

    def test_inherited_reader_never_eof_refuses_cleanup_without_descendant_signal(self):
        self.timeout()
        command.os.read.side_effect = BlockingIOError()
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed()
        self.assertFalse(command._OWNERS[0].receipt["bothReadersEof"])
        self.assertFalse(command._OWNERS[0].receipt["descendantCleanupProved"])
        command.signal.pidfd_send_signal.assert_called_once()

    def test_missing_pidfd_api_is_quarantined_not_numeric_kill_fallback(self):
        command.os.pidfd_open.side_effect = NotImplementedError()
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.signal.pidfd_send_signal.assert_not_called()
        self.assertIs(command._OWNERS[0].process, self.process)

    def test_nonzero_exit_preserves_called_process_error_after_clean_cleanup(self):
        self.exit_code = 9
        with self.assertRaises(subprocess.CalledProcessError) as caught:
            command.run_docker(["version"])
        self.assertEqual(9, caught.exception.returncode)
        self.assert_closed()
        self.assertEqual([], command._OWNERS)

    def test_waitid_echild_never_becomes_success_zero_and_closes_readers_independently(self):
        command.os.waitid.side_effect = ChildProcessError(errno.ECHILD, "unowned exit")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed(pidfd=False)
        command.os.waitpid.assert_not_called()
        self.assertFalse(command._OWNERS[0].receipt["originalReaped"])

    def test_wrong_waitpid_child_refuses_physical_reaping_claim(self):
        command.os.waitpid.side_effect = None
        command.os.waitpid.return_value = (701, 0)
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed(pidfd=False)
        self.assertFalse(command._OWNERS[0].receipt["originalReaped"])

    def test_repeat_cleanup_never_recloses_successfully_closed_pidfd_or_original_readers(self):
        self.selector.close = unittest.mock.Mock(side_effect=OSError("selector sticky fault"))
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        lease = command._OWNERS[0]
        command.os.close.assert_called_once_with(90)
        lease.cleanup()
        command.os.close.assert_called_once_with(90)
        self.selector.close.assert_called_once()
        self.assertFalse(lease.receipt["cleanupVerified"])

    def test_uncertain_pidfd_close_is_never_retried_even_when_a_new_close_would_succeed(self):
        command.os.close.side_effect = OSError(errno.EINTR, "uncertain close")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        lease = command._OWNERS[0]
        command.os.close.side_effect = None
        lease.cleanup()
        command.os.close.assert_called_once_with(90)
        self.assertFalse(lease.receipt["pidfdCloseCompleted"])
        self.assertTrue(lease.receipt["quarantined"])

    def test_timeout_validation_refuses_before_birth(self):
        for timeout in (0, -1, 301, True, float("nan"), float("inf")):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                command.run_docker(["version"], timeout=timeout)
        command.subprocess.Popen.assert_not_called()

    def test_post_waitpid_late_reaping_preserves_physical_fact_and_refuses_deadline(self):
        def late_reap(pid, flags):
            self.clock = 40.0
            return (pid, 0)
        command.os.waitpid.side_effect = late_reap
        with self.assertRaises(TimeoutError):
            command.run_docker(["version"])
        receipt = command.command_receipts()[0]
        self.assertTrue(receipt["originalReaped"])
        self.assertTrue(receipt["originalFailure"])
        self.assertTrue(receipt["cleanupVerified"])

    def test_changed_pidfd_identity_refuses_close_after_original_reap(self):
        command._descriptor.side_effect = lambda fd: (2, 91 if self.process.returncode is not None else 90, 0, "anon_inode:[pidfd]")
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        self.assert_closed(pidfd=False)
        command.os.close.assert_not_called()
        self.assertFalse(command.command_receipts()[0]["pidfdCloseCompleted"])

    def test_readonly_bounded_ledger_survives_clean_release_without_private_arguments(self):
        command.run_docker(["version", "private-canary-argument"])
        receipt = command.command_receipts()[0]
        self.assertEqual(0, receipt["originalExitCode"])
        self.assertTrue(receipt["bothReadersEof"])
        self.assertTrue(receipt["cleanupVerified"])
        self.assertNotIn("private-canary-argument", repr(receipt))
        with self.assertRaises(TypeError):
            receipt["cleanupVerified"] = False
        command._HISTORY[:] = [dict(receipt)] * 256
        with self.assertRaises(command.DockerLifecycleError):
            command.run_docker(["version"])
        command.subprocess.Popen.assert_called_once()

    def test_concurrent_lifetime_fence_refuses_before_birth(self):
        command._FENCE.acquire()
        try:
            with self.assertRaises(command.DockerLifecycleError):
                command.run_docker(["version"])
        finally:
            command._FENCE.release()
        command.subprocess.Popen.assert_not_called()

    def test_cleanup_bookkeeping_failure_retains_owner_and_attempts_failure_snapshot(self):
        with patch.object(command._Lease, "cleanup", side_effect=RuntimeError("private bookkeeping")):
            with self.assertRaises(command.DockerLifecycleError):
                command.run_docker(["version"])
        self.assertIs(command._OWNERS[0].process, self.process)
        self.assertTrue(command.command_receipts()[0]["quarantined"])
        self.assertFalse(command.command_receipts()[0]["cleanupVerified"])

    def test_transient_postbirth_metadata_failure_recovers_from_already_held_pidfd_but_original_fault_remains(self):
        count = 0
        def birth(pid):
            nonlocal count
            count += 1
            if count == 1:
                raise OSError("metadata transient")
            return (700, 900, 10, 700, 700)
        command._birth.side_effect = birth
        with self.assertRaisesRegex(OSError, "metadata transient"):
            command.run_docker(["version"])
        command.os.pidfd_open.assert_called_once_with(700, 0)
        self.assert_closed()
        receipt = command.command_receipts()[0]
        self.assertTrue(receipt["originalFailure"])
        self.assertTrue(receipt["generationBound"])
        self.assertTrue(receipt["cleanupVerified"])

    def test_transient_reader_setup_failure_recovers_original_stream_without_losing_held_process(self):
        count = 0
        def pipe(stream):
            nonlocal count
            count += 1
            if count == 1:
                raise OSError("reader setup transient")
            return (stream.fd, 2, stream.fd + 1000)
        command._pipe.side_effect = pipe
        with self.assertRaisesRegex(OSError, "reader setup transient"):
            command.run_docker(["version"])
        self.assert_closed()
        command.os.pidfd_open.assert_called_once_with(700, 0)
        self.assertTrue(command.command_receipts()[0]["cleanupVerified"])


if __name__ == "__main__":
    unittest.main()
