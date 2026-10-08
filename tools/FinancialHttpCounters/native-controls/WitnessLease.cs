using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FinancialHttpCounters;
using Microsoft.Win32.SafeHandles;

// Owns only this exact synthetic child, its streams, and its attached collector.
// A refused lease remains reachable for a later fresh cleanup attempt.
internal sealed class WitnessLease
{
    private static readonly SemaphoreSlim Admission = new(1, 1);
    private static WitnessLease? quarantine;
    private static bool admissionRefused;
    private static readonly List<CleanupReceipt> receipts = [];
    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<CleanupReceipt> receiptView = receipts.AsReadOnly();
    private readonly Process process;
    private readonly CancellationToken operation;
    private readonly CancellationTokenSource readers;
    private SafeHandle? processHandle, outputHandle, errorHandle, inputHandle;
    private SafeFileHandle? pidfd;
    private StreamReader? output, error;
    private StreamWriter? input;
    private Task? outputTask, errorTask, exitTask, cleanupExitTask, outputDrainTask, errorDrainTask;
    private EventCollector? collector;
    private Task? collectorCleanupTask;
    private (ulong Ticks, int Parent)? generation;
    private long? nativeStart;
    private bool attempted, associated, bound, outputEof, errorEof, exited, closed, released;
    private bool cleanupRefused;
    private readonly WitnessControlFault controlFault;
    private bool firstCleanupAttempt = true;
    internal bool FirstCleanupCancellationObserved { get; private set; }
    internal bool PhysicalCleanupCompleted => closed;
    internal bool CleanupOriginallyFailed => cleanupRefused;
    internal bool ExitObservationPending => exitTask is { IsCompleted: false };

    private WitnessLease(ProcessStartInfo info, CancellationToken operation, WitnessControlFault controlFault)
    {
        this.operation = operation;
        this.controlFault = controlFault;
        readers = CancellationTokenSource.CreateLinkedTokenSource(operation);
        try { process = new Process { StartInfo = info }; }
        catch { readers.Dispose(); throw; }
    }

    internal int Pid => process.Id;
    internal long NativeStartUtcTicks => process.StartTime.ToUniversalTime().Ticks;
    internal bool HasExited => Exited();
    internal bool Associated => associated;
    internal bool ExactExitObserved => exited;
    internal static IReadOnlyList<CleanupReceipt> Receipts => receiptView;

    internal static async Task<WitnessLease> AcquireAsync(ProcessStartInfo info, CancellationToken token,
        WitnessControlFault controlFault = WitnessControlFault.None)
    {
        await Admission.WaitAsync(token);
        try
        {
            if (quarantine is not null)
            {
                // A previous incomplete cleanup is retried, never a cached failed Task.
                await quarantine.CleanupAsync();
            }
            CounterState.Require(!admissionRefused && quarantine is null, "Native witness quarantine retained");
            return new WitnessLease(info, token, controlFault);
        }
        catch { Admission.Release(); throw; }
    }

    private static SafeHandle Handle(Stream stream) => stream switch
    {
        FileStream file => file.SafeFileHandle,
        PipeStream pipe => pipe.SafePipeHandle,
        _ => throw new InvalidDataException("Owned witness pipe handle unavailable")
    };

    private void CaptureStreams()
    {
        output ??= process.StandardOutput; outputHandle ??= Handle(output.BaseStream);
        error ??= process.StandardError; errorHandle ??= Handle(error.BaseStream);
        input ??= process.StandardInput; inputHandle ??= Handle(input.BaseStream);
    }

    internal void Start()
    {
        CounterState.Require(OperatingSystem.IsLinux(), "Hosted Linux witness required");
        operation.ThrowIfCancellationRequested();
        attempted = true;
        CounterState.Require(process.Start(), "Exact disposable witness child unavailable");
        associated = true;
        processHandle = process.SafeHandle; // Retained runtime wait association, never used for signalling.
        exitTask = process.WaitForExitAsync();
        CaptureStreams();
        input!.Dispose(); // The synthetic host takes arguments only, never interactive input.
    }

