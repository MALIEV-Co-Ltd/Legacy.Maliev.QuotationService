using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using InvoiceCompletionProducerAcceptance;
using QualificationOutcomeWireSource;

namespace HostedProcessStartObserver;

public sealed record ParentExpectation(int Pid, long KernelStartTicks, string Executable, string ExecutableSha256,
    string Script, string ScriptSha256);
public sealed record HostStartRequest(string Owner, string RunId, DateTimeOffset ExpiresUtc, string GithubRunId,
    string GithubAttempt, ParentExpectation Parent, int Pid, long KernelStartTicks, string DotnetExecutable,
    string DotnetSha256, string ExecutableDll, string ExecutableSha256, string ExpectedEnvironmentSha256, string FrontProfilePath = "");
public sealed record HostStartObservation(string Owner, int Pid, DateTimeOffset StartedUtc,
    long KernelStartTicks, string Executable, string ExecutableSha256, string ExecutableDll, string DllSha256);

/// <summary>Observes only direct children of the exact admitted parent; starts/stops no resource.</summary>
public static class ActualHostStartObservation
{
    private static readonly HashSet<string> Owners = ["Auth", "Accounting", "Quotation", "Order", "IAM", "Document", "File", "Notification", "Front"];

    public static async Task<HostStartObservation> ObserveAsync(HostStartRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidDataException("Actual hosted Linux parent required.");
        ValidateRequest(request, DateTimeOffset.UtcNow);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remaining = request.ExpiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new InvalidDataException("Owner lease expired before observation.");
        lifetime.CancelAfter(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5));
        token = lifetime.Token;
        await ObserveParent(request, token);
        var before = await ObserveTarget(request, token);
        using var process = Process.GetProcessById(request.Pid);
        process.Refresh();
        var started = ChildStartObservation.Capture(() => process.StartTime.ToUniversalTime(), () => process.HasExited)
            ?? throw new InvalidDataException("Actual target start time unavailable.");
        var after = await ObserveTarget(request, token);
        process.Refresh();
        var retained = ChildStartObservation.Capture(() => process.StartTime.ToUniversalTime(), () => process.HasExited)
            ?? throw new InvalidDataException("Actual target start time unavailable.");
        if (process.HasExited) throw new InvalidDataException("Target exited during start observation.");
        ValidateReobservations(request, before, after, started, retained, DateTimeOffset.UtcNow);
        await ObserveParent(request, token);
        token.ThrowIfCancellationRequested();
        ValidateRequest(request, DateTimeOffset.UtcNow);
        ValidateReobservations(request, before, after, started, retained, DateTimeOffset.UtcNow);
        // Exact .NET value; no observation timestamp or /proc btime approximation.
        return new(request.Owner, request.Pid, new DateTimeOffset(started), request.KernelStartTicks,
            request.DotnetExecutable, request.DotnetSha256, request.ExecutableDll, request.ExecutableSha256);
    }

    public static void ValidateRequest(HostStartRequest request, DateTimeOffset now)
    {
        if (!Owners.Contains(request.Owner) || request.Pid <= 0 || request.KernelStartTicks <= 0
            || request.Parent.Pid <= 0 || request.Parent.KernelStartTicks <= 0 || request.Parent.Pid == request.Pid
            || request.ExpiresUtc.Offset != TimeSpan.Zero || request.ExpiresUtc <= now || request.ExpiresUtc > now.AddMinutes(30)
            || !request.RunId.StartsWith("c821-", StringComparison.Ordinal)
            || !Guid.TryParseExact(request.RunId[5..], "D", out var run) || run == Guid.Empty
            || request.RunId != "c821-" + run.ToString("D")
            || !Regex.IsMatch(request.GithubRunId, "^[1-9][0-9]{0,19}$")
            || !Regex.IsMatch(request.GithubAttempt, "^[1-9][0-9]{0,8}$"))
            throw new InvalidDataException("Exact known parent/host finite run identity required.");
        foreach (var path in new[] { request.DotnetExecutable, request.ExecutableDll, request.Parent.Executable, request.Parent.Script })
            RequirePath(path);
        foreach (var hash in new[] { request.DotnetSha256, request.ExecutableSha256,
                     request.Parent.ExecutableSha256, request.Parent.ScriptSha256, request.ExpectedEnvironmentSha256 })
            if (!Regex.IsMatch(hash, "^[0-9A-F]{64}$")) throw new InvalidDataException("Exact uppercase SHA-256 required.");
        if (request.Owner == "Front")
        {
            RequirePath(request.FrontProfilePath);
            var owned = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? ""),
                "TestResults", "C821ProducerProfiles") + Path.DirectorySeparatorChar;
            if (!request.FrontProfilePath.StartsWith(owned, StringComparison.Ordinal))
                throw new InvalidDataException("Canonical owned front profile required.");
        }
        else if (request.FrontProfilePath != "")
            throw new InvalidDataException("Normal host cannot consume a front profile.");
        var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "");
        if (request.Parent.Script != Path.Combine(workspace, "tools", "InvoiceCompletionProducerAcceptance", "companion", "run_eight_host_financial_acceptance.py"))
            throw new InvalidDataException("Canonical actual parent script required.");
    }

    public static void ValidateReobservations(HostStartRequest request, KernelIdentity before, KernelIdentity after,
        DateTime started, DateTime retained, DateTimeOffset now)
    {
        if (before.Pid != request.Pid || after.Pid != request.Pid || before.Parent != request.Parent.Pid
            || after.Parent != request.Parent.Pid || before.Start != request.KernelStartTicks || after.Start != before.Start
            || started.Kind != DateTimeKind.Utc || retained.Kind != DateTimeKind.Utc || started != retained
            || started > now.UtcDateTime || started >= request.ExpiresUtc.UtcDateTime || request.ExpiresUtc <= now)
            throw new InvalidDataException("Actual target generation/start/lease changed.");
    }

    private static async Task ObserveParent(HostStartRequest request, CancellationToken token)
    {
        ValidateRequest(request, DateTimeOffset.UtcNow);
        var self = ParseStat(await ReadBounded("/proc/self/stat", 4096, token), Environment.ProcessId);
        if (self.Parent != request.Parent.Pid) throw new InvalidDataException("Observer is not the held parent's direct child.");
        ValidateRunEnvironment(DecodeEnvironment(await ReadBounded("/proc/self/environ", 262144, token)), request);
        var prefix = $"/proc/{request.Parent.Pid}";
        var parent = ParseStat(await ReadBounded(prefix + "/stat", 4096, token), request.Parent.Pid);
        if (parent.Start != request.Parent.KernelStartTicks || Link(prefix + "/exe") != request.Parent.Executable)
            throw new InvalidDataException("Actual parent generation/executable differs.");
        await DescriptorBoundFrontFile.HashKernelExecutableAsync(request.Parent.Pid, request.Parent.Executable, request.Parent.ExecutableSha256.ToLowerInvariant(), token);
        await RequireHash(request.Parent.Script, request.Parent.ScriptSha256, token);
        var command = DecodeCommand(await ReadBounded(prefix + "/cmdline", 65536, token));
        if (command.Length is < 2 or > 8 || command[0] != request.Parent.Executable || command[1] != request.Parent.Script)
            throw new InvalidDataException("Actual immediate parent script differs.");
        ValidateRunEnvironment(DecodeEnvironment(await ReadBounded(prefix + "/environ", 262144, token)), request);
        var final = ParseStat(await ReadBounded(prefix + "/stat", 4096, token), request.Parent.Pid);
        if (final.Start != parent.Start) throw new InvalidDataException("Parent generation changed.");
    }

    private static async Task<KernelIdentity> ObserveTarget(HostStartRequest request, CancellationToken token)
    {
        var prefix = $"/proc/{request.Pid}";
        var target = ParseStat(await ReadBounded(prefix + "/stat", 4096, token), request.Pid);
        if (target.Parent != request.Parent.Pid || target.Start != request.KernelStartTicks
            || Link(prefix + "/exe") != request.DotnetExecutable)
            throw new InvalidDataException("Target is not the exact parent's normal host.");
        await DescriptorBoundFrontFile.HashKernelExecutableAsync(request.Pid, request.DotnetExecutable, request.DotnetSha256.ToLowerInvariant(), token);
        await RequireHash(request.ExecutableDll, request.ExecutableSha256, token);
        var command = DecodeCommand(await ReadBounded(prefix + "/cmdline", 65536, token));
        ValidateCommand(request, command);
        var environment = DecodeEnvironment(await ReadBounded(prefix + "/environ", 262144, token));
        ValidateRunEnvironment(environment, request);
        var expected = request.Owner == "File" ? "HostedFinancialCompletionAcceptance" : "Production";
        CompanionLauncherAdmission.RequireExact(environment, "ASPNETCORE_ENVIRONMENT", expected);
        if (environment.Keys.Any(key => key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)))
            CompanionLauncherAdmission.RequireExact(environment, "DOTNET_ENVIRONMENT", expected);
        if (request.Owner == "Notification") CompanionLauncherAdmission.RequireExact(environment, "Notifications__DeliveryIntentsEnabled", "false");
        if (EnvironmentDigest(environment) != request.ExpectedEnvironmentSha256)
            throw new InvalidDataException("Actual complete consumed target environment differs.");
        var final = ParseStat(await ReadBounded(prefix + "/stat", 4096, token), request.Pid);
        if (final != target) throw new InvalidDataException("Target kernel identity changed during observation.");
        return final;
    }

    public static void ValidateCommand(HostStartRequest request, string[] command)
    {
        if (request.Owner == "Front")
        {
            if (request.FrontProfilePath == "" || !command.SequenceEqual(
                    [request.DotnetExecutable, request.ExecutableDll, request.FrontProfilePath], StringComparer.Ordinal))
                throw new InvalidDataException("Exact front executable and startup profile required.");
        }
        else
        {
            if (request.FrontProfilePath != "") throw new InvalidDataException("Normal host profile argument denied.");
            ProducerDatabaseBindings.ValidateLaunchArguments(command, request.ExecutableDll, request.DotnetExecutable);
            if (command[0] != request.DotnetExecutable) throw new InvalidDataException("Exact dotnet command required.");
        }
    }

    private static void ValidateRunEnvironment(IReadOnlyDictionary<string, string> environment, HostStartRequest request)
    {
        foreach (var (name, expected) in new[] { ("GITHUB_ACTIONS", "true"), ("GITHUB_RUN_ID", request.GithubRunId),
            ("GITHUB_RUN_ATTEMPT", request.GithubAttempt), ("C821_FIXTURE_RUN_ID", request.RunId),
            ("C821_FIXTURE_EXPIRES_UTC", request.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture)) })
        {
            var value = CompanionLauncherAdmission.RequireExact(environment, name);
            if (name == "C821_FIXTURE_EXPIRES_UTC")
            {
                if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry)
                    || expiry != request.ExpiresUtc) throw new InvalidDataException("Actual run lease differs.");
            }
            else if (value != expected) throw new InvalidDataException("Actual run environment differs.");
        }
    }

    public static string EnvironmentDigest(IReadOnlyDictionary<string, string> environment)
    {
        // Byte-order sort and NUL framing match the parent protocol without emitting any value.
        var values = environment.Select(pair => Encoding.UTF8.GetBytes(pair.Key + "=" + pair.Value + "\0"))
            .OrderBy(value => Convert.ToHexString(value), StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values) hash.AppendData(value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public sealed record KernelIdentity(int Pid, int Parent, long Start);

    public static KernelIdentity ParseStat(byte[] data, int expectedPid)
    {
        if (data.Length is < 1 or > 4096) throw new InvalidDataException("Bounded kernel stat required.");
        var text = Encoding.ASCII.GetString(data);
        int open = text.IndexOf('('), close = text.LastIndexOf(')');
        if (open < 1 || close <= open || !int.TryParse(text[..open].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid != expectedPid) throw new InvalidDataException("Exact kernel PID required.");
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0] is "Z" or "X" or "x"
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0)
            throw new InvalidDataException("Live kernel generation required.");
        return new(pid, parent, start);
    }

    public static Dictionary<string, string> DecodeEnvironment(byte[] bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in new UTF8Encoding(false, true).GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=');
            if (split <= 0 || !result.TryAdd(entry[..split], entry[(split + 1)..]))
                throw new InvalidDataException("Unique valid environment keys required.");
        }
        return result;
    }

    private static string[] DecodeCommand(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static string Link(string path) => new FileInfo(path).LinkTarget ?? throw new InvalidDataException("Actual proc link unavailable.");
    private static void RequirePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw new InvalidDataException("Canonical absolute path required.");
        FileSystemInfo? current = new FileInfo(path);
        while (current is not null)
        {
            if (current.LinkTarget is not null) throw new InvalidDataException("Redirected path rejected.");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    private static async Task RequireHash(string path, string expected, CancellationToken token)
    {
        RequirePath(path);
        await DescriptorBoundFrontFile.HashAsync(path, expected.ToLowerInvariant(), token);
    }

    public static async Task<byte[]> ReadBounded(string path, int maximum, CancellationToken token)
    {
        // Only these kernel-owned zero-length metadata files retain their typed proc reader.
        if (!Regex.IsMatch(path, "^/proc/(self|[1-9][0-9]*)/(stat|environ|cmdline)$"))
            return (await DescriptorBoundFrontFile.ReadAsync(path, maximum, token, requirePrivateOwner: true)).Bytes;
        token.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Read exceeded bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
