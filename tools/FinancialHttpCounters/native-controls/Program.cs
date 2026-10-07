using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FinancialHttpCounters;

// A synthetic Linux codec witness; neither its faults nor positive capture admit financial hosts.
NativeDiagnostics.Mark(NativeControl.Context);
if (!OperatingSystem.IsLinux() || args.Length != 2) return 2;
NativeDiagnostics.Mark(NativeControl.AdmissionBegin);
await AdmissionNativeControls.RunAsync();
string dotnet = args[0], host = args[1];
NativeDiagnostics.Mark(NativeControl.CallerPaths);
ProcessAdmission.CanonicalPath(dotnet); ProcessAdmission.CanonicalPath(host);
NativeDiagnostics.Mark(NativeControl.FaultBegin);
bool faultCleanup = await ExerciseAsync(dotnet, host, injectSetupFailure: true);
NativeDiagnostics.Mark(NativeControl.FaultResult);
CounterState.Require(faultCleanup, "Actual post-birth setup-failure cleanup control failed");
NativeDiagnostics.Mark(NativeControl.CodecBegin);
bool passed = await ExerciseAsync(dotnet, host, injectSetupFailure: false);
NativeDiagnostics.Mark(NativeControl.CodecResult);
NativeDiagnostics.Mark(NativeControl.Receipt);
CounterState.Require(!NativeDiagnostics.EmissionFailed, "Fixed native diagnostic emission incomplete");
Console.WriteLine(JsonSerializer.Serialize(new
{
    NativeEventPipeCodecWitnessPassed = passed,
    NativePostBirthFaultCleanupPassed = faultCleanup,
    SourceAdmissionAccepted = false,
    RealBusinessSchemaAccepted = false,
    SmtpNoSendProven = false,
    SocketNoSendProven = false,
    GenuineEightHostFinancialAccepted = false
}));
return passed ? 0 : 1;

