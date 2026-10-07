"""Call the bounded native observer; never approximate .NET UTC using kernel btime."""
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import uuid
import time
from types import SimpleNamespace

import hosted_companion_resources as h
import owned_normal_hosts as normal
from regular_owned_files import regular_hash, verify_source


def environment_digest(environment):
    h.require(type(environment) is dict and all(type(k) is str and type(v) is str
              and k and "\0" not in k + v and "=" not in k for k, v in environment.items()),
              "Exact process environment required")
    records = sorted((name + "=" + value + "\0").encode("utf-8") for name, value in environment.items())
    return hashlib.sha256(b"".join(records)).hexdigest().upper()


def observe_start(owner, process, ticks, spec, actual_environment, context, launcher_environment,
                  parent_profile, observer_dll, observer_sha256, front_profile_path=""):
    context.validate(launcher_environment, datetime.now(timezone.utc))
    h.require(process.poll() is None and owner in normal.OWNERS | {"Front"}, "Held known child required")
    h.require((owner == "Front") == bool(front_profile_path), "Explicit front-only profile required")
    path = Path(observer_dll)
    root = Path(spec.repository)
    h.require(path.is_absolute() and path.resolve() == path and path.is_relative_to(root)
              and all(not item.is_symlink() for item in (path, *path.parents))
              and regular_hash(path) == observer_sha256, "Exact owned observer build required")
    parent = {"Pid": parent_profile["ParentPid"], "KernelStartTicks": parent_profile["ParentKernelStartTicks"],
              "Executable": parent_profile["ParentExecutablePath"],
              "ExecutableSha256": parent_profile["ParentExecutableSha256"].upper(),
              "Script": parent_profile["ParentScriptPath"], "ScriptSha256": parent_profile["ParentScriptSha256"].upper()}
    request = {"Owner": owner, "RunId": context.lease_id, "ExpiresUtc": context.expires_utc,
               "GithubRunId": context.run_id, "GithubAttempt": str(context.attempt), "Parent": parent,
               "Pid": process.pid, "KernelStartTicks": ticks, "DotnetExecutable": spec.dotnet_executable,
               "DotnetSha256": spec.dotnet_sha256.upper(), "ExecutableDll": spec.executable_dll,
               "ExecutableSha256": spec.executable_sha256.upper(),
               "ExpectedEnvironmentSha256": environment_digest(actual_environment),
               "FrontProfilePath": front_profile_path}
    directory = root / "TestResults/C821ProducerProfiles"
    h.require(directory.resolve() == directory and directory.is_dir()
              and all(not item.is_symlink() for item in (directory, *directory.parents)), "Owned private observer root required")
    private_path = directory / ("start-" + uuid.uuid4().hex + ".json")
    helper = None
    helper_ticks = None
    descriptor = None
    created = False
    try:
        descriptor = os.open(private_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        created = True
        with os.fdopen(descriptor, "wb") as stream:
            descriptor = None
            stream.write(json.dumps(request, separators=(",", ":")).encode("utf-8"))
        budget = min(7.0, (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds())
        h.require(budget > 0, "Actual start observation lease expired")
        previous = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM})
        try:
            helper = subprocess.Popen([spec.dotnet_executable, observer_dll, str(private_path)],
                env=launcher_environment, cwd=str(path.parent), stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, close_fds=True)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous)
        helper_ticks = h.process_start_ticks(h.bounded_file(f"/proc/{helper.pid}/stat", 4096), helper.pid)
        output, _ = helper.communicate(timeout=budget)
        h.require(helper.returncode == 0 and len(output) <= 16384, "Actual native start observer failed")
        result = json.loads(output)
        expected = {"Owner", "Pid", "StartedUtc", "KernelStartTicks", "Executable", "ExecutableSha256", "ExecutableDll", "DllSha256"}
        h.require(type(result) is dict and set(result) == expected and result["Owner"] == owner
                  and result["Pid"] == process.pid and result["KernelStartTicks"] == ticks
                  and result["Executable"] == spec.dotnet_executable
                  and result["ExecutableSha256"] == spec.dotnet_sha256.upper()
                  and result["ExecutableDll"] == spec.executable_dll and result["DllSha256"] == spec.executable_sha256.upper(),
                  "Native observation differs from held child")
        # Parse only for bounds. Return the exact seven-digit native timestamp unchanged.
        started = h.instant(result["StartedUtc"])
        h.require(started <= datetime.now(timezone.utc) < h.instant(context.expires_utc)
                  and process.poll() is None
                  and h.process_start_ticks(h.bounded_file(f"/proc/{process.pid}/stat", 4096), process.pid) == ticks,
                  "Observed child or lease changed")
        return result
    finally:
        try:
            if helper is not None and helper.poll() is None:
                current = h.process_start_ticks(h.bounded_file(f"/proc/{helper.pid}/stat", 4096), helper.pid)
                h.require(helper_ticks is None or current == helper_ticks, "Observer generation uncertain; preserve exact handle")
                helper_ticks = current
                helper.terminate()
                try:
                    helper.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    h.require(h.process_start_ticks(h.bounded_file(f"/proc/{helper.pid}/stat", 4096), helper.pid)
                              == helper_ticks, "Observer generation changed before force")
                    helper.kill()
                    helper.wait(timeout=5)
        except BaseException as error:
            # Public identity only; caller can retain exact ownership/expiry on uncertainty.
            failure = h.AdmissionError("Exact observer cleanup failed")
            failure.resource = {"owner": "FrontStartObserver", "pid": helper.pid,
                                "kernelStartTicks": helper_ticks, "expiresUtc": context.expires_utc,
                                "errorType": type(error).__name__}
            raise failure from None
        finally:
            try:
                if descriptor is not None:
                    os.close(descriptor)
            finally:
                try:
                    if helper is not None and helper.stdout is not None:
                        helper.stdout.close()
                finally:
                    # Delete only the exact request created by this invocation.
                    if created:
                        private_path.unlink(missing_ok=True)


