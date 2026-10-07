using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HostedProcessStartObserver;

namespace FinancialHttpCounters;

public sealed record FilePin(string Path, string Sha256);
public sealed record ParentPin(int Pid, ulong KernelStartTicks, long NativeStartUtcTicks, FilePin Executable, FilePin Script);
public sealed record ProcessPin(string Owner, int Pid, ulong KernelStartTicks, long NativeStartUtcTicks,
    FilePin Executable, FilePin Dll, FilePin RuntimeConfig, string EnvironmentSha256,
    Dictionary<string, string> RequiredEnvironment, string Repository, string SourceSha, string SourceTree,
    FilePin[] RuntimeLibraries);
public sealed record CollectorInput(ParentPin Parent, ProcessPin[] Processes, string DocumentOrigin, string FileOrigin,
    DateTimeOffset ExpiresUtc, string GithubRunId, string GithubAttempt);

public static class ProcessAdmission
{
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Accounting"] = "ad45c78d7546674b5bb7eacff62759460df6ca02",
        ["Document"] = "af0976cbbb0fa9b2da41822f5d67e95b839f8a91",
        ["File"] = "d8be968eb73e7216568248af16a780057098be22",
        ["Notification"] = "55c2c4b68bfd7749c9262929c850032be6df44e0"
    };

    public static Uri Origin(string value)
    {
        CounterState.Require(Uri.TryCreate(value, UriKind.Absolute, out var origin) && origin.Scheme == "https"
            && origin.AbsolutePath == "/" && origin.UserInfo == "" && origin.Query == "" && origin.Fragment == ""
            && origin.AbsoluteUri == value, "Canonical private configured HTTPS origin required");
        return origin!;
    }

    public static async Task<byte[]> BoundedFileAsync(string path, int maximum, CancellationToken token, bool privateOwner = false,
        Action? afterOpen = null) => (await DescriptorBoundFrontFile.ReadAsync(path, maximum, token, afterOpen, privateOwner)).Bytes;

    // Only kernel-owned, typed proc leaves; never accepts an arbitrary caller path.
    private static async Task<byte[]> ProcAsync(int pid, string leaf, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CounterState.Require(pid > 0 && leaf is ("stat" or "environ" or "cmdline" or "maps"), "Typed kernel read required");
        using var stream = new FileStream($"/proc/{pid}/{leaf}", FileMode.Open, FileAccess.Read, FileShare.Read);
        using var result = new MemoryStream(); byte[] buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        { CounterState.Require(result.Length + count <= maximum, "Kernel observation exceeded bound"); await result.WriteAsync(buffer.AsMemory(0, count), token); }
        return result.ToArray();
    }

    public static void CanonicalPath(string path)
    {
        CounterState.Require(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path && !path.Contains('\n'), "Canonical absolute path required");
        for (string? candidate = path; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        { CounterState.Require(new FileInfo(candidate).LinkTarget is null, "Redirected path rejected"); if (Path.GetDirectoryName(candidate) == candidate) break; }
    }

    public static async Task<byte[]> FileAsync(FilePin pin, CancellationToken token)
    {
        CounterState.Require(Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$"), "Lowercase exact SHA256 required");
        var value = await DescriptorBoundFrontFile.ReadAsync(pin.Path, 67108864, token);
        CounterState.Require(value.Sha256 == pin.Sha256, "Actual pinned file bytes differ");
        return value.Bytes;
    }

    public static async Task<(ulong Ticks, int Parent)> KernelAsync(int pid, CancellationToken token)
    {
        string stat = Encoding.ASCII.GetString(await ProcAsync(pid, "stat", 8192, token));
        int left = stat.IndexOf('('), right = stat.LastIndexOf(')');
        CounterState.Require(left > 0 && right > left && int.Parse(stat[..left].Trim()) == pid, "Actual kernel PID differs");
        string[] fields = stat[(right + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CounterState.Require(fields.Length >= 20 && fields[0] is not ("Z" or "X"), "Actual live process required");
        return (ulong.Parse(fields[19]), int.Parse(fields[1]));
    }

    public static async Task<Dictionary<string, string>> EnvironmentAsync(int pid, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in Encoding.UTF8.GetString(await ProcAsync(pid, "environ", 1024 * 1024, token)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        { int split = pair.IndexOf('='); CounterState.Require(split > 0 && result.TryAdd(pair[..split], pair[(split + 1)..]), "Unambiguous actual environment required"); }
        return result;
    }

    public static async Task ObserveParentAsync(CollectorInput input, CancellationToken token)
    {
        var pin = input.Parent;
        CounterState.Require(pin.Pid > 0 && (await KernelAsync(System.Environment.ProcessId, token)).Parent == pin.Pid
            && (await KernelAsync(pin.Pid, token)).Ticks == pin.KernelStartTicks, "Held direct caller generation required");
        using var parent = Process.GetProcessById(pin.Pid);
        CounterState.Require(!parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == pin.NativeStartUtcTicks, "Actual caller native start differs");
        await FileAsync(pin.Executable, token); await FileAsync(pin.Script, token);
        await DescriptorBoundFrontFile.HashKernelExecutableAsync(pin.Pid, pin.Executable.Path, pin.Executable.Sha256, token);
        CounterState.Require(new FileInfo($"/proc/{pin.Pid}/exe").LinkTarget == pin.Executable.Path, "Actual caller executable differs");
        var argv = Encoding.UTF8.GetString(await ProcAsync(pin.Pid, "cmdline", 65536, token)).TrimEnd('\0').Split('\0');
        CounterState.Require(argv.Length >= 2 && argv[0] == pin.Executable.Path
            && (argv[1] == pin.Script.Path || (argv.Length >= 3 && argv[1] == "-B" && argv[2] == pin.Script.Path)), "Actual caller script differs");
        var env = await EnvironmentAsync(pin.Pid, token);
        CounterState.Require(env.GetValueOrDefault("GITHUB_RUN_ID") == input.GithubRunId && env.GetValueOrDefault("GITHUB_RUN_ATTEMPT") == input.GithubAttempt
            && env.GetValueOrDefault("GITHUB_ACTIONS") == "true", "Actual hosted caller context differs");
    }

    public static async Task ObserveAsync(ProcessPin pin, CollectorInput input, Process held, CancellationToken token)
    {
        CounterState.Require(Sources.TryGetValue(pin.Owner, out string? source) && source == pin.SourceSha
            && Regex.IsMatch(pin.SourceTree, "^[0-9a-f]{40}$"), "Selected source pin differs");
        CounterState.Require(pin.Pid > 0 && await KernelAsync(pin.Pid, token) == (pin.KernelStartTicks, input.Parent.Pid), "Actual target caller/generation differs");
        held.Refresh();
        CounterState.Require(!held.HasExited && held.Id == pin.Pid && held.StartTime.ToUniversalTime().Ticks == pin.NativeStartUtcTicks, "Actual held native generation differs");
        await FileAsync(pin.Executable, token); await FileAsync(pin.Dll, token);
        await DescriptorBoundFrontFile.HashKernelExecutableAsync(pin.Pid, pin.Executable.Path, pin.Executable.Sha256, token);
        byte[] configBytes = await FileAsync(pin.RuntimeConfig, token);
        CounterState.Require(configBytes.Length <= 65536, "Runtime config exceeded bound");
        using var runtimeConfig = JsonDocument.Parse(configBytes);
        var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
        CounterState.Require(options.GetProperty("tfm").GetString() == "net10.0", "Selected net10 target required");
        if (options.TryGetProperty("configProperties", out var properties)
            && properties.TryGetProperty("System.Net.Http.EnableActivityPropagation", out var propagation))
            CounterState.Require(propagation.ValueKind == JsonValueKind.True, "Runtimeconfig disables actual HTTP diagnostics");
        CounterState.Require(new FileInfo($"/proc/{pin.Pid}/exe").LinkTarget == pin.Executable.Path, "Actual target executable differs");
        string[] argv = Encoding.UTF8.GetString(await ProcAsync(pin.Pid, "cmdline", 65536, token)).TrimEnd('\0').Split('\0');
        CounterState.Require(argv.SequenceEqual(new[] { pin.Executable.Path, pin.Dll.Path }), "Actual target executable/DLL argv differs");
        byte[] environment = await ProcAsync(pin.Pid, "environ", 1024 * 1024, token);
        CounterState.Require(Regex.IsMatch(pin.EnvironmentSha256, "^[0-9a-f]{64}$")
            && Convert.ToHexStringLower(SHA256.HashData(environment)) == pin.EnvironmentSha256, "Actual private configuration environment differs");
        var env = await EnvironmentAsync(pin.Pid, token);
        foreach (var pair in pin.RequiredEnvironment)
            CounterState.Require(env.GetValueOrDefault(pair.Key) == pair.Value, "Actual consumed environment key differs");
        CounterState.Require(env.GetValueOrDefault("GITHUB_RUN_ID") == input.GithubRunId && env.GetValueOrDefault("GITHUB_RUN_ATTEMPT") == input.GithubAttempt,
            "Actual target hosted context differs");
        if (pin.Owner == "Accounting") CounterState.Require(env.GetValueOrDefault("Services__Document")?.TrimEnd('/') == input.DocumentOrigin.TrimEnd('/')
            && env.GetValueOrDefault("Services__File")?.TrimEnd('/') == input.FileOrigin.TrimEnd('/'), "Actual Accounting consumed endpoints differ");
        CounterState.Require(env.GetValueOrDefault("DOTNET_EnableDiagnostics")?.ToLowerInvariant() is not ("0" or "false")
            && env.GetValueOrDefault("DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION")?.ToLowerInvariant() is not ("0" or "false"), "Diagnostics disabled");
        CounterState.Require(pin.RuntimeLibraries.Length == 2 && pin.RuntimeLibraries.Select(x => Path.GetFileName(x.Path)).Order()
            .SequenceEqual(new[] { "System.Diagnostics.DiagnosticSource.dll", "System.Net.Http.dll" }), "Exact runtime telemetry library inventory required");
        string maps = Encoding.UTF8.GetString(await ProcAsync(pin.Pid, "maps", 4 * 1024 * 1024, token));
        foreach (var library in pin.RuntimeLibraries)
        {
            byte[] libraryBytes = await FileAsync(library, token);
            using var pe = new PEReader(new MemoryStream(libraryBytes, writable: false));
            CounterState.Require(pe.GetMetadataReader().GetAssemblyDefinition().Version.Major == 10
                && maps.Split('\n').Any(x => x.EndsWith(" " + library.Path, StringComparison.Ordinal)), "Pinned net10 telemetry library is not actually mapped");
        }
        CounterState.Require(maps.Split('\n').Any(x => x.EndsWith(" " + pin.Dll.Path, StringComparison.Ordinal)), "Actual selected entrypoint DLL is not mapped");
        CanonicalPath(pin.Repository);
        CounterState.Require(pin.Dll.Path.StartsWith(pin.Repository + '/', StringComparison.Ordinal), "Build must lie inside selected source root");
    }

    public static async Task VerifySourceAsync(ProcessPin pin, CancellationToken token)
    {
        async Task<string> Git(params string[] args)
        {
            var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-C"); info.ArgumentList.Add(pin.Repository);
            foreach (string argument in args) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidDataException("Source read failed");
            async Task<string> BoundedText(StreamReader reader)
            {
                var text = new StringBuilder(); char[] buffer = new char[4096]; int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
                { CounterState.Require(text.Length + length <= 65536, "Source observation output exceeded bound"); text.Append(buffer, 0, length); }
                return text.ToString();
            }
            var output = BoundedText(process.StandardOutput); var error = BoundedText(process.StandardError);
            try { await process.WaitForExitAsync(token); }
            catch { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); throw; }
            string text = await output; string discarded = await error;
            CounterState.Require(process.ExitCode == 0 && text.Length <= 65536 && discarded.Length <= 65536, "Bounded actual git observation required");
            return text.TrimEnd('\r', '\n');
        }
        CounterState.Require(await Git("rev-parse", "HEAD") == pin.SourceSha && await Git("rev-parse", "HEAD^{tree}") == pin.SourceTree
            && await Git("status", "--porcelain", "--untracked-files=all") == "", "Actual clean selected source differs");
    }
}