    private bool Exited()
    {
        if (exited) return true;
        process.Refresh();
        exited = process.HasExited; // Original .NET child wait state, never numeric PID absence.
        return exited;
    }

    internal async Task BindLiveAsync(CancellationToken token)
    {
        if (Exited()) return;
        processHandle ??= process.SafeHandle;
        if (Exited()) return;
        try
        {
            var before = await ProcessAdmission.KernelAsync(process.Id, token);
            long beforeNative = process.StartTime.ToUniversalTime().Ticks;
            if (Exited()) return;
            CounterState.Require(before.Parent == Environment.ProcessId
                && (!generation.HasValue || generation.Value == before)
                && (!nativeStart.HasValue || nativeStart.Value == beforeNative), "Owned witness generation differs");
            generation ??= before; nativeStart ??= beforeNative;
            if (pidfd is null)
            {
                int descriptor = pidfd_open(process.Id, 0);
                if (descriptor < 0)
                {
                    if (Exited()) return;
                    throw new InvalidDataException("Owned witness pidfd unavailable");
                }
                pidfd = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            }
            // If original exit happened during acquisition, no descriptor is ever signalled.
            if (Exited()) return;
            var after = await ProcessAdmission.KernelAsync(process.Id, token);
            long afterNative = process.StartTime.ToUniversalTime().Ticks;
            if (Exited()) return;
            CounterState.Require(after == generation.Value && afterNative == nativeStart.Value
                && !pidfd.IsInvalid && !pidfd.IsClosed, "Owned witness pidfd association differs");
            bound = true;
        }
        catch
        {
            if (Exited()) return;
            throw;
        }
    }

    internal void StartReaders(long maximum = 1048576)
    {
        CounterState.Require(outputTask is null && errorTask is null, "Witness readers already started");
        CaptureStreams();
        CounterState.Require(maximum is > 0 and <= 1048576, "Finite witness output budget required");
        outputTask = DiscardAsync(output!.BaseStream, outputPipe: true, readers.Token, maximum);
        errorTask = DiscardAsync(error!.BaseStream, outputPipe: false, readers.Token, maximum);
    }

    private async Task DiscardAsync(Stream stream, bool outputPipe, CancellationToken token, long maximum = 1048576)
    {
        byte[] buffer = new byte[4096]; long total = 0; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
            CounterState.Require((total += count) <= maximum, "Finite private witness output exceeded");
        if (outputPipe) outputEof = true; else errorEof = true;
    }

    internal void CheckReaders()
    {
        CounterState.Require(outputTask?.IsFaulted != true && errorTask?.IsFaulted != true
            && outputTask?.IsCanceled != true && errorTask?.IsCanceled != true,
            "Owned witness reader failed");
    }

    internal async Task ObserveReaderFaultAsync(CancellationToken token)
    {
        CounterState.Require(outputTask is not null && errorTask is not null, "Actual witness readers required");
        Task actual = await Task.WhenAny(outputTask!, errorTask!).WaitAsync(token);
        CounterState.Require(actual.IsFaulted, "Actual bounded reader fault required");
        _ = actual.Exception;
    }

    internal async Task ObserveExitAsync(CancellationToken token)
    {
        CounterState.Require(exitTask is not null, "Retained actual exit observation required");
        await exitTask!.WaitAsync(token);
    }

    internal static bool IsQuarantined(WitnessLease value) => ReferenceEquals(quarantine, value);

    internal static async Task<bool> RetryPhysicalCleanupAsync(CancellationToken token)
    {
        await Admission.WaitAsync(token);
        try { return quarantine is not null && await quarantine.CleanupAsync(); }
        finally { Admission.Release(); }
    }

    internal void AttachCollector(EventCollector value)
    {
        CounterState.Require(collector is null, "Witness collector already attached");
        collector = value;
    }

