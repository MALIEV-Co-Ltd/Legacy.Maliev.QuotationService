using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace InvoiceCompletionProducerAcceptance.Companion;

internal static class BoundedOwnedCommand
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Lease? retained;
    private static Exception? firstFailure;
    private static Observation? lastObservation;
    internal sealed record Observation(bool BirthAttempted, bool OriginalExited, int? ExitCode,
        bool StdoutEof, bool StderrEof, bool OriginalTasksTerminal, bool StdoutClosed, bool StderrClosed,
        bool PidfdAllocated, bool PidfdClosed, bool ProcessClosed, bool MetadataClosed,
        bool CleanupBudgetsClosed, bool KernelGenerationBound, bool ActualSignalInvoked,
        bool ActualSignalSucceeded, bool PhysicalReleased, bool CleanupFailureRecorded,
        bool RetainedOwner, bool AdmissionSticky, int CleanupAttempts, long StdoutBytesRead, long StderrBytesRead);
    internal static Observation? ObserveLast() => lastObservation;
    internal static bool HasRetainedOwner => retained is not null;

    internal static async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments,
        int maximumOutput, CancellationToken cancellationToken)
    {
        if (maximumOutput is < 1 or > 2097152)
            throw new InvalidDataException("Finite observation output bound required.");
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
            throw new InvalidDataException("Owned observation admission remained busy.");
        try
        {
            if (retained is not null && await CleanupSafelyAsync(retained)) retained = null;
            if (!AdmissionAllowed(firstFailure is not null, retained is not null))
                throw new InvalidDataException("Owned observation cleanup remains refused.");
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            // Install custody before any disposable allocation or attempted child birth.
            var lease = new Lease(info, maximumOutput, cancellationToken);
            retained = lease;
            Exception? primary = null;
            try { return await lease.RunAsync(); }
            catch (Exception failure)
            {
                primary = failure;
                lease.RecordOperationFailure(failure);
                throw;
            }
            finally
            {
                bool closed = await CleanupSafelyAsync(lease);
                if (lease.FirstFailure is not null)
                    firstFailure ??= lease.OperationFailure ?? lease.FirstFailure;
                if (closed) retained = null;
                lastObservation = lease.Observe(retained is not null, firstFailure is not null);
                if (!closed || lease.FirstFailure is not null)
                {
                    firstFailure ??= lease.OperationFailure ?? new InvalidDataException("Owned observation cleanup refused.");
                    if (primary is not null) primary.Data["OwnedCommandCleanupVerified"] = false;
                    else throw new InvalidDataException("Owned observation cleanup refused.");
                }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> CleanupSafelyAsync(Lease lease)
    {
        try { return await lease.CleanupAsync(); }
        catch (Exception failure) { lease.Refuse(failure); return false; }
    }

    // Recovery attempts never admit a new command or erase the original failure.
    internal static async Task<bool> RetryCleanupAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)) return false;
        try
        {
            Lease? lease = retained;
            if (lease is null) return true;
            bool closed = await CleanupSafelyAsync(lease);
            if (lease.FirstFailure is not null)
                firstFailure ??= lease.OperationFailure ?? lease.FirstFailure;
            if (closed) retained = null;
            lastObservation = lease.Observe(retained is not null, firstFailure is not null);
            return closed;
        }
        finally { Gate.Release(); }
    }

    // These seams are the production cleanup policy, not receipts for modeled physical resources.
    internal static async Task AttemptIndependentlyAsync(IEnumerable<Func<Task>> steps, Action<Exception> refused)
    {
        foreach (Func<Task> step in steps)
            try { await step(); }
            catch (Exception failure) { refused(failure); }
    }

    internal static bool ReleaseAllowed(bool exited, bool stdoutEof, bool stderrEof,
        bool streamsHeld, IEnumerable<Task?> tasks) => exited && stdoutEof && stderrEof && streamsHeld
        && tasks.All(task => task is null || task.IsCompleted);

    internal static bool AdmissionAllowed(bool cleanupFailed, bool retainedUnsettled) =>
        !cleanupFailed && !retainedUnsettled;

    internal static bool RecoveryAllowed(int attempts) => attempts is >= 0 and < 2;

    internal static async Task ObserveSettledAsync(Task original, CancellationToken token)
    {
        try { await original.WaitAsync(token); }
        catch (Exception)
        {
            if (!original.IsCompleted) throw;
            if (original.IsFaulted) _ = original.Exception;
        }
    }

    internal sealed class CloseOnce(Action close, Func<bool> isClosed)
    {
        private bool attempted;
        internal bool Completed { get; private set; }
        internal void Attempt()
        {
            if (Completed) return;
            if (attempted) throw new InvalidDataException("Owned handle closure remains uncertain.");
            attempted = true;
            close();
            Completed = isClosed();
            if (!Completed) throw new InvalidDataException("Owned handle did not close.");
        }
    }

    internal static (ulong Ticks, int Parent) ParseKernel(byte[] bytes, int expectedPid)
    {
        if (bytes.Length is < 1 or > 4096) throw new InvalidDataException("Owned kernel metadata exceeded bound.");
        string stat = Encoding.ASCII.GetString(bytes);
        int opening = stat.IndexOf('('), closing = stat.LastIndexOf(')');
        if (opening <= 0 || closing <= opening
            || !int.TryParse(stat.AsSpan(0, opening).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid != expectedPid) throw new InvalidDataException("Owned kernel process differs.");
        string[] fields = stat[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent)
            || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ticks)
            || ticks == 0) throw new InvalidDataException("Owned kernel generation unavailable.");
        return (ticks, parent);
    }

    internal static bool PidfdMatches(byte[] bytes, int expectedPid)
    {
        if (bytes.Length is < 1 or > 1024 || expectedPid <= 0) return false;
        string[] rows = Encoding.ASCII.GetString(bytes).Split('\n')
            .Where(line => line.StartsWith("Pid:", StringComparison.Ordinal)).ToArray();
        return rows.Length == 1 && int.TryParse(rows[0].AsSpan(4).Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out int pid) && pid == expectedPid;
    }

    private sealed class Lease(ProcessStartInfo info, int maximumOutput, CancellationToken caller)
    {
        private Process? process;
        private CancellationTokenSource? work, readers;
        private SafeProcessHandle? runtimeHandle;
        private SafeFileHandle? pidfd;
        private StreamReader? stdout, stderr;
        private SafeHandle? stdoutHandle, stderrHandle;
        private Task<byte[]>? outputTask, errorTask;
        private Task? exitTask, cleanupExitTask, outputDrain, errorDrain;
        private (ulong Ticks, int Parent)? kernel;
        private long? nativeTicks;
        private bool attempted, stdoutEof, stderrEof, exitVerified, closed;
        private int? observedExitCode;
        private bool generationBound, signalInvoked, signalSucceeded;
        private long stdoutBytesRead, stderrBytesRead;
        internal Observation Observe(bool retainedOwner, bool admissionSticky) => new(
            attempted, exitVerified, observedExitCode, stdoutEof, stderrEof,
            new Task?[] { outputTask, errorTask, exitTask, cleanupExitTask, outputDrain, errorDrain }
                .All(task => task is null || task.IsCompleted),
            stdoutClose?.Completed is true, stderrClose?.Completed is true,
            pidfd is not null, pidfdClose?.Completed is true, processClose?.Completed is true,
            metadata.All(item => item.Close.Completed), cleanupBudgets.All(item => item.Close.Completed),
            generationBound, signalInvoked, signalSucceeded, closed, FirstFailure is not null,
            retainedOwner, admissionSticky, cleanupBudgets.Count, stdoutBytesRead, stderrBytesRead);
        private CloseOnce? stdoutClose, stderrClose, pidfdClose, processClose, workClose, readersClose;
        private readonly List<(FileStream Stream, CloseOnce Close)> metadata = [];
        private readonly List<(CancellationTokenSource Source, CloseOnce Close)> cleanupBudgets = [];
        private readonly List<Exception> cleanupFailures = [];
        internal Exception? FirstFailure { get; private set; }
        internal Exception? OperationFailure { get; private set; }
        internal void RecordOperationFailure(Exception failure) => OperationFailure ??= failure;

        internal void Refuse(Exception failure)
        {
            FirstFailure ??= failure;
            if (cleanupFailures.Count < 32) cleanupFailures.Add(failure);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
        private static SafeHandle PipeHandle(Stream stream) => stream switch
        {
            FileStream file => file.SafeFileHandle,
            PipeStream pipe => pipe.SafePipeHandle,
            _ => throw new InvalidDataException("Owned observation pipe handle unavailable."),
        };
        private void CaptureOutput()
        {
            stdout ??= process!.StandardOutput;
            stdoutHandle ??= PipeHandle(stdout.BaseStream);
            stdoutClose ??= new(stdout.Dispose, () => stdoutHandle!.IsClosed);
        }
        private void CaptureError()
        {
            stderr ??= process!.StandardError;
            stderrHandle ??= PipeHandle(stderr.BaseStream);
            stderrClose ??= new(stderr.Dispose, () => stderrHandle!.IsClosed);
        }

        private async Task<byte[]> ReadAsync(Stream stream, int maximum, bool output, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, token);
                if (count == 0)
                {
                    if (output) stdoutEof = true; else stderrEof = true;
                    return bytes.ToArray();
                }
                if (output) stdoutBytesRead += count; else stderrBytesRead += count;
                Require(bytes.Length + count <= maximum, "Observation output exceeded its bound.");
                bytes.Write(buffer, 0, count);
            }
        }

        private bool Exited()
        {
            if (exitVerified) return true;
            process!.Refresh();
            exitVerified = process.HasExited; // Original retained .NET child wait state.
            if (exitVerified) observedExitCode ??= process.ExitCode;
            return exitVerified;
        }

        private async Task<byte[]> MetadataAsync(string path, int maximum, CancellationToken token)
        {
            Require(metadata.Count < 9, "Owned metadata attempt budget exhausted.");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous);
            SafeFileHandle handle = stream.SafeFileHandle;
            var closure = new CloseOnce(stream.Dispose, () => handle.IsClosed);
            metadata.Add((stream, closure)); // Retained before reading; failed closure is never retried.
            try
            {
                byte[] buffer = new byte[maximum + 1];
                int total = 0;
                while (total < buffer.Length)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(total), token);
                    if (count == 0) break;
                    total += count;
                }
                Require(total <= maximum, "Owned metadata exceeded bound.");
                return buffer[..total];
            }
            finally
            {
                try { closure.Attempt(); }
                catch (Exception failure) { Refuse(failure); throw; }
            }
        }

        private async Task BindLiveAsync(CancellationToken token)
        {
            if (Exited()) return;
            try
            {
                runtimeHandle ??= process!.SafeHandle; // Retained pseudo-handle; never used for signaling.
                if (Exited()) return;
                int pid = process!.Id;
                var before = ParseKernel(await MetadataAsync($"/proc/{pid}/stat", 4096, token), pid);
                long beforeNative = process.StartTime.ToUniversalTime().Ticks;
                if (Exited()) return;
                Require(before.Parent == Environment.ProcessId && (!kernel.HasValue || before == kernel.Value)
                    && (!nativeTicks.HasValue || beforeNative == nativeTicks.Value), "Owned observation generation differs.");
                kernel ??= before;
                nativeTicks ??= beforeNative;
                if (pidfd is null)
                {
                    int descriptor = pidfd_open(pid, 0);
                    if (descriptor < 0)
                    {
                        if (Exited()) return;
                        throw new InvalidDataException("Owned observation pidfd unavailable.");
                    }
                    pidfd = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                    pidfdClose = new(pidfd.Dispose, () => pidfd!.IsClosed);
                }
                if (Exited()) return;
                Require(!pidfd.IsClosed && !pidfd.IsInvalid, "Owned pidfd unavailable.");
                int fd = checked((int)pidfd.DangerousGetHandle());
                byte[] descriptorInfo = await MetadataAsync($"/proc/self/fdinfo/{fd}", 1024, token);
                Require(PidfdMatches(descriptorInfo, pid),
                    "Owned pidfd association differs.");
                var after = ParseKernel(await MetadataAsync($"/proc/{pid}/stat", 4096, token), pid);
                long afterNative = process.StartTime.ToUniversalTime().Ticks;
                if (Exited()) return;
                Require(after == kernel.Value && afterNative == nativeTicks.Value, "Owned pidfd generation differs.");
                generationBound = true;
            }
            catch (Exception)
            {
                if (Exited()) return; // Original natural exit is allowed, never a reason to signal a new PID.
                throw;
            }
        }

        internal async Task<byte[]> RunAsync()
        {
            Require(OperatingSystem.IsLinux(), "Hosted Linux observation helper required.");
            caller.ThrowIfCancellationRequested();
            work = CancellationTokenSource.CreateLinkedTokenSource(caller);
            work.CancelAfter(TimeSpan.FromSeconds(5));
            workClose = new(work.Dispose, () => true);
            readers = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
            readersClose = new(readers.Dispose, () => true);
            process = new Process { StartInfo = info };
            processClose = new(process.Dispose, () => runtimeHandle is null || runtimeHandle.IsClosed);
            work.Token.ThrowIfCancellationRequested(); // Observed pre-birth refusal, not atomic cancellation ordering.
            attempted = true;
            Require(process.Start(), "Owned observation helper did not start.");
            CaptureOutput();
            CaptureError();
            outputTask = ReadAsync(stdout!.BaseStream, maximumOutput, true, readers.Token);
            errorTask = ReadAsync(stderr!.BaseStream, 16384, false, readers.Token);
            exitTask = process.WaitForExitAsync(readers.Token);
            await BindLiveAsync(work.Token);
            work.Token.ThrowIfCancellationRequested();
            var pending = new List<Task> { outputTask, errorTask, exitTask };
            while (pending.Count != 0)
            {
                Task done = await Task.WhenAny(pending).WaitAsync(work.Token);
                await done;
                work.Token.ThrowIfCancellationRequested();
                pending.Remove(done);
            }
            Require(Exited() && stdoutEof && stderrEof && process.ExitCode == 0,
                "Owned observation helper failed.");
            work.Token.ThrowIfCancellationRequested();
            return await outputTask;
        }

        private async Task SettleReaderAsync(Task? original, StreamReader? stream, bool output, CancellationToken token)
        {
            if (original is not null)
            {
                await ObserveSettledAsync(original, token);
            }
            if (output ? stdoutEof : stderrEof) return;
            Require(stream is not null, "Owned reader unavailable.");
            Task? drain = output ? outputDrain : errorDrain;
            if (drain is null || drain.IsCompleted)
            {
                if (drain?.IsFaulted == true) _ = drain.Exception;
                drain = ReadAsync(stream!.BaseStream, output ? maximumOutput : 16384, output, token);
                if (output) outputDrain = drain; else errorDrain = drain;
            }
            await drain.WaitAsync(token);
        }

        internal async Task<bool> CleanupAsync()
        {
            if (closed) return true;
            Require(RecoveryAllowed(cleanupBudgets.Count), "Owned cleanup attempt budget exhausted.");
            var finite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var budgetClose = new CloseOnce(finite.Dispose, () => true);
            cleanupBudgets.Add((finite, budgetClose));
            bool released = false;
            try { released = await CleanupCoreAsync(finite.Token); }
            finally
            {
                await AttemptIndependentlyAsync([
                    () => { finite.Cancel(); return Task.CompletedTask; },
                    () => { budgetClose.Attempt(); return Task.CompletedTask; },
                ], Refuse);
            }
            closed = released && cleanupBudgets.All(item => item.Close.Completed);
            return closed;
        }

        private async Task<bool> CleanupCoreAsync(CancellationToken token)
        {
            if (attempted)
            {
                await AttemptIndependentlyAsync([
                    () => { CaptureOutput(); return Task.CompletedTask; },
                    () => { CaptureError(); return Task.CompletedTask; },
                    async () =>
                    {
                        if (Exited()) return;
                        await BindLiveAsync(token);
                        if (Exited()) return;
                        Require(pidfd is not null && !pidfd.IsInvalid && !pidfd.IsClosed, "Owned live signal unavailable.");
                        signalInvoked = true;
                        int signalResult = pidfd_send_signal(pidfd!, 9, IntPtr.Zero, 0);
                        int signalError = Marshal.GetLastPInvokeError();
                        signalSucceeded = signalResult == 0;
                        if (signalResult != 0)
                            Require(signalError == 3 && Exited(), "Owned pidfd signal failed.");
                    },
                    async () =>
                    {
                        if (cleanupExitTask is null || (cleanupExitTask.IsCompleted && !cleanupExitTask.IsCompletedSuccessfully))
                        {
                            if (cleanupExitTask?.IsFaulted == true) _ = cleanupExitTask.Exception;
                            cleanupExitTask = process!.WaitForExitAsync();
                        }
                        await cleanupExitTask.WaitAsync(token);
                        Require(Exited(), "Owned original child remains unsettled.");
                    },
                    () => { readers?.Cancel(); return Task.CompletedTask; },
                    async () => await SettleReaderAsync(outputTask, stdout, true, token),
                    async () => await SettleReaderAsync(errorTask, stderr, false, token),
                ], Refuse);
                Task?[] tasks = [outputTask, errorTask, exitTask, cleanupExitTask, outputDrain, errorDrain];
                foreach (Task? task in tasks) if (task?.IsFaulted == true) _ = task.Exception;
                if (!ReleaseAllowed(exitVerified, stdoutEof, stderrEof,
                    stdoutHandle is not null && stderrHandle is not null, tasks))
                {
                    Refuse(new InvalidDataException("Owned observation cleanup remains uncertain."));
                    return false;
                }
            }
            var closures = new List<CloseOnce?> { stdoutClose, stderrClose, pidfdClose, processClose, readersClose, workClose };
            closures.AddRange(metadata.Select(item => item.Close));
            await AttemptIndependentlyAsync(closures.Where(item => item is not null).Select(item => (Func<Task>)(() =>
            {
                item!.Attempt();
                return Task.CompletedTask;
            })), Refuse);
            return closures.All(item => item is null || item.Completed);
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int pidfd_open(int pid, uint flags);
        [DllImport("libc", SetLastError = true)]
        private static extern int pidfd_send_signal(SafeFileHandle pidfd, int signal, IntPtr info, uint flags);
    }
}
