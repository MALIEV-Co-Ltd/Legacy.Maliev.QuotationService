using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

internal sealed record ContextInspectionIdentity(int Pid, DateTime StartedUtc, string Executable);
internal sealed record ContextInspectionCustody(ContextInspectionIdentity? Identity, bool ExitConfirmed,
    bool OutputClosed, bool ErrorClosed, bool ProcessHandleClosed,
    string OutputState, string ErrorState, string ExitWaitState);

internal sealed class ContextInspectionRecovery(IContextInspectionProcess process,
    ContextInspectionIdentity? identity, IReadOnlyList<Task> tasks,
    Func<TimeSpan, Task<ContextInspectionCustody>> recover)
{
    private int recovering;
    internal IContextInspectionProcess Process { get; } = process;
    internal ContextInspectionIdentity? Identity { get; } = identity;
    internal IReadOnlyList<Task> Tasks { get; } = tasks;
    internal string Purpose => "Recover the original Docker context inspection handles and tasks.";
    internal DateTime CreatedUtc { get; } = DateTime.UtcNow;
    internal DateTime ExpiresUtc => CreatedUtc.AddMinutes(5);
    internal ContextInspectionCustody? LastOutcome { get; private set; }
    internal bool Released => LastOutcome is
    {
        ExitConfirmed: true, OutputClosed: true,
        ErrorClosed: true, ProcessHandleClosed: true, OutputState: "settled" or "not-started",
        ErrorState: "settled" or "not-started", ExitWaitState: "settled" or "not-started"
    };

    internal async Task<ContextInspectionCustody> RecoverAsync(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (Interlocked.CompareExchange(ref recovering, 1, 0) != 0)
            throw new InvalidOperationException("Owned inspection recovery is already in progress.");
        try { return LastOutcome = await recover(budget); }
        finally { Volatile.Write(ref recovering, 0); }
    }
}

internal interface IContextInspectionProcess : IDisposable
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    ContextInspectionIdentity ObserveIdentity();
    Task WaitForExitAsync(CancellationToken token);
    bool TryGracefulStop();
    void ForceStop();
}

internal static class DockerContextInspection
{
    internal const string CleanupFailuresKey = "DockerContextInspectionCleanupFailures";
    internal const string CustodyKey = "DockerContextInspectionCustody";
    internal const string RecoveryKey = "DockerContextInspectionRecovery";