    private async Task<bool> QuiesceCollectorAsync(CancellationToken token)
    {
        if (collector is null) return true;
        if (collectorCleanupTask is null || collectorCleanupTask.IsCompleted)
        {
            if (collectorCleanupTask?.IsFaulted == true) _ = collectorCleanupTask.Exception;
            collectorCleanupTask = collector.DisposeAsync().AsTask();
        }
        try { await collectorCleanupTask.WaitAsync(token); }
        catch { cleanupRefused = true; }
        // A fault may have physically settled resources, but its refusal stays sticky.
        return collectorCleanupTask.IsCompleted && collector.CleanupQuiescent;
    }

    private async Task QuiescePipeAsync(Task? reading, StreamReader? owner, bool outputPipe, CancellationToken token)
    {
        if (reading is not null)
        {
            try { await reading.WaitAsync(token); }
            catch { if (!reading.IsCompleted) throw; }
            CounterState.Require(reading.IsCompleted, "Witness reader remains active");
            if (reading.IsFaulted) { _ = reading.Exception; cleanupRefused = true; }
        }
        if (!(outputPipe ? outputEof : errorEof))
        {
            CounterState.Require(owner is not null, "Witness reader ownership unavailable");
            Task? drain = outputPipe ? outputDrainTask : errorDrainTask;
            if (drain is null || drain.IsCompleted)
            {
                if (drain?.IsFaulted == true) { _ = drain.Exception; cleanupRefused = true; }
                drain = DiscardAsync(owner!.BaseStream, outputPipe, token);
                if (outputPipe) outputDrainTask = drain; else errorDrainTask = drain;
            }
            await drain.WaitAsync(token); // The actual read task stays retained on timeout.
        }
    }

    private bool TasksDone() => (outputTask is null || outputTask.IsCompleted)
        && (errorTask is null || errorTask.IsCompleted) && (exitTask is null || exitTask.IsCompleted)
        && (cleanupExitTask is null || cleanupExitTask.IsCompleted)
        && (outputDrainTask is null || outputDrainTask.IsCompleted) && (errorDrainTask is null || errorDrainTask.IsCompleted)
        && (collectorCleanupTask is null || collectorCleanupTask.IsCompleted);

