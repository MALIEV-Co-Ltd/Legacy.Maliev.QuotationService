using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FinancialHttpCounters;

// Fixed, private-output lifecycle controls run separately from the codec transcript.
if (OperatingSystem.IsLinux() && args.Length == 1 && args[0] == "--witness-pipe-child")
{
    await Console.OpenStandardOutput().WriteAsync(new byte[8192]);
    await Console.OpenStandardError().WriteAsync(new byte[8192]);
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
if (OperatingSystem.IsLinux() && args.Length == 3)
    return await WitnessLifecycleControls.RunAsync(args[0], args[1], args[2]);

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
    GenuineEightHostFinancialAccepted = false,
    NativeWitnessCleanup = WitnessLease.Receipts
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
    var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    info.ArgumentList.Add(host); info.ArgumentList.Add(port.ToString());
    var witness = await WitnessLease.AcquireAsync(info, budget.Token);
    EventCollector? collector = null;
    (ulong Ticks, int Parent)? generation = null;
    bool associated = false, setupFailureObserved = false, passed = false;
    Exception? primary = null;
    try
    {
        NativeDiagnostics.Mark(NativeControl.WitnessBirth);
        witness.Start();
        associated = witness.Associated;
        NativeDiagnostics.Mark(NativeControl.WitnessSetup);
        await witness.BindLiveAsync(budget.Token);
        generation = await ProcessAdmission.KernelAsync(witness.Pid, budget.Token);
        CounterState.Require(generation.Value.Parent == Environment.ProcessId, "Actual owned witness parent differs");
        // Causal failure AFTER a real child exists, BEFORE reader setup. No supplied success callback.
        if (injectSetupFailure)
        {
            NativeDiagnostics.Mark(NativeControl.WitnessInjectedFailure);
            throw new InjectedWitnessSetupFailure();
        }
        NativeDiagnostics.Mark(NativeControl.WitnessReaders);
        witness.StartReaders();
        Uri origin = new($"http://127.0.0.1:{port}/");
        using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };
        NativeDiagnostics.Mark(NativeControl.WitnessReadiness);
        for (int attempt = 0; ; attempt++)
        {
            witness.CheckReaders();
            CounterState.Require(attempt < 30 && !witness.HasExited, "Actual witness host readiness failed");
            try { using var health = await http.GetAsync("/health", budget.Token); if (health.IsSuccessStatusCode) break; }
            catch (HttpRequestException) { }
            await Task.Delay(100, budget.Token);
        }
        NativeDiagnostics.Mark(NativeControl.WitnessPin);
        async Task<FilePin> Pin(string path) => new(path, Convert.ToHexStringLower(SHA256.HashData(await ProcessAdmission.BoundedFileAsync(path, 67108864, budget.Token))));
        var processPin = new ProcessPin("Accounting", witness.Pid, (await ProcessAdmission.KernelAsync(witness.Pid, budget.Token)).Ticks,
            witness.NativeStartUtcTicks, await Pin(dotnet), await Pin(host), await Pin(Path.ChangeExtension(host, ".runtimeconfig.json")),
            "", new(), "", "", "", []);
        collector = new EventCollector(processPin, origin, origin, budget.Token);
        witness.AttachCollector(collector);
        NativeDiagnostics.Mark(NativeControl.WitnessCollectorStart);
        await collector.StartAsync(budget.Token);
        // Attach is followed by a real request in the observed child, never a
        // supplied counter or callback. Require the decoded hosting schema canary.
        NativeDiagnostics.Mark(NativeControl.WitnessCanary);
        using (var health = await http.GetAsync("/health", budget.Token)) health.EnsureSuccessStatusCode();
        for (int attempt = 0; ; attempt++)
        {
            witness.CheckReaders();
            try { collector.CheckReader(); }
            catch (InvalidDataException)
            {
                NativeDiagnostics.ReaderFault(collector.ReaderFault);
                throw;
            }
            bool healthSeen, heartbeatSeen;
            lock (collector.Gate)
            {
                healthSeen = collector.State.HealthCanaries > 0;
                heartbeatSeen = collector.State.Heartbeats > 0;
            }
            if (healthSeen && heartbeatSeen) break;
            if (attempt >= 100)
            {
                NativeDiagnostics.Mark(healthSeen ? NativeControl.CanaryMissingHeartbeat
                    : heartbeatSeen ? NativeControl.CanaryMissingHealth : NativeControl.CanaryMissingBoth);
            }
            CounterState.Require(attempt < 100, "Native diagnostic canary not dispatched");
            await Task.Delay(100, budget.Token);
        }
        await Task.Delay(1000, budget.Token); DateTime before = DateTime.UtcNow;
        // The boundary guard needs quiet time after the recorded boundary as well as before it.
        await Task.Delay(1000, budget.Token);
        NativeDiagnostics.Mark(NativeControl.WitnessCompletion);
        using (var trigger = await http.GetAsync("/trigger", budget.Token)) trigger.EnsureSuccessStatusCode();
        await Task.Delay(1000, budget.Token); DateTime completion = DateTime.UtcNow;
        NativeDiagnostics.Mark(NativeControl.WitnessReplay);
        using (var replay = await http.GetAsync("/replay", budget.Token)) replay.EnsureSuccessStatusCode();
        await Task.Delay(1000, budget.Token); DateTime after = DateTime.UtcNow;
        NativeDiagnostics.Mark(NativeControl.WitnessDrain);
        await collector.StopAndDrainAsync(budget.Token);
        NativeDiagnostics.Mark(NativeControl.WitnessCounters);
        var first = NativeDiagnostics.FinalizeWindow(collector.State, before, completion, TimeSpan.FromMilliseconds(250));
        var second = NativeDiagnostics.FinalizeWindow(collector.State, completion, after, TimeSpan.FromMilliseconds(250));
        NativeDiagnostics.RequirePredicate(first["AccountingInvoiceRenderPost"].Started == 1 && first["AccountingFileUploadPost"].Started == 1
            && first["AccountingOtherUploadBoundaryMethod"].Started == 1 && second.Values.All(x => x.Started == 0), "Actual native POST/DELETE/replay decoding differs", NativeControl.ExpectedEffectCounts);
        NativeDiagnostics.RequirePredicate(collector.Drained && collector.Lost == 0 && (await ProcessAdmission.KernelAsync(witness.Pid, budget.Token)).Ticks == processPin.KernelStartTicks
            && witness.NativeStartUtcTicks == processPin.NativeStartUtcTicks && !witness.HasExited, "Actual witness generation/drain differs", NativeControl.GenerationDrain);
        passed = true;
    }
    catch (InjectedWitnessSetupFailure) when (injectSetupFailure) { setupFailureObserved = true; }
    catch (Exception error) { primary = error; throw; }
    finally
    {
        NativeDiagnostics.Mark(NativeControl.WitnessCleanup);
        bool cleanup = false;
        try { cleanup = await witness.ReleaseAsync(); }
        catch (Exception) { }
        // Preserve the original failure; recovered uncertainty never becomes a pass.
        if (!cleanup)
        {
            if (primary is not null) primary.Data["OwnedCleanupVerified"] = false;
            else throw new InvalidDataException("Owned witness cleanup uncertain");
        }
    }
    return injectSetupFailure ? associated && setupFailureObserved && witness.HasExited : passed;
}

