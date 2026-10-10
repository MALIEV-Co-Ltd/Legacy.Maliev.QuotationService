using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

[CollectionDefinition("Docker context inspection environment", DisableParallelization = true)]
public sealed class DockerContextInspectionEnvironment;

[Collection("Docker context inspection environment")]
public sealed class DockerContextInspectionContractTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Work = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Cleanup = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task CallerAlreadyCancelled_DoesNotInspectOrAllocateAProcess()
    {
        var previous = Environment.GetEnvironmentVariable("DOCKER_HOST");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            // Invalid ambient configuration prevents the old code starting a real process.
            // A cancelled caller must be observed before any inspection admission work.
            Environment.SetEnvironmentVariable("DOCKER_HOST", "synthetic-forbidden-endpoint");
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DisposableContainerStartup.LocalDockerEndpointAsync(cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", previous);
        }
    }

    [Fact]
    public async Task HealthyExitedProcess_ReturnsOnlyTheCanonicalLocalEndpoint()
    {
        var process = new ScriptedProcess(exited: true);
        Assert.Equal("unix:///var/run/docker.sock", await Run(process));
        Assert.True(process.Disposed);
        Assert.Equal(0, process.GracefulStops + process.ForcedStops);
    }

    [Fact]
    public async Task FastExitedProcess_WithoutLiveIdentity_DrainsOriginalOutput()
    {
        var process = new ScriptedProcess(exited: true) { IdentityUnavailable = true };
        Assert.Equal("unix:///var/run/docker.sock", await Run(process));
        Assert.True(process.Disposed);
        Assert.Equal(0, process.GracefulStops + process.ForcedStops);
    }

    [Fact]
    public async Task CallerCancellation_AfterAllocation_StopsAndSettlesItsProcess()
    {
        var process = new ScriptedProcess();
        using var cancellation = new CancellationTokenSource();
        var inspection = Run(process, cancellation.Token);
        await process.WaitEntered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
        Assert.True(process.Exited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.GracefulStops);
        Assert.Equal(0, process.ForcedStops);
        Assert.True(process.Output.Disposed);
        Assert.True(process.Error.Disposed);
    }

    [Fact]
    public async Task InspectionTimeout_UsesGracefulStopBeforeExactProcessEscalation()
    {
        var process = new ScriptedProcess { ExitOnGraceful = false };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(process));
        Assert.Equal(["graceful", "force"], process.Signals);
        Assert.True(process.Exited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task GracefulStopUnsupported_StillVerifiesBeforeExactEscalation()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary, GracefulSupported = false };
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Run(process)));
        Assert.Equal(["graceful", "force"], process.Signals);
        Assert.True(process.Exited);
        Assert.False(primary.Data.Contains(DockerContextInspection.CleanupFailuresKey));
    }

    [Fact]
    public async Task OutputOverflow_RemainsPrimaryAfterIndependentCleanupFailures()
    {
        var process = new ScriptedProcess
        {
            Output = new InspectionStream(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 16 * 1024 + 1)))),
            GracefulFailure = new IOException("Synthetic graceful failure"),
            DisposeFailure = new IOException("Synthetic process-handle close failure")
        };
        var error = await Assert.ThrowsAsync<IOException>(() => Run(process));
        Assert.Equal("Docker context output exceeded the 16 KiB budget.", error.Message);
        Assert.True(process.Exited);
        Assert.True(process.Output.Disposed);
        Assert.True(process.Error.Disposed);
        Assert.True(process.Disposed);
        var secondary = Assert.IsType<AggregateException>(error.Data[DockerContextInspection.CleanupFailuresKey]);
        Assert.Contains(process.GracefulFailure, secondary.InnerExceptions);
        Assert.Contains(process.DisposeFailure, secondary.InnerExceptions);
        process.DisposeFailure = null;
        await Recover(error, process);
    }

    [Fact]
    public async Task CleanupNeverSettles_ReturnsOriginalFailureWithinOneFiniteBudget()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary, ExitOnGraceful = false, ExitOnForce = false };
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => Run(process));
        Assert.Same(primary, error);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(process.Exited);
        Assert.False(process.Disposed);
        Assert.IsType<AggregateException>(error.Data[DockerContextInspection.CleanupFailuresKey]);
        var retained = Assert.IsType<ContextInspectionRecovery>(error.Data[DockerContextInspection.RecoveryKey]);
        var stillPending = await retained.RecoverAsync(Cleanup);
        Assert.False(stillPending.ExitConfirmed);
        Assert.False(retained.Released);
        Assert.False(process.Disposed);
        Assert.Same(retained, error.Data[DockerContextInspection.RecoveryKey]);
        process.CompleteExit();
        await Recover(error, process);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("executable")]
    [InlineData("unavailable")]
    public async Task UnprovedRetainedIdentity_RefusesEverySignalAndPreservesPrimary(string mutation)
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary, IdentityMutation = mutation };
        var error = await Assert.ThrowsAsync<IOException>(() => Run(process));
        Assert.Same(primary, error);
        Assert.Equal(0, process.GracefulStops + process.ForcedStops);
        Assert.False(process.Exited);
        Assert.False(process.Disposed);
        Assert.IsType<AggregateException>(error.Data[DockerContextInspection.CleanupFailuresKey]);
        process.CompleteExit();
        await Recover(error, process);
    }

    [Fact]
    public async Task IdentityChangesAfterGracefulAttempt_RefusesExactEscalation()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess
        {
            WorkFailure = primary,
            IdentityMutation = "birth",
            MutationAfter = 2,
            ExitOnGraceful = false
        };
        var error = await Assert.ThrowsAsync<IOException>(() => Run(process));
        Assert.Same(primary, error);
        Assert.Equal(1, process.GracefulStops);
        Assert.Equal(0, process.ForcedStops);
        Assert.False(process.Exited);
        Assert.False(process.Disposed);
        process.CompleteExit();
        await Recover(error, process);
    }

    [Fact]
    public async Task PipeCloseFailure_DoesNotPreventOtherPipeAndProcessHandleRelease()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary };
        process.Output.CloseFailure = new IOException("Synthetic output close failure");
        var error = await Assert.ThrowsAsync<IOException>(() => Run(process));
        Assert.Same(primary, error);
        Assert.True(process.Output.Disposed);
        Assert.True(process.Error.Disposed);
        Assert.True(process.Disposed);
        Assert.True(process.Exited);
        var secondary = Assert.IsType<AggregateException>(error.Data[DockerContextInspection.CleanupFailuresKey]);
        Assert.Contains(process.Output.CloseFailure, secondary.InnerExceptions);
        process.Output.CloseFailure = null;
        await Recover(error, process);
    }

    [Fact]
    public async Task ReaderIgnoringCancellationUntilClose_IsObservedSettledBeforeReturn()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary };
        process.Output.IgnoreCancellation = true;
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Run(process)));
        var custody = Assert.IsType<ContextInspectionCustody>(primary.Data[DockerContextInspection.CustodyKey]);
        Assert.True(custody.OutputClosed);
        Assert.Equal("settled", custody.OutputState);
        Assert.Equal("settled", custody.ErrorState);
        Assert.Equal("settled", custody.ExitWaitState);
        Assert.False(primary.Data.Contains(DockerContextInspection.CleanupFailuresKey));
    }

    [Fact]
    public async Task CancellationIgnoringReader_WithCloseFailure_PreservesBothOutcomes()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary };
        process.Error.IgnoreCancellation = true;
        process.Error.CloseFailure = new IOException("Synthetic error-pipe close failure");
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Run(process)));
        var custody = Assert.IsType<ContextInspectionCustody>(primary.Data[DockerContextInspection.CustodyKey]);
        Assert.True(custody.OutputClosed);
        Assert.False(custody.ErrorClosed); // Throwing close is not recorded as confirmed closure.
        Assert.Equal("settled", custody.ErrorState);
        Assert.True(process.Disposed);
        var secondary = Assert.IsType<AggregateException>(primary.Data[DockerContextInspection.CleanupFailuresKey]);
        Assert.Contains(process.Error.CloseFailure, secondary.InnerExceptions);
        process.Error.CloseFailure = null;
        await Recover(primary, process);
    }

    [Fact]
    public async Task ReaderStillPendingAfterClose_IsReportedUnresolvedWithinTheBudget()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess { WorkFailure = primary };
        process.Output.IgnoreCancellation = true;
        process.Output.ReleaseOnDispose = false;
        var timer = Stopwatch.StartNew();
        try
        {
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Run(process)));
            var custody = Assert.IsType<ContextInspectionCustody>(primary.Data[DockerContextInspection.CustodyKey]);
            Assert.Equal("pending", custody.OutputState);
            var recovery = Assert.IsType<ContextInspectionRecovery>(primary.Data[DockerContextInspection.RecoveryKey]);
            Assert.Same(process, recovery.Process);
            Assert.Contains(recovery.Tasks, task => !task.IsCompleted);
            Assert.False(process.Disposed);
            Assert.IsType<AggregateException>(primary.Data[DockerContextInspection.CleanupFailuresKey]);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
        }
        finally
        {
            process.Output.Release();
            await process.Output.ReadSettled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Recover(primary, process);
        }
    }

    [Fact]
    public async Task CleanupWaitIgnoringCancellation_IsReportedPendingInsteadOfDiscarded()
    {
        var primary = new IOException("Synthetic inspection failure");
        var process = new ScriptedProcess
        {
            WorkFailure = primary,
            IgnoreWaitCancellation = true,
            ExitOnGraceful = false,
            ExitOnForce = false
        };
        try
        {
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Run(process)));
            var custody = Assert.IsType<ContextInspectionCustody>(primary.Data[DockerContextInspection.CustodyKey]);
            Assert.Equal("pending", custody.ExitWaitState);
            Assert.False(custody.ExitConfirmed);
            var recovery = Assert.IsType<ContextInspectionRecovery>(primary.Data[DockerContextInspection.RecoveryKey]);
            Assert.Same(process, recovery.Process);
            Assert.Contains(recovery.Tasks, task => !task.IsCompleted);
            Assert.False(process.Disposed);
            Assert.IsType<AggregateException>(primary.Data[DockerContextInspection.CleanupFailuresKey]);
        }
        finally { process.CompleteExit(); await Recover(primary, process); }
    }

    [LinuxInspectionFact]
    public async Task ActualInspectionTimeout_ExitsOnlyItsParentAndPreservesOwnedChild()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotation-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "child.pid");
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-c", "sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; wait \"$child\"", "inspection", pidFile })
            start.ArgumentList.Add(argument);
        var parent = new RecordingProcess(new ContextInspectionProcess(Process.Start(start)!));
        RecordingProcess? child = null;
        try
        {
            parent.ObserveIdentity();
            using var admission = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
                await Task.Delay(10, admission.Token);
            var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, admission.Token));
            var status = await File.ReadAllTextAsync($"/proc/{childPid}/stat", admission.Token);
            var fields = status[(status.LastIndexOf(") ", StringComparison.Ordinal) + 2)..].Split(' ');
            Assert.Equal(parent.Identity!.Pid, int.Parse(fields[1]));
            child = new(new ContextInspectionProcess(Process.GetProcessById(childPid)));
            child.ObserveIdentity();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DockerContextInspection.RunAsync(
                () => parent, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2)));
            var custody = Assert.IsType<ContextInspectionCustody>(error.Data[DockerContextInspection.CustodyKey]);
            Assert.True(custody.ExitConfirmed);
            Assert.True(custody.OutputClosed);
            Assert.True(custody.ErrorClosed);
            Assert.True(custody.ProcessHandleClosed);
            Assert.Equal("settled", custody.OutputState);
            Assert.Equal("settled", custody.ErrorState);
            Assert.Equal("settled", custody.ExitWaitState);
            Assert.False(error.Data.Contains(DockerContextInspection.CleanupFailuresKey));
            output.WriteLine("Actual inspection custody: {0}", custody);
            Assert.True(parent.ExitConfirmedBeforeDispose);
            Assert.Equal(1, parent.GracefulStops);
            Assert.Equal(0, parent.ForcedStops);
            Assert.False(child.HasExited);
        }
        finally
        {
            // Each resource is owned and re-observed separately; never stop an ancestry tree.
            try { if (child is not null) await StopFixture(child); }
            finally
            {
                await StopFixture(parent);
                if (File.Exists(pidFile)) File.Delete(pidFile);
                Directory.Delete(directory);
            }
        }
        WriteReceipt("parent", parent);
        Assert.NotNull(child);
        WriteReceipt("child", child);
    }

    private void WriteReceipt(string role, RecordingProcess process) => output.WriteLine(
        "Owned inspection resource Role={0} Pid={1} StartedUtc={2:O} Executable={3} ExitConfirmed={4} Disposed={5}",
        role, process.Identity?.Pid, process.Identity?.StartedUtc, process.Identity?.Executable,
        process.ExitConfirmedBeforeDispose, process.Disposed);

    private static async Task StopFixture(RecordingProcess process)
    {
        if (process.Disposed) return;
        try
        {
            if (!process.HasExited)
            {
                var retained = process.Identity ?? process.ObserveIdentity();
                Assert.Equal(retained, process.ObserveIdentity());
                process.TryGracefulStop();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(deadline.Token);
            }
            Assert.True(process.HasExited);
        }
        finally { process.Dispose(); }
    }

    private static Task<string> Run(ScriptedProcess process, CancellationToken token = default) =>
        DockerContextInspection.RunAsync(() => process, Work, Cleanup, token);

    private static async Task Recover(Exception error, ScriptedProcess process)
    {
        var recovery = Assert.IsType<ContextInspectionRecovery>(error.Data[DockerContextInspection.RecoveryKey]);
        Assert.Same(process, recovery.Process);
        Assert.Equal(TimeSpan.FromMinutes(5), recovery.ExpiresUtc - recovery.CreatedUtc);
        Assert.NotEmpty(recovery.Purpose);
        var custody = await recovery.RecoverAsync(Cleanup);
        Assert.True(recovery.Released);
        Assert.True(custody.ExitConfirmed);
        Assert.True(custody.ProcessHandleClosed);
        Assert.True(custody.OutputClosed);
        Assert.True(custody.ErrorClosed);
        Assert.All(recovery.Tasks, task => Assert.True(task.IsCompleted));
        Assert.True(process.Disposed);
        Assert.Same(custody, error.Data[DockerContextInspection.CustodyKey]);
    }

    private sealed class ScriptedProcess(bool exited = false) : IContextInspectionProcess
    {
        private static readonly ContextInspectionIdentity Initial = new(42,
            new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            OperatingSystem.IsWindows() ? @"C:\synthetic\docker.exe" : "/synthetic/docker");
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int observations;
        private int waits;
        public bool Exited = exited;
        public bool HasExited => Exited;
        public int ExitCode => 0;
        public InspectionStream Output { get; set; } = new(exited ?
            new MemoryStream(Encoding.UTF8.GetBytes("\"unix:///var/run/docker.sock\"")) : null);
        public InspectionStream Error { get; } = new(exited ? new MemoryStream() : null);
        public Stream StandardOutput => Output;
        public Stream StandardError => Error;
        public bool ExitOnGraceful = true;
        public bool ExitOnForce = true;
        public bool GracefulSupported = true;
        public bool IgnoreWaitCancellation;
        public bool IdentityUnavailable;
        public string? IdentityMutation;
        public int MutationAfter = 1;
        public Exception? WorkFailure;
        public Exception? GracefulFailure;
        public Exception? DisposeFailure;
        public bool Disposed;
        public int GracefulStops;
        public int ForcedStops;
        public List<string> Signals { get; } = [];
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ContextInspectionIdentity ObserveIdentity()
        {
            if (IdentityUnavailable || observations++ >= MutationAfter && IdentityMutation == "unavailable")
                throw new InvalidOperationException("Synthetic identity unavailable");
            if (observations > MutationAfter)
                return IdentityMutation switch
                {
                    "pid" => Initial with { Pid = 43 },
                    "birth" => Initial with { StartedUtc = Initial.StartedUtc.AddMilliseconds(1) },
                    "executable" => Initial with { Executable = OperatingSystem.IsWindows() ? @"C:\synthetic\foreign.exe" : "/synthetic/foreign" },
                    _ => Initial
                };
            return Initial;
        }
        public Task WaitForExitAsync(CancellationToken token)
        {
            WaitEntered.TrySetResult();
            if (waits++ == 0 && WorkFailure is not null) return Task.FromException(WorkFailure);
            return Exited ? Task.CompletedTask : IgnoreWaitCancellation ? exit.Task : exit.Task.WaitAsync(token);
        }
        public bool TryGracefulStop()
        {
            GracefulStops++; Signals.Add("graceful");
            if (GracefulFailure is not null) throw GracefulFailure;
            if (!GracefulSupported) return false;
            if (ExitOnGraceful) CompleteExit();
            return true;
        }
        public void ForceStop()
        {
            ForcedStops++; Signals.Add("force");
            if (ExitOnForce) CompleteExit();
        }
        public void CompleteExit() { Exited = true; exit.TrySetResult(); }
        public void Dispose()
        {
            Disposed = true;
            if (DisposeFailure is not null) throw DisposeFailure;
        }
    }

    private sealed class InspectionStream(Stream? source) : Stream
    {
        private readonly TaskCompletionSource<int> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Exception? CloseFailure;
        public bool IgnoreCancellation;
        public bool ReleaseOnDispose = true;
        public TaskCompletionSource ReadSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => release.TrySetResult(0);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try
            {
                if (source is not null) return await source.ReadAsync(buffer, token);
                if (IgnoreCancellation) return await release.Task;
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            finally { ReadSettled.TrySetResult(); }
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            source?.Dispose();
            if (ReleaseOnDispose) Release();
            if (CloseFailure is not null) throw CloseFailure;
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingProcess(ContextInspectionProcess inner) : IContextInspectionProcess
    {
        public ContextInspectionIdentity? Identity;
        public bool Disposed;
        public bool ExitConfirmedBeforeDispose;
        public int GracefulStops;
        public int ForcedStops;
        public bool HasExited => inner.HasExited;
        public int ExitCode => inner.ExitCode;
        public Stream StandardOutput => inner.StandardOutput;
        public Stream StandardError => inner.StandardError;
        public ContextInspectionIdentity ObserveIdentity()
        {
            var actual = inner.ObserveIdentity();
            Identity ??= actual;
            return actual;
        }
        public Task WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);
        public bool TryGracefulStop() { GracefulStops++; return inner.TryGracefulStop(); }
        public void ForceStop() { ForcedStops++; inner.ForceStop(); }
        public void Dispose()
        {
            ExitConfirmedBeforeDispose = inner.HasExited;
            Disposed = true;
            inner.Dispose();
        }
    }
}

internal sealed class LinuxInspectionFactAttribute : FactAttribute
{
    public LinuxInspectionFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Actual POSIX process ownership case requires hosted Linux.";
    }
}