static async Task<bool> ExerciseAsync(string dotnet, string host, bool injectSetupFailure)
{
    // Every owned object/deadline exists before attempted childbirth.
    NativeDiagnostics.Mark(NativeControl.WitnessReserve);
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    int port;
    var reserve = new TcpListener(IPAddress.Loopback, 0);
    try { reserve.Start(); port = ((IPEndPoint)reserve.LocalEndpoint).Port; }
    finally { reserve.Stop(); }
    NativeDiagnostics.Mark(NativeControl.WitnessConfigure);
    var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(host); info.ArgumentList.Add(port.ToString());
    using var child = new Process { StartInfo = info };
    EventCollector? collector = null;
    Task? stdout = null, stderr = null;
    (ulong Ticks, int Parent)? generation = null;
    long? nativeStart = null;
    bool birthAttempted = false, associated = false, setupFailureObserved = false, passed = false;
    Exception? primary = null;
    async Task Discard(Stream stream)
    {
        byte[] buffer = new byte[4096]; long total = 0; int count;
        while ((count = await stream.ReadAsync(buffer, budget.Token)) > 0)
            CounterState.Require((total += count) <= 1024 * 1024, "Private witness output exceeded budget");
    }
    try
    {
        NativeDiagnostics.Mark(NativeControl.WitnessBirth);
        birthAttempted = true;
        CounterState.Require(child.Start(), "Exact disposable witness child unavailable");
        associated = true; _ = child.SafeHandle;
        NativeDiagnostics.Mark(NativeControl.WitnessSetup);
        generation = await ProcessAdmission.KernelAsync(child.Id, budget.Token);
        nativeStart = child.StartTime.ToUniversalTime().Ticks;
        CounterState.Require(generation.Value.Parent == Environment.ProcessId, "Actual owned witness parent differs");
        // Causal failure AFTER a real child exists, BEFORE reader setup. No supplied success callback.
        if (injectSetupFailure)
        {
            NativeDiagnostics.Mark(NativeControl.WitnessInjectedFailure);
            throw new InjectedWitnessSetupFailure();
        }
        NativeDiagnostics.Mark(NativeControl.WitnessReaders);
        stdout = Discard(child.StandardOutput.BaseStream);
        stderr = Discard(child.StandardError.BaseStream);
        Uri origin = new($"http://127.0.0.1:{port}/");
        using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };
        NativeDiagnostics.Mark(NativeControl.WitnessReadiness);
        for (int attempt = 0; ; attempt++)
        {
            CounterState.Require(attempt < 30 && !child.HasExited, "Actual witness host readiness failed");
            try { using var health = await http.GetAsync("/health", budget.Token); if (health.IsSuccessStatusCode) break; }
            catch (HttpRequestException) { }
            await Task.Delay(100, budget.Token);
        }
        NativeDiagnostics.Mark(NativeControl.WitnessPin);
        async Task<FilePin> Pin(string path) => new(path, Convert.ToHexStringLower(SHA256.HashData(await ProcessAdmission.BoundedFileAsync(path, 67108864, budget.Token))));
        var processPin = new ProcessPin("Accounting", child.Id, (await ProcessAdmission.KernelAsync(child.Id, budget.Token)).Ticks,
            child.StartTime.ToUniversalTime().Ticks, await Pin(dotnet), await Pin(host), await Pin(Path.ChangeExtension(host, ".runtimeconfig.json")),
            "", new(), "", "", "", []);
        collector = new EventCollector(processPin, origin, origin, budget.Token);
        NativeDiagnostics.Mark(NativeControl.WitnessCollectorStart);
        await collector.StartAsync(budget.Token);
        // Attach is followed by a real request in the observed child, never a
        // supplied counter or callback. Require the decoded hosting schema canary.
        NativeDiagnostics.Mark(NativeControl.WitnessCanary);
        using (var health = await http.GetAsync("/health", budget.Token)) health.EnsureSuccessStatusCode();
        for (int attempt = 0; ; attempt++)
        {
            collector.CheckReader();
            bool canary; lock (collector.Gate) canary = collector.State.HealthCanaries > 0 && collector.State.Heartbeats > 0;
            if (canary) break;
            CounterState.Require(attempt < 100, "Native diagnostic canary not dispatched");
            await Task.Delay(100, budget.Token);
        }
        await Task.Delay(1000, budget.Token); DateTime before = DateTime.UtcNow;
        NativeDiagnostics.Mark(NativeControl.WitnessCompletion);
        using (var trigger = await http.GetAsync("/trigger", budget.Token)) trigger.EnsureSuccessStatusCode();
        await Task.Delay(1000, budget.Token); DateTime completion = DateTime.UtcNow;
        NativeDiagnostics.Mark(NativeControl.WitnessReplay);
        using (var replay = await http.GetAsync("/replay", budget.Token)) replay.EnsureSuccessStatusCode();
        await Task.Delay(1000, budget.Token); DateTime after = DateTime.UtcNow;
        NativeDiagnostics.Mark(NativeControl.WitnessDrain);
        await collector.StopAndDrainAsync(budget.Token);
        NativeDiagnostics.Mark(NativeControl.WitnessCounters);
        var first = collector.State.FinalizeWindow(before, completion, TimeSpan.FromMilliseconds(250));
        var second = collector.State.FinalizeWindow(completion, after, TimeSpan.FromMilliseconds(250));
        CounterState.Require(first["AccountingInvoiceRenderPost"].Started == 1 && first["AccountingFileUploadPost"].Started == 1
            && first["AccountingOtherUploadBoundaryMethod"].Started == 1 && second.Values.All(x => x.Started == 0), "Actual native POST/DELETE/replay decoding differs");
        CounterState.Require(collector.Drained && collector.Lost == 0 && (await ProcessAdmission.KernelAsync(child.Id, budget.Token)).Ticks == processPin.KernelStartTicks
            && child.StartTime.ToUniversalTime().Ticks == processPin.NativeStartUtcTicks && !child.HasExited, "Actual witness generation/drain differs");
        passed = true;
    }
    catch (InjectedWitnessSetupFailure) when (injectSetupFailure) { setupFailureObserved = true; }
    catch (Exception error) { primary = error; throw; }
    finally
    {
        NativeDiagnostics.Mark(NativeControl.WitnessCleanup);
        bool cleanup = true;
        // Disposal of one resource never skips independent child/reader cleanup attempts.
        try { if (collector is not null) await collector.DisposeAsync(); }
        catch (Exception) { cleanup = false; }
        using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Recover the retained object's association even if Start/metadata setup threw.
            _ = child.SafeHandle; associated = true;
            child.Refresh();
            if (!child.HasExited)
            {
                var actual = await ProcessAdmission.KernelAsync(child.Id, cleanupBudget.Token);
                long actualNative = child.StartTime.ToUniversalTime().Ticks;
                CounterState.Require(actual.Parent == Environment.ProcessId && (!generation.HasValue || generation.Value == actual)
                    && (!nativeStart.HasValue || nativeStart.Value == actualNative), "Owned child generation uncertain");
                generation ??= actual; nativeStart ??= actualNative;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        child.Refresh();
                        if (child.HasExited) break;
                        CounterState.Require(await ProcessAdmission.KernelAsync(child.Id, cleanupBudget.Token) == generation.Value
                            && child.StartTime.ToUniversalTime().Ticks == nativeStart.Value, "Owned child changed before signal");
                        child.Kill(); break; // Exact retained child only; never names/ports/group-wide kills.
                    }
                    catch (Exception) { cleanup = false; }
                }
            }
        }
        catch (Exception) { if (birthAttempted) cleanup = false; }
        // Wait/absence and readers are attempted even if association, signal or collector disposal failed.
        if (associated)
        {
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); CounterState.Require(child.HasExited, "Owned child exit uncertain"); }
            catch (Exception) { cleanup = false; }
        }
        foreach (var reader in new[] { stdout, stderr })
        {
            if (reader is null) continue;
            try { await reader.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { cleanup = false; }
        }
        try { budget.Cancel(); } catch (Exception) { cleanup = false; }
        if (associated)
        {
            try { child.StandardOutput.Dispose(); } catch (Exception) { cleanup = false; }
            try { child.StandardError.Dispose(); } catch (Exception) { cleanup = false; }
        }
        foreach (var reader in new[] { stdout, stderr })
        {
            if (reader is null) continue;
            try { await reader.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { cleanup = false; }
        }
        // Preserve the original failure; recovered uncertainty never becomes a pass.
        if (!cleanup)
        {
            if (primary is not null) primary.Data["OwnedCleanupVerified"] = false;
            else throw new InvalidDataException("Owned witness cleanup uncertain");
        }
    }
    return injectSetupFailure ? associated && setupFailureObserved && child.HasExited : passed;
}