internal sealed class InjectedWitnessSetupFailure : Exception { }

internal static class NativeDiagnostics
{
    internal static bool EmissionFailed { get; private set; }

    internal static Dictionary<string, CounterState.Counts> FinalizeWindow(CounterState state, DateTime from, DateTime until, TimeSpan guard)
    {
        try { return state.FinalizeWindow(from, until, guard); }
        catch (InvalidDataException)
        {
            NativeControl? category = state.WindowFailure switch
            {
                WindowFailureCategory.PendingRequests => NativeControl.WindowPendingRequests,
                WindowFailureCategory.MissingHeartbeat => NativeControl.WindowMissingHeartbeat,
                WindowFailureCategory.MissingHealthCanary => NativeControl.WindowMissingHealthCanary,
                WindowFailureCategory.MissingIncomingListener => NativeControl.WindowMissingIncomingListener,
                WindowFailureCategory.OutgoingListener => NativeControl.WindowOutgoingListener,
                WindowFailureCategory.BoundarySpan => NativeControl.WindowBoundarySpan,
                WindowFailureCategory.GuardInterval => NativeControl.WindowGuardInterval,
                _ => null
            };
            if (category.HasValue) Mark(category.Value);
            throw;
        }
    }

    internal static void RequirePredicate(bool condition, string message, NativeControl category)
    {
        try { CounterState.Require(condition, message); }
        catch (InvalidDataException)
        {
            Mark(category);
            throw;
        }
    }