    private async Task<bool> CleanupAsync()
    {
        if (closed) return true;
        if (!attempted)
        {
            try { process.Dispose(); } catch { cleanupRefused = true; }
            try { readers.Dispose(); } catch { cleanupRefused = true; }
            closed = !cleanupRefused;
            return closed;
        }
        using var finite = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        if (firstCleanupAttempt && controlFault == WitnessControlFault.FirstCleanupDeadline)
        {
            finite.Cancel(); // Causal deadline fault after a real, retained child exists.
            FirstCleanupCancellationObserved = finite.IsCancellationRequested;
        }
        firstCleanupAttempt = false;
        try
        {
            // The collector owns operations against this process. Their actual quiescence
            // is a fence before any process/pipe mutation, even after a failed disposal.
            if (!await QuiesceCollectorAsync(finite.Token))
            { cleanupRefused = true; return false; }
            finite.Token.ThrowIfCancellationRequested();
            try { processHandle ??= process.SafeHandle; associated = true; } catch { cleanupRefused = true; }
            try { output ??= process.StandardOutput; outputHandle ??= Handle(output.BaseStream); } catch { cleanupRefused = true; }
            try { error ??= process.StandardError; errorHandle ??= Handle(error.BaseStream); } catch { cleanupRefused = true; }
            try { input ??= process.StandardInput; inputHandle ??= Handle(input.BaseStream); } catch { cleanupRefused = true; }
            try { input?.Dispose(); } catch { cleanupRefused = true; }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (Exited()) break;
                    await BindLiveAsync(finite.Token);
                    if (Exited()) break;
                    CounterState.Require(bound && pidfd is not null && !pidfd.IsClosed, "Witness live signal ownership unavailable");
                    if (pidfd_send_signal(pidfd!, 9, IntPtr.Zero, 0) != 0)
                        CounterState.Require(Marshal.GetLastPInvokeError() == 3 && Exited(), "Witness pidfd signal failed");
                    break;
                }
                catch { cleanupRefused = true; }
            }
            try
            {
                if (cleanupExitTask is null || (cleanupExitTask.IsCompleted && !cleanupExitTask.IsCompletedSuccessfully))
                {
                    if (cleanupExitTask?.IsFaulted == true) _ = cleanupExitTask.Exception;
                    cleanupExitTask = process.WaitForExitAsync();
                }
                await cleanupExitTask.WaitAsync(finite.Token);
                Exited();
            }
            catch { cleanupRefused = true; }
            if (exitTask is not null)
            {
                try { await exitTask.WaitAsync(finite.Token); }
                catch { cleanupRefused = true; }
            }
            try { readers.Cancel(); } catch { cleanupRefused = true; }
            try { await QuiescePipeAsync(outputTask, output, outputPipe: true, finite.Token); } catch { cleanupRefused = true; }
            try { await QuiescePipeAsync(errorTask, error, outputPipe: false, finite.Token); } catch { cleanupRefused = true; }
            if (!exited || !TasksDone() || !outputEof || !errorEof
                || outputHandle is null || errorHandle is null || inputHandle is null)
            { cleanupRefused = true; return false; }
            foreach (var task in new[] { outputTask, errorTask, exitTask, cleanupExitTask, outputDrainTask, errorDrainTask, collectorCleanupTask })
                if (task?.IsFaulted == true) _ = task.Exception;
            // Only exact original exit and every retained operation's completion
            // permit disposal. No unknown live association is disposed or forgotten.
            try { output!.Dispose(); } catch { cleanupRefused = true; }
            try { error!.Dispose(); } catch { cleanupRefused = true; }
            try { pidfd?.Dispose(); } catch { cleanupRefused = true; }
            try { process.Dispose(); } catch { cleanupRefused = true; }
            try { readers.Dispose(); } catch { cleanupRefused = true; }
            closed = outputHandle.IsClosed && errorHandle.IsClosed && inputHandle.IsClosed
                && (pidfd is null || pidfd.IsClosed) && (processHandle is null || processHandle.IsClosed);
            if (!closed) cleanupRefused = true;
            return closed;
        }
        finally
        {
            try { finite.Cancel(); } catch { cleanupRefused = true; }
        }
    }

    internal async Task<bool> ReleaseAsync()
    {
        CounterState.Require(!released, "Witness admission already released");
        bool physical = false;
        try
        {
            physical = await CleanupAsync();
        }
        catch { cleanupRefused = true; throw; }
        finally
        {
            if (!physical || cleanupRefused)
            {
                admissionRefused = true;
                quarantine = this; // All original tasks/streams/handles remain reachable.
            }
            try
            {
                CounterState.Require(receipts.Count < 2, "Finite witness lifecycle ledger exceeded");
                receipts.Add(Snapshot());
            }
            catch { cleanupRefused = true; admissionRefused = true; quarantine = this; }
            finally { released = true; Admission.Release(); }
        }
        return physical && !cleanupRefused;
    }

    internal CleanupReceipt Snapshot() => new(attempted, associated, bound, exited, TasksDone(), outputEof, errorEof,
        outputHandle?.IsClosed == true && errorHandle?.IsClosed == true && inputHandle?.IsClosed == true,
        processHandle?.IsClosed == true, pidfd?.IsClosed == true,
        collector is null || collector.CleanupQuiescent, cleanupRefused, closed);

    internal sealed record CleanupReceipt(bool BirthAttempted, bool ProcessAssociated, bool PidfdBound,
        bool ExactOriginalExitObserved, bool AllRetainedOperationsCompleted, bool OutputEof, bool ErrorEof,
        bool AllPipeHandlesClosed, bool ProcessWaitHandleClosed, bool PidfdClosed, bool CollectorQuiescent,
        bool CleanupOriginallyFailed, bool PhysicalCleanupCompleted);

    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(SafeFileHandle pidfd, int signal, IntPtr info, uint flags);
}

internal enum WitnessControlFault { None, FirstCleanupDeadline }
