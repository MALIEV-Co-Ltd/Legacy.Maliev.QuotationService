"""Proposed actual Ubuntu Docker CLI controls. No actor is created on import.

Injected faults follow delegated real operations; fixed public receipts never
contain command output, PID/FD identities, paths or exception text.
"""
import hashlib
import errno
import json
import os
import re
from pathlib import Path
import selectors
import signal
import stat
import subprocess
import sys
import time
import types

HELPER_SHA = "ea5097db7e52ca9138e22af276f59c95a12b827546edc53dfe29eaa6954544fc"
HELPER_BYTES = 17068
CASES = ("natural", "selector-register", "post-signal", "post-waitid")
STAGES = ("admission", "load", "control", "recovery", "receipt", "completed")
CATEGORIES = ("None", "ControlRefused", "Timeout", "Lifecycle", "InjectedRegister", "InjectedPostSignal", "InjectedPostWaitid", "Other")
_WITNESSES = []
FLAGS = ("ActualPopenDelegated", "ActualGenerationBound", "ActualSelectorReturned",
         "ActualWaitidDelegated", "ActualWaitpidOriginalReaped", "ActualKillDelegated",
         "SelectorRegisterFaultInjected", "PostSignalFaultInjected", "PostWaitidFaultInjected",
         "SourceOriginalFailure", "SourceCleanupVerified", "SourceQuarantined",
         "SourceOriginalReaped", "PhysicalOriginalReaped", "OriginalReadersEof",
         "OriginalReadersClosed", "OriginalSelectorClosed", "OriginalPidfdClosed",
         "OriginalPipeIdentitiesAbsent", "StickyQuarantineRetained", "RecoveryAttempted",
         "OriginalFailurePreserved", "NaturalControlExitZeroObserved")


class ControlRefused(RuntimeError):
    pass


class InjectedRegisterFault(RuntimeError):
    pass


class InjectedPostSignalFault(RuntimeError):
    pass


class InjectedPostWaitidFault(RuntimeError):
    pass


def require(value):
    if not value:
        raise ControlRefused("Hosted control refused")


def hosted_context(environment):
    head = environment.get("FINANCIAL_SOURCE_HEAD", "")
    run = environment.get("GITHUB_RUN_ID", "")
    attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
    require(environment.get("GITHUB_ACTIONS") == "true" and environment.get("RUNNER_ENVIRONMENT") == "github-hosted")
    require(type(head) is str and len(head) == 40 and re.fullmatch(r"[0-9a-f]{40}", head) is not None)
    require(type(run) is str and 1 <= len(run) <= 20 and re.fullmatch(r"[1-9][0-9]*", run) is not None and int(run) <= 2**63 - 1)
    require(type(attempt) is str and 1 <= len(attempt) <= 10 and re.fullmatch(r"[1-9][0-9]*", attempt) is not None and int(attempt) <= 2**31 - 1)
    return {"SourceHead": head, "RunId": int(run), "Attempt": int(attempt), "RunnerEnvironment": "github-hosted"}


def error_category(error, helper=None):
    known = {ControlRefused: "ControlRefused", TimeoutError: "Timeout", InjectedRegisterFault: "InjectedRegister",
             InjectedPostSignalFault: "InjectedPostSignal", InjectedPostWaitidFault: "InjectedPostWaitid"}
    if error is None:
        return "None"
    if helper is not None and type(error) is helper.DockerLifecycleError:
        return "Lifecycle"
    return known.get(type(error), "Other")


def read_qualified(path, expected_size, expected_sha):
    fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_size == expected_size)
        value = bytearray()
        while len(value) <= expected_size:
            chunk = os.read(fd, min(4096, expected_size + 1 - len(value)))
            if not chunk:
                break
            value.extend(chunk)
        after = os.fstat(fd)
        identity = lambda row: (row.st_dev, row.st_ino, row.st_size, row.st_mtime_ns, row.st_ctime_ns)
        require(identity(before) == identity(after) and len(value) == expected_size)
        require(hashlib.sha256(value).hexdigest() == expected_sha)
        return bytes(value)
    finally:
        os.close(fd)


def load_helper():
    path = Path(__file__).resolve().parent / "scanner_docker_command.py"
    source = read_qualified(path, HELPER_BYTES, HELPER_SHA)
    module = types.ModuleType("qualified_scanner_docker_command")
    module.__file__ = str(path)
    # Held exact bytes, not reopening the pathname, supply the executed module.
    exec(compile(source, "qualified_scanner_docker_command", "exec"), module.__dict__)
    return module


