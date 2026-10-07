using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using FinancialHttpCounters;

// Each case runs in its own hosted parent process; all child output stays private.
internal static class WitnessLifecycleControls
{
    internal static async Task<int> RunAsync(string dotnet, string nativeAssembly, string control)
    {
        CounterState.Require(control is "PostBirthSetupFault" or "ReaderOverflow" or "RetainedWaitTimeout" or "CleanupDeadlineRecovery" or "CollectorReaderFault",
            "Known witness lifecycle control required");
        ProcessAdmission.CanonicalPath(dotnet); ProcessAdmission.CanonicalPath(nativeAssembly);
        CounterState.Require(nativeAssembly == typeof(WitnessLease).Assembly.Location, "Exact native control assembly required");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var info = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        info.ArgumentList.Add(nativeAssembly); info.ArgumentList.Add("--witness-pipe-child");
        var fault = control == "CleanupDeadlineRecovery" ? WitnessControlFault.FirstCleanupDeadline : WitnessControlFault.None;
        var witness = await WitnessLease.AcquireAsync(info, budget.Token, fault);
        bool setupFault = false, readerFault = false, waitTimeout = false, pendingAfterTimeout = false;
        bool collectorReaderFault = false;
        EventCollector? observedCollector = null;
        bool initialCleanupRefused = false, quarantineRetained = false, recoveryCompleted = false, stickyAdmissionRefused = false;
        WitnessLease.CleanupReceipt? recovered = null;
        bool released = false, clean = false;
        try
        {
            witness.Start();
            await witness.BindLiveAsync(budget.Token);
            CounterState.Require(witness.Associated && !witness.HasExited, "Actual bound witness child required");
            if (control == "PostBirthSetupFault")
            {
                try { throw new InjectedWitnessControlSetupFailure(); }
                catch (InjectedWitnessControlSetupFailure) { setupFault = true; }
            }
            else
            {
                witness.StartReaders(control == "ReaderOverflow" ? 1 : 1048576);
                if (control == "ReaderOverflow")
                {
                    await witness.ObserveReaderFaultAsync(budget.Token);
                    readerFault = true;
                }
                if (control == "RetainedWaitTimeout")
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                    try { await witness.ObserveExitAsync(wait.Token); }
                    catch (OperationCanceledException) { waitTimeout = wait.IsCancellationRequested; }
                    pendingAfterTimeout = witness.ExitObservationPending && !witness.HasExited;
                    CounterState.Require(waitTimeout && pendingAfterTimeout, "Actual exit task must remain retained after wrapper timeout");
                }
                if (control == "CollectorReaderFault")
                {
                    async Task<FilePin> Pin(string path) => new(path,
                        Convert.ToHexStringLower(SHA256.HashData(await ProcessAdmission.BoundedFileAsync(path, 67108864, budget.Token))));
                    var pin = new ProcessPin("Accounting", witness.Pid, (await ProcessAdmission.KernelAsync(witness.Pid, budget.Token)).Ticks,
                        witness.NativeStartUtcTicks, await Pin(dotnet), await Pin(nativeAssembly), await Pin(Path.ChangeExtension(nativeAssembly, ".runtimeconfig.json")),
                        "", new(), "", "", "", []);
                    Uri origin = new("http://127.0.0.1:1/"); // No HTTP request or bind; selected origins are unused in this transport fault.
                    observedCollector = new EventCollector(pin, origin, origin, budget.Token, CollectorControlFault.TraceBudgetOneByte);
                    witness.AttachCollector(observedCollector);
                    await observedCollector.StartAsync(budget.Token);
                    for (int attempt = 0; ; attempt++)
                    {
                        try { observedCollector.CheckReader(); }
                        catch (InvalidDataException)
                        {
                            CounterState.Require(observedCollector.TraceBudgetFaultObserved,
                                "Actual collector byte-budget overflow required");
                            collectorReaderFault = true;
                            break;
                        }
                        CounterState.Require(attempt < 100, "Actual collector byte-budget fault required");
                        await Task.Delay(100, budget.Token);
                    }
                }
            }
        }
        finally
        {
            try { clean = await witness.ReleaseAsync(); }
            catch (OperationCanceledException) when (control == "CleanupDeadlineRecovery")
            { initialCleanupRefused = witness.FirstCleanupCancellationObserved; }
            finally { released = true; }
        }
        quarantineRetained = WitnessLease.IsQuarantined(witness);
        if (control == "CleanupDeadlineRecovery")
        {
            CounterState.Require(initialCleanupRefused && quarantineRetained && witness.ExitObservationPending && !witness.HasExited && !clean,
                "Actual incomplete lease must remain reachable");
            recoveryCompleted = await WitnessLease.RetryPhysicalCleanupAsync(budget.Token);
            recovered = witness.Snapshot();
            WitnessLease? unexpected = null;
            try { unexpected = await WitnessLease.AcquireAsync(info, budget.Token); }
            catch (InvalidDataException) { stickyAdmissionRefused = true; }
            finally { if (unexpected is not null) await unexpected.ReleaseAsync(); }
            CounterState.Require(recoveryCompleted && witness.CleanupOriginallyFailed && stickyAdmissionRefused,
                "Physical recovery must not clear original admission refusal");
        }
        else if (control == "ReaderOverflow")
        {
            CounterState.Require(readerFault && !clean && witness.PhysicalCleanupCompleted && witness.CleanupOriginallyFailed,
                "Actual bounded reader failure must remain refused after physical cleanup");
        }
        else if (control == "CollectorReaderFault")
        {
            CounterState.Require(collectorReaderFault && !clean && witness.PhysicalCleanupCompleted && witness.CleanupOriginallyFailed
                && observedCollector is { CleanupQuiescent: true, CleanupOriginallyFailed: true },
                "Actual collector fault must quiesce physically while refusal stays sticky");
        }
        else CounterState.Require(clean && (setupFault || (waitTimeout && pendingAfterTimeout)), "Actual witness lifecycle control failed");
        CounterState.Require(released && witness.PhysicalCleanupCompleted && witness.ExactExitObserved,
            "Actual owned witness physical cleanup required");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ControlCase = control,
            Passed = true,
            SetupFaultObserved = setupFault,
            ReaderFaultObserved = readerFault,
            CollectorReaderFaultObserved = collectorReaderFault,
            WaitTimeoutObserved = waitTimeout,
            RetainedWaitPendingAfterTimeout = pendingAfterTimeout,
            FirstCleanupDeadlineObserved = initialCleanupRefused,
            QuarantineRetained = quarantineRetained,
            PhysicalRecoveryCompleted = recoveryCompleted,
            StickyAdmissionRefused = stickyAdmissionRefused,
            Cleanup = WitnessLease.Receipts,
            RecoveredCleanup = recovered,
            NativeEventPipeCodecAccepted = false,
            GenuineEightHostFinancialAccepted = false
        }));
        return 0;
    }

    private sealed class InjectedWitnessControlSetupFailure : Exception { }
}
