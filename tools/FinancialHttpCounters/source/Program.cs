using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialHttpCounters;

var json = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
var collectors = new List<EventCollector>();
DescriptorBoundOutput? output = null;
using var admissionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
object? receipt = null;
int result = 1;
try
{
    if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Hosted Linux required");
    CounterState.Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && args.Length == 2,
        "One private input and one fresh public output on hosted Linux required");
    byte[] inputBytes = await ProcessAdmission.BoundedFileAsync(args[0], 1024 * 1024, admissionDeadline.Token, privateOwner: true);
    var input = JsonSerializer.Deserialize<CollectorInput>(inputBytes, json) ?? throw new InvalidDataException("Input unavailable");
    CounterState.Require(input.Processes.Length == 4 && input.Processes.Select(x => x.Owner).Order().SequenceEqual(ProcessAdmission.Sources.Keys.Order())
        && input.Processes.Select(x => x.Pid).Distinct().Count() == 4, "Exact four independent selected targets required");
    CounterState.Require(input.GithubRunId == Environment.GetEnvironmentVariable("GITHUB_RUN_ID") && input.GithubAttempt == Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT")
        && input.ExpiresUtc.Offset == TimeSpan.Zero && input.ExpiresUtc > DateTimeOffset.UtcNow && input.ExpiresUtc <= DateTimeOffset.UtcNow.AddMinutes(10),
        "Exact finite hosted context required");
    admissionDeadline.Token.ThrowIfCancellationRequested();
    TimeSpan budget = input.ExpiresUtc - DateTimeOffset.UtcNow;
    using var lifetime = new CancellationTokenSource(budget); // Timer remains bounded when wall clock changes.
    var token = lifetime.Token;
    var watch = Stopwatch.StartNew(); DateTime started = DateTime.UtcNow;
    var document = ProcessAdmission.Origin(input.DocumentOrigin); var file = ProcessAdmission.Origin(input.FileOrigin);
    output = DescriptorBoundOutput.Admit(args[1], token);
    async Task ObserveAsync()
    {
        token.ThrowIfCancellationRequested();
        CounterState.Require(DateTimeOffset.UtcNow < input.ExpiresUtc && (DateTime.UtcNow - started - watch.Elapsed).Duration() <= TimeSpan.FromMilliseconds(100),
            "Lease expired or clock relationship changed");
        await ProcessAdmission.ObserveParentAsync(input, token);
        foreach (var collector in collectors) { collector.CheckReader(); await ProcessAdmission.ObserveAsync(collector.Pin, input, collector.Held, token); }
    }
    await ProcessAdmission.ObserveParentAsync(input, token);
    foreach (var pin in input.Processes)
    {
        var collector = new EventCollector(pin, document, file, token); collectors.Add(collector);
        await ProcessAdmission.ObserveAsync(pin, input, collector.Held, token);
        using var sourceBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        sourceBudget.CancelAfter(TimeSpan.FromSeconds(5));
        await ProcessAdmission.VerifySourceAsync(pin, sourceBudget.Token);
        await collector.StartAsync(token);
        await ProcessAdmission.ObserveAsync(pin, input, collector.Held, token);
    }
    Console.WriteLine("{\"SessionsStarted\":true,\"HttpObservationsComplete\":false}");
    DateTime[] boundaries = new DateTime[3];
    string[] commands = ["baseline", "completion", "replay"];
    for (int phase = 0; phase < commands.Length; phase++)
    {
        string? command = await Console.In.ReadLineAsync(token);
        CounterState.Require(command == commands[phase], "Exact ordered phase protocol required");
        // Parent must cease scenario activity during this held collection guard.
        // Final drain retrospectively rejects effects crossing/near any boundary.
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        await ObserveAsync(); boundaries[phase] = DateTime.UtcNow;
        foreach (var collector in collectors)
            lock (collector.Gate) CounterState.Require(collector.State.Heartbeats > 0 && collector.State.HealthCanaries > 0,
                "Actual runtime/hosting schema canary not observed");
        Console.WriteLine(JsonSerializer.Serialize(new { Phase = commands[phase], BoundaryHeld = true, HttpObservationsComplete = false }));
    }
    CounterState.Require(await Console.In.ReadLineAsync(token) == "stop", "Explicit ordered stop required");
    await Task.Delay(TimeSpan.FromSeconds(1), token); await ObserveAsync();
    using var stopBudget = CancellationTokenSource.CreateLinkedTokenSource(token); stopBudget.CancelAfter(TimeSpan.FromSeconds(10));
    foreach (var collector in collectors) await collector.StopAndDrainAsync(stopBudget.Token);
    await ObserveAsync();
    var observations = new Dictionary<string, object>();
    foreach (var collector in collectors)
    {
        var guard = TimeSpan.FromMilliseconds(250);
        object completion = collector.State.FinalizeWindow(boundaries[0], boundaries[1], guard);
        object replay = collector.State.FinalizeWindow(boundaries[1], boundaries[2], guard);
        object baseline = collector.State.FinalizeWindow(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), boundaries[0], guard);
        object tail = collector.State.FinalizeWindow(boundaries[2], DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), guard);
        observations.Add(collector.Pin.Owner, new
        {
            collector.Pin.Pid,
            collector.Pin.KernelStartTicks,
            collector.Pin.NativeStartUtcTicks,
            collector.Pin.SourceSha,
            collector.Pin.SourceTree,
            ExecutableSha256 = collector.Pin.Executable.Sha256,
            DllSha256 = collector.Pin.Dll.Sha256,
            BeforeBaseline = baseline,
            Completion = completion,
            Replay = replay,
            AfterReplayThroughDrain = tail,
            EventsLost = collector.Lost,
            StreamEofObserved = collector.Drained
        });
        using var sourceBudget = CancellationTokenSource.CreateLinkedTokenSource(token); sourceBudget.CancelAfter(TimeSpan.FromSeconds(5));
        await ProcessAdmission.VerifySourceAsync(collector.Pin, sourceBudget.Token);
    }
    receipt = new
    {
        HttpObservationsComplete = true,
        GenuineEightHostFinancialAccepted = false,
        SmtpNoSendProven = false,
        SocketNoSendProven = false,
        IncomingMethodSchemaCanariesObserved = true,
        Scope = "HttpClient/ASP.NET observations from attach through drained stop; no pre-attach coverage",
        PrivateInputSha256 = Convert.ToHexStringLower(SHA256.HashData(inputBytes)),
        Observations = observations
    };
    result = 0;
}
catch (Exception error)
{
    receipt = new
    {
        HttpObservationsComplete = false,
        GenuineEightHostFinancialAccepted = false,
        SmtpNoSendProven = false,
        SocketNoSendProven = false,
        ErrorType = error.GetType().Name
    };
}
finally
{
    bool cleanup = true;
    foreach (var collector in collectors)
    {
        try { await collector.DisposeAsync(); }
        catch (Exception) { cleanup = false; result = 1; }
    }
    if (output is not null)
    {
        // Keep cleanup independent of observation validity. No speculative success.
        var wrapped = new { SessionCleanupVerified = cleanup, Observation = receipt };
        try { await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(wrapped, json)); }
        finally { output.Dispose(); }
    }
}
return result;