def admitted(case, flags):
    require(case in CASES and type(flags) is dict and set(flags) == set(FLAGS))
    require(all(type(value) is bool for value in flags.values()))
    common = ("ActualPopenDelegated", "ActualGenerationBound", "ActualSelectorReturned",
              "ActualWaitidDelegated", "ActualWaitpidOriginalReaped", "PhysicalOriginalReaped",
              "OriginalReadersEof", "OriginalReadersClosed", "OriginalSelectorClosed",
              "OriginalPidfdClosed", "OriginalPipeIdentitiesAbsent", "OriginalFailurePreserved")
    if not all(flags[key] for key in common):
        return False
    expected = {
        "natural": (False, True, False, True, False, False, False, False, False, True),
        "selector-register": (True, True, False, True, True, False, False, None, False, False),
        "post-signal": (True, False, True, True, False, True, False, True, False, False),
        "post-waitid": (True, False, True, False, False, False, True, True, True, False),
    }[case]
    keys = ("SourceOriginalFailure", "SourceCleanupVerified", "SourceQuarantined",
            "SourceOriginalReaped", "SelectorRegisterFaultInjected", "PostSignalFaultInjected",
            "PostWaitidFaultInjected", "ActualKillDelegated", "RecoveryAttempted", "NaturalControlExitZeroObserved")
    return all(value is None or flags[key] is value for key, value in zip(keys, expected)) and flags["StickyQuarantineRetained"] == flags["SourceQuarantined"]