    internal static async Task<string> RunAsync(Func<IContextInspectionProcess> start,
        TimeSpan workBudget, TimeSpan cleanupBudget, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (workBudget <= TimeSpan.Zero || workBudget > TimeSpan.FromSeconds(15)
            || cleanupBudget <= TimeSpan.Zero || cleanupBudget > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(workBudget), "Finite inspection and cleanup budgets required.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(workBudget);
        var process = start();
        ContextInspectionIdentity? identity = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Task? exited = null;
        var ownedWaits = new List<Task>();
        Stream? outputPipe = null;
        Stream? errorPipe = null;
        var outputCloseAttempted = false;
        var errorCloseAttempted = false;
        var outputClosed = false;
        var errorClosed = false;
        Exception? primary = null;
        var cleanupErrors = new List<Exception>();
        void ClosePipes()
        {
            if (!outputCloseAttempted)
            {
                outputCloseAttempted = true;
                Close(() => { (outputPipe ?? process.StandardOutput).Dispose(); outputClosed = true; }, cleanupErrors);
            }
            if (!errorCloseAttempted)
            {
                errorCloseAttempted = true;
                Close(() => { (errorPipe ?? process.StandardError).Dispose(); errorClosed = true; }, cleanupErrors);
            }
        }
        try
        {
            try { identity = CaptureIdentity(process); }
            catch (Exception) when (process.HasExited) { /* Terminal retained process needs no signal identity. */ }
            outputPipe = process.StandardOutput;
            stdout = DisposableContainerStartup.ReadBoundedAsync(outputPipe, lifetime.Token);
            errorPipe = process.StandardError;
            stderr = DisposableContainerStartup.ReadBoundedAsync(errorPipe, lifetime.Token);
            exited = process.WaitForExitAsync(lifetime.Token);
            ownedWaits.Add(exited);
            var pending = new List<Task> { stdout, stderr, exited };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(lifetime.Token);
                await completed;
                pending.Remove(completed);
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("Docker context inspection failed.");
            return DisposableContainerStartup.NormalizeLocalEndpoint(System.Text.Json.JsonSerializer.Deserialize<string>(await stdout)?.Trim()
                ?? throw new InvalidOperationException("Docker context endpoint is absent."));
        }
        catch (Exception error)
        {
            primary = error;
            try { await CleanupAsync(process, identity, lifetime, stdout, stderr, ownedWaits, cleanupBudget, cleanupErrors, error, ClosePipes); }
            catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
            throw; // Preserve the exact primary exception and its original stack.
        }
        finally
        {
            // Close every independent handle even when another close fails.
            ClosePipes();
            var exitConfirmed = false;
            Close(() => exitConfirmed = process.HasExited, cleanupErrors);
            var processClosed = false;
            bool TasksSettled() => (stdout is null || stdout.IsCompleted) && (stderr is null || stderr.IsCompleted)
                && ownedWaits.All(task => task.IsCompleted);
            void ReleaseSettledProcess()
            {
                if (!processClosed && exitConfirmed && TasksSettled())
                    Close(() => { process.Dispose(); processClosed = true; }, cleanupErrors);
            }
            ContextInspectionCustody Snapshot() => new(identity, exitConfirmed, outputClosed, errorClosed, processClosed,
                State(stdout), State(stderr), ownedWaits.Count == 0 ? "not-started" : ownedWaits.All(task => task.IsCompleted) ? "settled" : "pending");
            ReleaseSettledProcess();
            var custody = Snapshot();
            var unresolved = !exitConfirmed || !TasksSettled() || !outputClosed || !errorClosed || !processClosed;
            if (unresolved)
                cleanupErrors.Add(new InvalidOperationException("Owned Docker inspection custody remains unresolved."));
            if (cleanupErrors.Count != 0)
            {
                var secondary = new AggregateException("Owned Docker inspection cleanup failed.", cleanupErrors);
                var recipient = primary ?? secondary;
                recipient.Data[CustodyKey] = custody;
                if (primary is not null) primary.Data[CleanupFailuresKey] = secondary;
                if (unresolved)
                {
                    // Transfer actual retained ownership, not merely status strings. Expiry is an
                    // attention deadline, never evidence that a process or pending task settled.
                    var tasks = new[] { stdout, stderr }.OfType<Task>().Concat(ownedWaits).ToList();
                    recipient.Data[RecoveryKey] = new ContextInspectionRecovery(process, identity, tasks.AsReadOnly(), async recoveryBudget =>
                    {
                        outputCloseAttempted = outputClosed;
                        errorCloseAttempted = errorClosed;
                        using var recoveryCancellation = new CancellationTokenSource();
                        if (!processClosed)
                        {
                            await CleanupAsync(process, identity, recoveryCancellation, stdout, stderr, ownedWaits,
                                recoveryBudget, cleanupErrors, recipient, ClosePipes);
                            Close(() => exitConfirmed = process.HasExited, cleanupErrors);
                        }
                        else ClosePipes();
                        // Include any new recovery waits in the retained task collection too.
                        foreach (var task in ownedWaits) if (!tasks.Contains(task)) tasks.Add(task);
                        ReleaseSettledProcess();
                        var outcome = Snapshot();
                        recipient.Data[CustodyKey] = outcome;
                        recipient.Data[CleanupFailuresKey] = new AggregateException("Owned Docker inspection cleanup outcomes.", cleanupErrors);
                        return outcome;
                    });
                }
                if (primary is null) throw recipient;
            }
            else if (primary is not null) primary.Data[CustodyKey] = custody;
        }
    }

    private static async Task CleanupAsync(IContextInspectionProcess process, ContextInspectionIdentity? retained,
        CancellationTokenSource lifetime, Task? stdout, Task? stderr, List<Task> ownedWaits,
        TimeSpan budget, List<Exception> errors, Exception primary, Action closePipes)
    {
        var timer = Stopwatch.StartNew();
        using var cleanup = new CancellationTokenSource(budget);
        TimeSpan Remaining()
        {
            var remaining = budget - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Owned Docker inspection cleanup deadline exhausted.");
            return remaining;
        }
        var identityTrusted = true;
        void VerifyIdentity()
        {
            Remaining();
            if (!identityTrusted || retained is null)
                throw new InvalidOperationException("Owned Docker inspection identity unproved; signal refused.");
            try
            {
                if (CaptureIdentity(process) != retained)
                    throw new InvalidOperationException("Owned Docker inspection identity changed; signal refused.");
            }
            catch { identityTrusted = false; throw; }
            Remaining();
        }
        async Task Attempt(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { errors.Add(error); }
        }

        await Attempt(async () => await lifetime.CancelAsync().WaitAsync(
            TimeSpan.FromTicks(Math.Min(Remaining().Ticks, TimeSpan.FromMilliseconds(250).Ticks))));
        await Attempt(async () =>
        {
            if (process.HasExited) return;
            VerifyIdentity();
            if (!process.TryGracefulStop()) return;
            // Reserve most of the one cleanup budget for exact escalation and pipe settlement.
            var grace = TimeSpan.FromTicks(Math.Min(Remaining().Ticks / 3, TimeSpan.FromSeconds(1).Ticks));
            var wait = process.WaitForExitAsync(cleanup.Token);
            ownedWaits.Add(wait);
            try { await wait.WaitAsync(grace); }
            catch (TimeoutException) { /* Grace elapsed; re-observe before any exact escalation. */ }
        });
        await Attempt(async () =>
        {
            if (process.HasExited) return;
            VerifyIdentity();
            process.ForceStop();
            // Keep half the remaining budget for pipe closure and final task settlement.
            var wait = process.WaitForExitAsync(cleanup.Token);
            ownedWaits.Add(wait);
            await wait.WaitAsync(TimeSpan.FromTicks(Remaining().Ticks / 2));
        });
        try
        {
            if (!process.HasExited) throw new InvalidOperationException("Owned Docker inspection exit was not confirmed.");
        }
        catch (Exception error) { errors.Add(error); }

        // Cancellation alone need not settle inherited pipes. Close both while settlement time remains.
        closePipes();
        // Dispose does not cancel a CTS: settle timed-out graceful/escalation wait registrations too.
        await Attempt(async () => await cleanup.CancelAsync().WaitAsync(
            TimeSpan.FromTicks(Math.Min(Remaining().Ticks, TimeSpan.FromMilliseconds(250).Ticks))));
        var tasks = new[] { stdout, stderr }.OfType<Task>().Concat(ownedWaits).ToArray();
        var settlement = Task.WhenAll(tasks);
        try { await settlement.WaitAsync(Remaining()); }
        catch (Exception error)
        {
            // Faulted/cancelled work tasks are settled; their primary error is already retained.
            if (!settlement.IsCompleted) errors.Add(error);
            _ = settlement.Exception;
        }
        foreach (var task in tasks)
        {
            // Observe every fault after pipe closure, preserving independent secondary outcomes.
            if (task.Exception is not { } failure) continue;
            errors.AddRange(failure.Flatten().InnerExceptions.Where(error =>
                !ReferenceEquals(error, primary) && error is not OperationCanceledException));
        }
    }

    private static void Close(Action close, List<Exception> errors)
    {
        try { close(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static string State(Task? task) => task is null ? "not-started" : task.IsCompleted ? "settled" : "pending";

    private static ContextInspectionIdentity CaptureIdentity(IContextInspectionProcess process)
    {
        var identity = process.ObserveIdentity();
        if (identity.Pid <= 0 || identity.StartedUtc.Kind != DateTimeKind.Utc
            || identity.StartedUtc == DateTime.MinValue || !Path.IsPathFullyQualified(identity.Executable))
            throw new InvalidOperationException("Owned Docker inspection identity malformed; signal refused.");
        return identity;
    }
}

internal sealed class ContextInspectionProcess(Process process) : IContextInspectionProcess
{
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardError => process.StandardError.BaseStream;
    public bool HasExited { get { process.Refresh(); return process.HasExited; } }
    public int ExitCode => process.ExitCode;
    public ContextInspectionIdentity ObserveIdentity()
    {
        // Fresh observer avoids cached module/start information; never adopt a replacement PID.
        if (HasExited) throw new InvalidOperationException("Owned Docker inspection process already exited.");
        using var current = Process.GetProcessById(process.Id);
        current.Refresh();
        var started = current.StartTime.ToUniversalTime();
        var executable = current.MainModule?.FileName;
        if (HasExited || current.HasExited || started.Kind != DateTimeKind.Utc || string.IsNullOrEmpty(executable)
            || !Path.IsPathFullyQualified(executable))
            throw new InvalidOperationException("Owned Docker inspection identity unavailable.");
        return new(current.Id, started, Path.GetFullPath(executable));
    }
    public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
    public bool TryGracefulStop()
    {
        if (OperatingSystem.IsWindows()) return process.CloseMainWindow();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Owned inspection stop requires Windows or Linux.");
        if (SendSignal(process.Id, 15) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Owned Docker inspection graceful stop failed.");
        return true;
    }
    public void ForceStop() => process.Kill(); // Exact retained process only; never descendants.
    public void Dispose() => process.Dispose();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);
}