internal sealed class InjectedWitnessSetupFailure : Exception { }

internal static class NativeDiagnostics
{
    internal static bool EmissionFailed { get; private set; }

    internal static void Mark(NativeControl control)
    {
        // Fixed strings only; diagnostic failure never skips owned-resource cleanup.
        string? marker = control switch
        {
            NativeControl.Context => "{\"NativeControl\":\"Context\"}",
            NativeControl.AdmissionBegin => "{\"NativeControl\":\"AdmissionBegin\"}",
            NativeControl.AdmissionRootCreate => "{\"NativeControl\":\"AdmissionRootCreate\"}",
            NativeControl.AdmissionFifoCreate => "{\"NativeControl\":\"AdmissionFifoCreate\"}",
            NativeControl.AdmissionFifoReject => "{\"NativeControl\":\"AdmissionFifoReject\"}",
            NativeControl.AdmissionDeviceReject => "{\"NativeControl\":\"AdmissionDeviceReject\"}",
            NativeControl.AdmissionDirectoryReject => "{\"NativeControl\":\"AdmissionDirectoryReject\"}",
            NativeControl.AdmissionPrivateFileCreate => "{\"NativeControl\":\"AdmissionPrivateFileCreate\"}",
            NativeControl.AdmissionSymlinkReplacement => "{\"NativeControl\":\"AdmissionSymlinkReplacement\"}",
            NativeControl.AdmissionSymlinkRecovery => "{\"NativeControl\":\"AdmissionSymlinkRecovery\"}",
            NativeControl.AdmissionSameBytesReplacement => "{\"NativeControl\":\"AdmissionSameBytesReplacement\"}",
            NativeControl.AdmissionReplacementRecovery => "{\"NativeControl\":\"AdmissionReplacementRecovery\"}",
            NativeControl.AdmissionModeReject => "{\"NativeControl\":\"AdmissionModeReject\"}",
            NativeControl.AdmissionExpiredReject => "{\"NativeControl\":\"AdmissionExpiredReject\"}",
            NativeControl.AdmissionOutputPrepare => "{\"NativeControl\":\"AdmissionOutputPrepare\"}",
            NativeControl.AdmissionRedirectedOutput => "{\"NativeControl\":\"AdmissionRedirectedOutput\"}",
            NativeControl.AdmissionReplacedOutput => "{\"NativeControl\":\"AdmissionReplacedOutput\"}",
            NativeControl.AdmissionPositiveOutput => "{\"NativeControl\":\"AdmissionPositiveOutput\"}",
            NativeControl.AdmissionCleanup => "{\"NativeControl\":\"AdmissionCleanup\"}",
            NativeControl.CallerPaths => "{\"NativeControl\":\"CallerPaths\"}",
            NativeControl.FaultBegin => "{\"NativeControl\":\"FaultBegin\"}",
            NativeControl.WitnessReserve => "{\"NativeControl\":\"WitnessReserve\"}",
            NativeControl.WitnessConfigure => "{\"NativeControl\":\"WitnessConfigure\"}",
            NativeControl.WitnessBirth => "{\"NativeControl\":\"WitnessBirth\"}",
            NativeControl.WitnessSetup => "{\"NativeControl\":\"WitnessSetup\"}",
            NativeControl.WitnessInjectedFailure => "{\"NativeControl\":\"WitnessInjectedFailure\"}",
            NativeControl.WitnessReaders => "{\"NativeControl\":\"WitnessReaders\"}",
            NativeControl.WitnessReadiness => "{\"NativeControl\":\"WitnessReadiness\"}",
            NativeControl.WitnessPin => "{\"NativeControl\":\"WitnessPin\"}",
            NativeControl.WitnessCollectorStart => "{\"NativeControl\":\"WitnessCollectorStart\"}",
            NativeControl.WitnessCanary => "{\"NativeControl\":\"WitnessCanary\"}",
            NativeControl.WitnessCompletion => "{\"NativeControl\":\"WitnessCompletion\"}",
            NativeControl.WitnessReplay => "{\"NativeControl\":\"WitnessReplay\"}",
            NativeControl.WitnessDrain => "{\"NativeControl\":\"WitnessDrain\"}",
            NativeControl.WitnessCounters => "{\"NativeControl\":\"WitnessCounters\"}",
            NativeControl.WitnessCleanup => "{\"NativeControl\":\"WitnessCleanup\"}",
            NativeControl.FaultResult => "{\"NativeControl\":\"FaultResult\"}",
            NativeControl.CodecBegin => "{\"NativeControl\":\"CodecBegin\"}",
            NativeControl.CodecResult => "{\"NativeControl\":\"CodecResult\"}",
            NativeControl.Receipt => "{\"NativeControl\":\"Receipt\"}",
            _ => null
        };
        if (marker is null) { EmissionFailed = true; return; }
        try { Console.Error.WriteLine(marker); }
        catch (Exception) { EmissionFailed = true; }
    }
}

internal enum NativeControl
{
    Context,
    AdmissionBegin,
    AdmissionRootCreate,
    AdmissionFifoCreate,
    AdmissionFifoReject,
    AdmissionDeviceReject,
    AdmissionDirectoryReject,
    AdmissionPrivateFileCreate,
    AdmissionSymlinkReplacement,
    AdmissionSymlinkRecovery,
    AdmissionSameBytesReplacement,
    AdmissionReplacementRecovery,
    AdmissionModeReject,
    AdmissionExpiredReject,
    AdmissionOutputPrepare,
    AdmissionRedirectedOutput,
    AdmissionReplacedOutput,
    AdmissionPositiveOutput,
    AdmissionCleanup,
    CallerPaths,
    FaultBegin,
    WitnessReserve,
    WitnessConfigure,
    WitnessBirth,
    WitnessSetup,
    WitnessInjectedFailure,
    WitnessReaders,
    WitnessReadiness,
    WitnessPin,
    WitnessCollectorStart,
    WitnessCanary,
    WitnessCompletion,
    WitnessReplay,
    WitnessDrain,
    WitnessCounters,
    WitnessCleanup,
    FaultResult,
    CodecBegin,
    CodecResult,
    Receipt
}