def observe_normal_starts(normal_owner, parent_profile, observer_dll, observer_sha256, owners=None):
    """Adapt held normal HostSpec rows; no observation timestamp is promoted to UTC."""
    held = [row[0].owner for row in normal_owner.owned]
    requested = normal.OWNERS if owners is None else frozenset(owners)
    h.require(not normal_owner.closed and not normal_owner.cleanup_failures
              and len(held) == len(set(held)) and set(held).issubset(normal.OWNERS)
              and requested and requested.issubset(normal.OWNERS) and requested.issubset(held)
              and (owners is not None or set(held) == normal.OWNERS),
              "Exact requested already-held normal hosts required")
    context = normal_owner.context
    observations = {}
    for spec, process, ticks, _ in normal_owner.owned:
        if spec.owner not in requested:
            continue
        context.validate(normal_owner.environment, datetime.now(timezone.utc))
        deadline = time.monotonic() + min(10, (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds())
        verify_source(spec, deadline)
        expected = dict(spec.environment)
        expected.update({"GITHUB_ACTIONS": "true", "GITHUB_RUN_ID": context.run_id,
            "GITHUB_RUN_ATTEMPT": str(context.attempt), "C821_FIXTURE_RUN_ID": context.lease_id,
            "C821_FIXTURE_EXPIRES_UTC": context.expires_utc,
            "DOTNET_GCHeapHardLimit": format(spec.heap_limit_bytes, "x")})
        actual = h.process_environment(h.bounded_file(f"/proc/{process.pid}/environ", 262144))
        h.require(actual == expected and process.poll() is None and ticks is not None,
                  "Actual complete normal environment/handle differs")
        # Observer assembly and private requests belong to Quotation's workspace;
        # the target DLL remains the source-admitted peer's actual DLL.
        adapter = SimpleNamespace(repository=parent_profile["Repository"],
            dotnet_executable=normal_owner.dotnet, dotnet_sha256=normal_owner.dotnet_hash,
            executable_dll=spec.executable_dll, executable_sha256=spec.executable_sha256)
        observations[spec.owner] = observe_start(spec.owner, process, ticks, adapter, expected,
            context, normal_owner.environment, parent_profile, observer_dll, observer_sha256)
        verify_source(spec, time.monotonic() + min(10, (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds()))
    return observations