class Witness:
    def __init__(self, helper, case):
        self.helper = helper
        self.case = case
        self.flags = {key: False for key in FLAGS}
        self.process = None
        self.lease = None
        self.epoll = None
        self.selector_actual = None
        self.cleanup_errors = []
        self.cleanup_error_stages = []
        self.control_error = None
        self.original_failure = None
        self.original_cleanup_failures = ()
        self.pipe_identities = ()
        self.originals = (subprocess.Popen, selectors.DefaultSelector, signal.pidfd_send_signal, os.waitid, os.waitpid)
        self.reap_delegate = None

    def observe_binding(self):
        require(len(self.helper._OWNERS) == 1)
        self.lease = self.helper._OWNERS[0]
        require(self.lease.process is self.process and self.lease.receipt["generationBound"])
        self.lease.require_original()
        require(self.lease.birth[2] == os.getpid() and self.lease.birth[3:] == (self.process.pid, self.process.pid))
        require(self.helper._pipe(self.process.stdout) == self.lease.bindings[self.process.stdout])
        require(self.helper._pipe(self.process.stderr) == self.lease.bindings[self.process.stderr])
        self.pipe_identities = tuple((row[1], row[2]) for row in self.lease.bindings.values())
        require(len(set(self.pipe_identities)) == 2)
        self.flags["ActualGenerationBound"] = True

    def install(self):
        witness = self
        popen, selector, send, waitid, waitpid = self.originals
        def start(*args, **kwargs):
            value = popen(*args, **kwargs)
            # Retain immediately; no fallible metadata work before handoff.
            witness.process = value
            witness.flags["ActualPopenDelegated"] = True
            return value
        def make_selector():
            actual = selector()
            witness.selector_actual = actual
            witness.epoll = actual._selector
            witness.flags["ActualSelectorReturned"] = True
            witness.observe_binding()
            class Delegate:
                def register(self, *args):
                    result = actual.register(*args)
                    if witness.case == "selector-register" and not witness.flags["SelectorRegisterFaultInjected"]:
                        witness.lease.require_original()
                        witness.flags["SelectorRegisterFaultInjected"] = True
                        raise InjectedRegisterFault()
                    return result
                def unregister(self, *args): return actual.unregister(*args)
                def select(self, *args): return actual.select(*args)
                def get_map(self): return actual.get_map()
                def close(self): return actual.close()
            return Delegate()
        def send_signal(fd, number, info=None, flags=0):
            witness.lease.require_original()
            require(fd == witness.lease.pidfd and number == signal.SIGKILL and info is None and flags == 0)
            result = send(fd, number, info, flags)
            witness.flags["ActualKillDelegated"] = True
            if witness.case == "post-signal" and not witness.flags["PostSignalFaultInjected"]:
                witness.flags["PostSignalFaultInjected"] = True
                raise InjectedPostSignalFault()
            return result
        def observe_exit(kind, fd, options):
            value = waitid(kind, fd, options)
            witness.flags["ActualWaitidDelegated"] = True
            frame = sys._getframe(1)
            if (witness.case == "post-waitid" and witness.flags["ActualKillDelegated"]
                    and not witness.flags["PostWaitidFaultInjected"] and value is not None
                    and frame.f_code is witness.helper._Lease.wait_original.__code__):
                require(kind == os.P_PIDFD and fd == witness.lease.pidfd and value.si_pid == witness.process.pid)
                require(value.si_code in (os.CLD_EXITED, os.CLD_KILLED, os.CLD_DUMPED))
                require(time.monotonic() < frame.f_locals["end"])
                witness.lease.require_original()
                witness.flags["PostWaitidFaultInjected"] = True
                raise InjectedPostWaitidFault()
            return value
        def reap(pid, options):
            require(witness.lease is not None and pid == witness.process.pid and options == os.WNOHANG)
            witness.lease.require_original()
            result = waitpid(pid, options)
            if result[0] == pid:
                witness.flags["ActualWaitpidOriginalReaped"] = True
            return result
        self.reap_delegate = reap
        subprocess.Popen, selectors.DefaultSelector = start, make_selector
        signal.pidfd_send_signal, os.waitid, os.waitpid = send_signal, observe_exit, reap

    def restore(self):
        subprocess.Popen, selectors.DefaultSelector, signal.pidfd_send_signal, os.waitid, os.waitpid = self.originals

    def retain_source_snapshot(self):
        rows = self.helper.command_receipts()
        require(len(rows) == 1 and self.lease is not None)
        row = rows[0]
        for target, origin in (("SourceOriginalFailure", "originalFailure"), ("SourceCleanupVerified", "cleanupVerified"),
                               ("SourceQuarantined", "quarantined"), ("SourceOriginalReaped", "originalReaped")):
            require(type(row[origin]) is bool)
            self.flags[target] = row[origin]
        self.original_failure = self.lease.original_failure
        self.original_cleanup_failures = tuple(self.lease.cleanup_failures)
        if self.case == "natural":
            self.flags["NaturalControlExitZeroObserved"] = self.process.returncode == 0 and self.lease.reaped

    def recover(self):
        if self.lease is None or self.lease.reaped:
            return
        require(self.helper._OWNERS == [self.lease])
        require(self.helper._FENCE.acquire(blocking=False))
        try:
            self.flags["RecoveryAttempted"] = True
            os.waitpid = self.reap_delegate
            # Qualification-only private lease recovery. No public API or fault
            # erasure: original history and sticky exception objects must remain.
            self.lease.cleanup()
        finally:
            os.waitpid = self.originals[4]
            self.helper._FENCE.release()

    def finish(self):
        require(self.lease is not None and self.process is self.lease.process)
        self.flags["PhysicalOriginalReaped"] = self.lease.reaped and self.process.returncode is not None
        self.flags["OriginalReadersEof"] = self.process.stdout in self.lease.eof and self.process.stderr in self.lease.eof
        self.flags["OriginalReadersClosed"] = self.process.stdout.closed and self.process.stderr.closed
        self.flags["OriginalSelectorClosed"] = self.epoll is not None and self.epoll.closed
        absent = False
        if self.lease.pidfd_closed and self.lease.pidfd_close_attempted:
            try:
                os.fstat(self.lease.pidfd)
            except OSError as error:
                absent = error.errno == errno.EBADF
        self.flags["OriginalPidfdClosed"] = self.lease.pidfd_closed and self.lease.pidfd_close_attempted and absent
        self.flags["StickyQuarantineRetained"] = self.helper._OWNERS == [self.lease] and self.lease.receipt["quarantined"]
        self.flags["OriginalFailurePreserved"] = (self.lease.original_failure is self.original_failure
                                                   and tuple(self.lease.cleanup_failures[:len(self.original_cleanup_failures)]) == self.original_cleanup_failures)
        # Independent bounded census of ONLY the two recorded pipe identities;
        # no before/after ambient FD set equality or all-handle inference.
        end = time.monotonic() + 1
        present = False
        with os.scandir("/proc/self/fd") as entries:
            for count, entry in enumerate(entries, 1):
                require(count <= 4096 and time.monotonic() < end)
                if not entry.name.isdecimal():
                    continue
                try:
                    value = os.fstat(int(entry.name))
                except OSError:
                    continue
                if (value.st_dev, value.st_ino) in self.pipe_identities:
                    present = True
        require(time.monotonic() < end)
        self.flags["OriginalPipeIdentitiesAbsent"] = bool(self.pipe_identities) and not present

    def close_unreturned_selector(self):
        # A constructor wrapper may have retained a returned real selector before
        # its fallible binding observation prevented source handoff. It remains
        # witness-owned; close that actual object, never a guessed integer.
        if self.selector_actual is not None and (self.lease is None or self.lease.selector is None):
            self.selector_actual.close()


