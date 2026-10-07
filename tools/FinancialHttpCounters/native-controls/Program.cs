using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FinancialHttpCounters;

// Independent real EventPipe codec test; source/owner admission is deliberately
// not bypassed into a financial receipt. It uses one synthetic child only.
if (!OperatingSystem.IsLinux() || args.Length != 2) return 2;
await AdmissionNativeControls.RunAsync();
string dotnet = args[0], host = args[1];
ProcessAdmission.CanonicalPath(dotnet); ProcessAdmission.CanonicalPath(host);
int port;
var reserve = new TcpListener(IPAddress.Loopback, 0);
try { reserve.Start(); port = ((IPEndPoint)reserve.LocalEndpoint).Port; }
finally { reserve.Stop(); }
var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
info.ArgumentList.Add(host); info.ArgumentList.Add(port.ToString());
using var child = Process.Start(info) ?? throw new InvalidDataException("Exact disposable witness child unavailable");
// Logs are private and discarded, never forwarded to output/artifacts.
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
async Task Discard(Stream stream)
{
    byte[] buffer = new byte[4096]; long total = 0; int count;
    while ((count = await stream.ReadAsync(buffer, budget.Token)) > 0)
        CounterState.Require((total += count) <= 1024 * 1024, "Private witness output exceeded budget");
}
Task stdout = Discard(child.StandardOutput.BaseStream), stderr = Discard(child.StandardError.BaseStream);
EventCollector? collector = null;
bool passed = false;
try
{
    Uri origin = new($"http://127.0.0.1:{port}/");
    using var http = new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(2) };
    for (int attempt = 0; ; attempt++)
    {
        CounterState.Require(attempt < 30 && !child.HasExited, "Actual witness host readiness failed");
        try { using var health = await http.GetAsync("/health", budget.Token); if (health.IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        await Task.Delay(100, budget.Token);
    }
    async Task<FilePin> Pin(string path) => new(path, Convert.ToHexStringLower(SHA256.HashData(await ProcessAdmission.BoundedFileAsync(path, 67108864, budget.Token))));
    var processPin = new ProcessPin("Accounting", child.Id, (await ProcessAdmission.KernelAsync(child.Id, budget.Token)).Ticks,
        child.StartTime.ToUniversalTime().Ticks, await Pin(dotnet), await Pin(host), await Pin(Path.ChangeExtension(host, ".runtimeconfig.json")),
        "", new(), "", "", "", []);
    collector = new EventCollector(processPin, origin, origin, budget.Token);
    await collector.StartAsync(budget.Token);
    // Attach is followed by a real request in the observed child, never a
    // supplied counter or callback. Require the decoded hosting schema canary.
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
    using (var trigger = await http.GetAsync("/trigger", budget.Token)) trigger.EnsureSuccessStatusCode();
    await Task.Delay(1000, budget.Token); DateTime completion = DateTime.UtcNow;
    using (var replay = await http.GetAsync("/replay", budget.Token)) replay.EnsureSuccessStatusCode();
    await Task.Delay(1000, budget.Token); DateTime after = DateTime.UtcNow;
    await collector.StopAndDrainAsync(budget.Token);
    var first = collector.State.FinalizeWindow(before, completion, TimeSpan.FromMilliseconds(250));
    var second = collector.State.FinalizeWindow(completion, after, TimeSpan.FromMilliseconds(250));
    CounterState.Require(first["AccountingInvoiceRenderPost"].Started == 1 && first["AccountingFileUploadPost"].Started == 1
        && first["AccountingOtherUploadBoundaryMethod"].Started == 1 && second.Values.All(x => x.Started == 0), "Actual native POST/DELETE/replay decoding differs");
    CounterState.Require(collector.Drained && collector.Lost == 0 && (await ProcessAdmission.KernelAsync(child.Id, budget.Token)).Ticks == processPin.KernelStartTicks
        && child.StartTime.ToUniversalTime().Ticks == processPin.NativeStartUtcTicks && !child.HasExited, "Actual witness generation/drain differs");
    passed = true;
}
finally
{
    try { if (collector is not null) await collector.DisposeAsync(); }
    finally
    {
        // Exact retained disposable child only; no process-name/port-wide kill.
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { NativeEventPipeCodecWitnessPassed = passed, SourceAdmissionAccepted = false,
    RealBusinessSchemaAccepted = false, SmtpNoSendProven = false, SocketNoSendProven = false, GenuineEightHostFinancialAccepted = false }));
return passed ? 0 : 1;