    internal static void ReaderFault(ReaderFaultCategory category)
    {
        Mark(category switch
        {
            ReaderFaultCategory.ParserSetup => NativeControl.ReaderParserSetup,
            ReaderFaultCategory.ProcessIdentity => NativeControl.ReaderProcessIdentity,
            ReaderFaultCategory.ListenerSchema => NativeControl.ReaderListenerSchema,
            ReaderFaultCategory.BridgeSchema => NativeControl.ReaderBridgeSchema,
            ReaderFaultCategory.ProjectionArguments => NativeControl.ReaderProjectionArguments,
            ReaderFaultCategory.CounterState => NativeControl.ReaderCounterState,
            ReaderFaultCategory.ParserRead => NativeControl.ReaderParserRead,
            ReaderFaultCategory.ParserCompletion => NativeControl.ReaderParserCompletion,
            ReaderFaultCategory.StreamCompletion => NativeControl.ReaderStreamCompletion,
            ReaderFaultCategory.Cancelled => NativeControl.ReaderCancelled,
            _ => NativeControl.ReaderUnknown
        });
    }

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
            NativeControl.ReaderParserSetup => "{\"NativeControl\":\"ReaderParserSetup\"}",
            NativeControl.ReaderProcessIdentity => "{\"NativeControl\":\"ReaderProcessIdentity\"}",
            NativeControl.ReaderListenerSchema => "{\"NativeControl\":\"ReaderListenerSchema\"}",
            NativeControl.ReaderBridgeSchema => "{\"NativeControl\":\"ReaderBridgeSchema\"}",
            NativeControl.ReaderProjectionArguments => "{\"NativeControl\":\"ReaderProjectionArguments\"}",
            NativeControl.ReaderCounterState => "{\"NativeControl\":\"ReaderCounterState\"}",
            NativeControl.ReaderParserRead => "{\"NativeControl\":\"ReaderParserRead\"}",
            NativeControl.ReaderParserCompletion => "{\"NativeControl\":\"ReaderParserCompletion\"}",
            NativeControl.ReaderStreamCompletion => "{\"NativeControl\":\"ReaderStreamCompletion\"}",
            NativeControl.ReaderCancelled => "{\"NativeControl\":\"ReaderCancelled\"}",
            NativeControl.ReaderUnknown => "{\"NativeControl\":\"ReaderUnknown\"}",
            NativeControl.CanaryMissingHeartbeat => "{\"NativeControl\":\"CanaryMissingHeartbeat\"}",
            NativeControl.CanaryMissingHealth => "{\"NativeControl\":\"CanaryMissingHealth\"}",
            NativeControl.CanaryMissingBoth => "{\"NativeControl\":\"CanaryMissingBoth\"}",
            NativeControl.WindowPendingRequests => "{\"NativeControl\":\"WindowPendingRequests\"}",
            NativeControl.WindowMissingHeartbeat => "{\"NativeControl\":\"WindowMissingHeartbeat\"}",
            NativeControl.WindowMissingHealthCanary => "{\"NativeControl\":\"WindowMissingHealthCanary\"}",
            NativeControl.WindowMissingIncomingListener => "{\"NativeControl\":\"WindowMissingIncomingListener\"}",
            NativeControl.WindowOutgoingListener => "{\"NativeControl\":\"WindowOutgoingListener\"}",
            NativeControl.WindowBoundarySpan => "{\"NativeControl\":\"WindowBoundarySpan\"}",
            NativeControl.WindowGuardInterval => "{\"NativeControl\":\"WindowGuardInterval\"}",
            NativeControl.ExpectedEffectCounts => "{\"NativeControl\":\"ExpectedEffectCounts\"}",
            NativeControl.GenerationDrain => "{\"NativeControl\":\"GenerationDrain\"}",
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
    Receipt,
    ReaderParserSetup,
    ReaderProcessIdentity,
    ReaderListenerSchema,
    ReaderBridgeSchema,
    ReaderProjectionArguments,
    ReaderCounterState,
    ReaderParserRead,
    ReaderParserCompletion,
    ReaderStreamCompletion,
    ReaderCancelled,
    ReaderUnknown,
    CanaryMissingHeartbeat,
    CanaryMissingHealth,
    CanaryMissingBoth,
    WindowPendingRequests,
    WindowMissingHeartbeat,
    WindowMissingHealthCanary,
    WindowMissingIncomingListener,
    WindowOutgoingListener,
    WindowBoundarySpan,
    WindowGuardInterval,
    ExpectedEffectCounts,
    GenerationDrain
}