def run_case(helper, case):
    require(not _WITNESSES and case in CASES)
    witness = Witness(helper, case)
    _WITNESSES.append(witness)  # Own before any child or selector acquisition.
    caught = None
    try:
        witness.install()
        args = ["--version"] if case in ("natural", "selector-register") else ["events", "--since", str(int(time.time())), "--format", "synthetic-owned-command-observation"]
        try:
            helper.run_docker(args, timeout=5 if case in ("natural", "selector-register") else 1)
        except BaseException as error:
            caught = error
        witness.retain_source_snapshot()
        if case == "natural":
            require(caught is None)
        elif case == "selector-register":
            require(type(caught) is InjectedRegisterFault)
        else:
            require(type(caught) is helper.DockerLifecycleError and type(witness.original_failure) is TimeoutError)
    except BaseException as error:
        witness.control_error = error
    finally:
        # Restore real operations before all independent recovery attempts.
        witness.restore()
        for stage, action in (("recovery", witness.recover), ("recovery", witness.close_unreturned_selector), ("receipt", witness.finish)):
            try:
                action()
            except BaseException as error:
                witness.cleanup_errors.append(error)
                witness.cleanup_error_stages.append(stage)
    require(witness.control_error is None and not witness.cleanup_errors)
    require(admitted(case, witness.flags))
    return witness.flags


def main():
    receipt = None
    flags = None
    case = "unknown"
    failed = True
    stage = "admission"
    category = "None"
    context = None
    helper = None
    try:
        require(os.name == "posix" and sys.platform.startswith("linux") and os.environ.get("GITHUB_ACTIONS") == "true")
        require(all(not os.environ.get(key) for key in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH")))
        require(len(sys.argv) == 5 and sys.argv[1] == "--case" and sys.argv[3] == "--receipt")
        case = sys.argv[2]
        require(case in CASES)
        receipt = Path(sys.argv[4])
        require(not receipt.is_absolute() and ".." not in receipt.parts and receipt.parts[:2] == ("TestResults", "ScannerHelper"))
        require(not any(path.is_symlink() for path in (receipt, *receipt.parents)))
        require(receipt.resolve().is_relative_to(Path.cwd().resolve()))
        context = hosted_context(os.environ)
        def expired(*args):
            raise ControlRefused("Hosted control deadline expired")
        prior = signal.signal(signal.SIGALRM, expired)
        signal.setitimer(signal.ITIMER_REAL, 30)
        try:
            stage = "load"
            helper = load_helper()
            stage = "control"
            flags = run_case(helper, case)
            failed = False
            stage = "completed"
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, prior)
    except BaseException as error:
        # No private exception text, argument, metadata or trace is emitted.
        failed = True
        category = error_category(error, helper)
        if _WITNESSES:
            witness = _WITNESSES[0]
            flags = dict(witness.flags)
            if witness.control_error is not None:
                stage = "control"
                category = error_category(witness.control_error, helper)
            elif witness.cleanup_errors:
                stage = witness.cleanup_error_stages[0]
                category = error_category(witness.cleanup_errors[0], helper)
    if receipt is not None:
        value = {"SchemaVersion": 1, "Case": case, "Status": "failed" if failed else "passed",
                 "HelperSha256": HELPER_SHA, "Flags": flags,
                 "ActualBusinessGraphAccepted": False, "KernelCapsObserved": False,
                 "ParentDeathGuaranteeProved": False, "AllDescriptorsCensusProved": False,
                 "WitnessRetainedUntilHarnessExit": bool(_WITNESSES)}
        value.update(context or {"SourceHead": None, "RunId": None, "Attempt": None, "RunnerEnvironment": None})
        value.update(DiagnosticStage=stage, DiagnosticCategory=category)
        try:
            receipt.parent.mkdir(parents=True, exist_ok=True)
            receipt.write_text(json.dumps(value, separators=(",", ":")) + "\n", encoding="utf-8")
        except BaseException:
            failed = True
            stage, category = "receipt", "Other"
    print("SCANNER_HELPER_CONTROL_FAILED:" + stage + ":" + category if failed else "SCANNER_HELPER_CONTROL_COMPLETED")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
