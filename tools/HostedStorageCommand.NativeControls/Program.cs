using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceCompletionProducerAcceptance.Companion;

if (args.Length == 2 && args[0] == "fixture")
{
    using var fixtureBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    switch (args[1])
    {
        case "natural":
            await Console.OpenStandardOutput().WriteAsync("owned-read"u8.ToArray(), fixtureBudget.Token);
            return 0;
        case "nonzero":
            return 7;
        case "cancel":
            await Task.Delay(TimeSpan.FromSeconds(12));
            return 0;
        case "stdout-cap":
            await Console.OpenStandardOutput().WriteAsync(new byte[8192], fixtureBudget.Token);
            await Task.Delay(TimeSpan.FromSeconds(12));
            return 0;
        case "stderr-cap":
            await Console.OpenStandardError().WriteAsync(new byte[32768], fixtureBudget.Token);
            await Task.Delay(TimeSpan.FromSeconds(12));
            return 0;
        default:
            return 64;
    }
}

string[] cases = ["natural", "nonzero-next-read", "cancel", "stdout-cap", "stderr-cap"];
string stage = "admission";
string selected = args.Length == 1 && cases.Contains(args[0], StringComparer.Ordinal) ? args[0] : "Unknown";
var observations = new List<BoundedOwnedCommand.Observation>();
bool passed = false;
string category = "None";
string rawHead = Environment.GetEnvironmentVariable("REVIEWED_SOURCE_HEAD") ?? string.Empty;
string rawRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? string.Empty;
string rawAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? string.Empty;
bool validHead = rawHead.Length == 40
    && Regex.IsMatch(rawHead, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
bool validRun = rawRun.Length is > 0 and <= 19
    && long.TryParse(rawRun, NumberStyles.None, CultureInfo.InvariantCulture, out long runNumber)
    && runNumber > 0 && rawRun == runNumber.ToString(CultureInfo.InvariantCulture);
bool validAttempt = rawAttempt.Length is > 0 and <= 10
    && int.TryParse(rawAttempt, NumberStyles.None, CultureInfo.InvariantCulture, out int attemptNumber)
    && attemptNumber > 0 && rawAttempt == attemptNumber.ToString(CultureInfo.InvariantCulture);
string head = validHead ? rawHead : string.Empty;
string run = validRun ? rawRun : string.Empty;
string attempt = validAttempt ? rawAttempt : string.Empty;
try
{
    try
    {
        Require(OperatingSystem.IsLinux() && selected != "Unknown"
            && validHead && validRun && validAttempt
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted");
        string executable = Environment.ProcessPath ?? throw new InvalidDataException();
        string assembly = Assembly.GetExecutingAssembly().Location;
        Require(Path.GetFileName(executable) == "dotnet" && Path.IsPathFullyQualified(assembly));
        stage = "actual-command";
        if (selected == "nonzero-next-read")
        {
            await Refused(executable, assembly, "nonzero", 64, CancellationToken.None, typeof(InvalidDataException));
            var first = Snapshot();
            Require(first.ExitCode == 7 && !first.CleanupFailureRecorded && !first.AdmissionSticky);
            observations.Add(first);
            byte[] output = await Run(executable, assembly, "natural", 64, CancellationToken.None);
            Require(output.AsSpan().SequenceEqual("owned-read"u8));
            var second = Snapshot();
            Require(!ReferenceEquals(first, second) && second.ExitCode == 0 && !second.ActualSignalInvoked);
            observations.Add(second);
        }
        else if (selected == "natural")
        {
            byte[] output = await Run(executable, assembly, selected, 64, CancellationToken.None);
            Require(output.AsSpan().SequenceEqual("owned-read"u8));
            var observed = Snapshot();
            Require(observed.ExitCode == 0 && !observed.ActualSignalInvoked);
            observations.Add(observed);
        }
        else
        {
            using var cancellation = new CancellationTokenSource();
            if (selected == "cancel") cancellation.CancelAfter(TimeSpan.FromSeconds(3));
            await Refused(executable, assembly, selected, 64, cancellation.Token,
                selected == "cancel" ? typeof(OperationCanceledException) : typeof(InvalidDataException));
            var observed = BoundedOwnedCommand.ObserveLast() ?? throw new InvalidDataException();
            Require(observed.KernelGenerationBound && observed.PidfdAllocated
                && observed.ActualSignalInvoked && observed.ActualSignalSucceeded);
            if (selected == "cancel") Require(cancellation.IsCancellationRequested);
            if (selected == "stdout-cap") Require(observed.StdoutBytesRead > 64);
            if (selected == "stderr-cap") Require(observed.StderrBytesRead > 16384);
            observations.Add(observed);
            if (!observed.PhysicalReleased)
            {
                Require(observed.RetainedOwner && observed.CleanupFailureRecorded
                    && observed.AdmissionSticky && observed.CleanupAttempts == 1);
                Require(await BoundedOwnedCommand.RetryCleanupAsync(CancellationToken.None));
                var recovered = PhysicalSnapshot();
                Require(recovered.CleanupFailureRecorded && recovered.AdmissionSticky
                    && recovered.CleanupAttempts == 2 && !ReferenceEquals(observed, recovered));
                observations.Add(recovered);
                // Sticky failure must refuse replacement birth; no new observation means
                // this rejection did not manufacture a replacement command lease.
                await Refused(executable, assembly, "natural", 64, CancellationToken.None,
                    typeof(InvalidDataException));
                Require(ReferenceEquals(recovered, BoundedOwnedCommand.ObserveLast()));
            }
            else
            {
                _ = PhysicalSnapshot();
            }
        }
        stage = "completed";
        passed = true;
    }
    catch (Exception error)
    {
        category = error switch
        {
            OperationCanceledException => "Cancellation",
            TimeoutException => "Timeout",
            InvalidDataException => "Refused",
            _ => "Other",
        };
        var last = BoundedOwnedCommand.ObserveLast();
        if (last is not null && !observations.Contains(last)) observations.Add(last);
    }

    // Each hosted invocation is a separate process. Failed owners stay rooted in the
    // actual runner static lease until this parent exits; no success is inferred then.
    bool retainedAtReceipt = BoundedOwnedCommand.HasRetainedOwner;
    if (retainedAtReceipt)
    {
        passed = false;
        stage = "quarantine";
        category = "CleanupUnsettled";
    }
    var receipt = new
    {
        SchemaVersion = 1,
        SourceHead = head,
        RunId = run,
        Attempt = attempt,
        Case = selected,
        Passed = passed,
        Stage = stage,
        Category = category,
        Observations = observations,
        ActualRegistryRetainedOwner = retainedAtReceipt,
        OuterDeadlineOrParentDeathCleanupAccepted = false,
        ObservedLoadedRuntime = Environment.Version.ToString(),
        ActualBusinessGraphAccepted = false,
        KernelResourceCapsObserved = false,
        AllFdCensusAccepted = false,
        ParentDeathCleanupAccepted = false,
        InjectedSyscallFaultCasesQualified = false,
        InheritedWriterOrUncertainCloseCasesQualified = false,
    };
    try
    {
        Directory.CreateDirectory("TestResults/StorageCommandNative");
        await File.WriteAllTextAsync($"TestResults/StorageCommandNative/{selected}.json",
            JsonSerializer.Serialize(receipt));
    }
    catch (Exception)
    {
        LogFixed("STORAGE_COMMAND_RECEIPT_REFUSED");
        return 1;
    }
    LogFixed(passed ? "STORAGE_COMMAND_CONTROL_COMPLETED" : "STORAGE_COMMAND_CONTROL_FAILED");
    return passed ? 0 : 1;
}
finally
{
    // Every operation, observation, receipt and logging path crosses this fence.
    await RetainFailedOwnerAsync();
}

static Task<byte[]> Run(string executable, string assembly, string mode, int cap, CancellationToken token) =>
    BoundedOwnedCommand.RunAsync(executable, [assembly, "fixture", mode], cap, token);

static async Task Refused(string executable, string assembly, string mode, int cap,
    CancellationToken token, Type expected)
{
    Exception? failure = null;
    try { _ = await Run(executable, assembly, mode, cap, token); }
    catch (Exception error) { failure = error; }
    Require(failure is not null && expected.IsAssignableFrom(failure.GetType()));
}

static BoundedOwnedCommand.Observation Snapshot()
{
    var observed = PhysicalSnapshot();
    Require(!observed.CleanupFailureRecorded && !observed.AdmissionSticky
        && observed.CleanupAttempts == 1);
    return observed;
}

static BoundedOwnedCommand.Observation PhysicalSnapshot()
{
    var observed = BoundedOwnedCommand.ObserveLast() ?? throw new InvalidDataException();
    Require(observed.BirthAttempted && observed.OriginalExited && observed.ExitCode.HasValue
        && observed.StdoutEof && observed.StderrEof && observed.OriginalTasksTerminal
        && observed.StdoutClosed && observed.StderrClosed && observed.ProcessClosed
        && observed.MetadataClosed && observed.CleanupBudgetsClosed && observed.PhysicalReleased
        && (!observed.PidfdAllocated || observed.PidfdClosed)
        && !observed.RetainedOwner);
    return observed;
}

static void Require(bool condition)
{
    if (!condition) throw new InvalidDataException("Storage command control refused.");
}

static async Task RetainFailedOwnerAsync()
{
    if (!BoundedOwnedCommand.HasRetainedOwner) return;
    LogFixed("STORAGE_COMMAND_OWNER_QUARANTINED");
    // The outer hosted deadline bounds this failed invocation. Parent termination
    // is not settlement or an ownership handoff; no third cleanup is attempted.
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

static void LogFixed(string marker)
{
    try { Console.WriteLine(marker); }
    catch (Exception) { /* Logging cannot release a retained original owner. */ }
}
