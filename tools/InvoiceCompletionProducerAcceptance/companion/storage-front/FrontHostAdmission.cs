using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Public-only startup identity; private signer keys and JWTs never appear in this profile.</summary>
public sealed record FrontHostProfile(string RunId, DateTimeOffset ExpiresUtc, Uri FrontOrigin,
    StorageBackendLease Backend, string Repository, string SourceSha, string ExecutableDll, string ExecutableSha256,
    int ParentPid, long ParentKernelStartTicks, string ParentExecutablePath, string ParentExecutableSha256,
    string ParentScriptPath, string ParentScriptSha256, string BootstrapPipeHandle, string BootstrapPipeInode);

/// <summary>Read-only finite startup and owner-pipe checks; declarations never establish process ownership alone.</summary>
public static class FrontHostAdmission
{
    /// <summary>Reads the bounded run-owned profile and rejects missing or duplicated properties.</summary>
    public static async Task<FrontHostProfile> ReadAsync(string suppliedPath, CancellationToken token)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "");
        var owned = Path.Combine(root, "TestResults", "C821ProducerProfiles") + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(suppliedPath);
        var file = new FileInfo(path);
        if (!path.StartsWith(owned, StringComparison.Ordinal) || !NonredirectedPath(path)
            || !file.Exists || file.Length is < 1 or > 32768)
            throw new InvalidDataException("Exact owned nonredirected profile path required.");
        byte[] bytes = await ReadBoundedAsync(path, 32768, token);
        var profile = Parse(bytes);
        Validate(profile, DateTimeOffset.UtcNow);
        return profile;
    }

    internal static FrontHostProfile Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 32768) throw new InvalidDataException("Bounded owner profile required.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
        UniqueObjects(document.RootElement);
        var profile = document.Deserialize<FrontHostProfile>(new JsonSerializerOptions
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        }) ?? throw new InvalidDataException();
        return profile;
    }

    /// <summary>Requires a current exact hosted profile before resource observation or listener startup.</summary>
    public static void Validate(FrontHostProfile profile, DateTimeOffset now)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || profile.ExpiresUtc.Offset != TimeSpan.Zero || profile.ExpiresUtc <= now || profile.ExpiresUtc > now.AddMinutes(30)
            || profile.Backend.ExpiresUtc != profile.ExpiresUtc || profile.Backend.RunId != profile.RunId
            || !Guid.TryParseExact(profile.RunId.StartsWith("c821-", StringComparison.Ordinal) ? profile.RunId[5..] : "", "D", out var run)
            || run == Guid.Empty || profile.RunId != "c821-" + run.ToString("D")
            || !profile.FrontOrigin.IsAbsoluteUri || profile.FrontOrigin.Scheme != "http"
            || profile.FrontOrigin.Host is not ("127.0.0.1" or "[::1]") || profile.FrontOrigin.Port is < 1024 or > 65535
            || profile.FrontOrigin.AbsolutePath != "/" || profile.FrontOrigin.Query != "" || profile.FrontOrigin.Fragment != "" || profile.FrontOrigin.UserInfo != ""
            || profile.Repository != Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "")
            || !Regex.IsMatch(profile.SourceSha, "^[0-9a-f]{40}$") || profile.SourceSha != Environment.GetEnvironmentVariable("GITHUB_SHA")
            || profile.ParentPid <= 0 || profile.ParentKernelStartTicks <= 0
            || !Regex.IsMatch(profile.BootstrapPipeHandle, "^[1-9][0-9]{0,8}$") || int.Parse(profile.BootstrapPipeHandle, CultureInfo.InvariantCulture) < 3
            || !Regex.IsMatch(profile.BootstrapPipeInode, "^[1-9][0-9]{0,19}$")
            || !ExactFile(profile.ExecutableDll, profile.Repository, profile.ExecutableSha256)
            || !ExactFile(profile.ParentScriptPath, profile.Repository, profile.ParentScriptSha256)
            || !Path.IsPathFullyQualified(profile.ParentExecutablePath) || !HashShape(profile.ParentExecutableSha256))
            throw new InvalidDataException("Current exact hosted front startup identity required.");
        new ObservedStorageBackend(profile.Backend).Validate(now);
    }

    /// <summary>Re-observes actual source, executable, immediate parent generation and both ends of the inherited pipe.</summary>
    public static async Task ObserveOwnerAsync(FrontHostProfile profile, CancellationToken token)
    {
        Validate(profile, DateTimeOffset.UtcNow);
        string head = Encoding.ASCII.GetString(await BoundedOwnedCommand.RunAsync("git",
            ["-C", profile.Repository, "rev-parse", "--verify", "HEAD"], 4096, token)).Trim();
        byte[] dirty = await BoundedOwnedCommand.RunAsync("git", ["-C", profile.Repository, "status", "--porcelain", "--untracked-files=all"], 65536, token);
        if (head != profile.SourceSha || dirty.Length != 0
            || await FileHashAsync(profile.ExecutableDll, token) != profile.ExecutableSha256
            || await FileHashAsync(profile.ParentScriptPath, token) != profile.ParentScriptSha256
            || profile.ParentScriptPath != Path.Combine(profile.Repository, "tools", "InvoiceCompletionProducerAcceptance", "companion", "run_eight_host_financial_acceptance.py"))
            throw new InvalidDataException("Actual admitted source or executable differs.");
        var self = ProcessStat(Encoding.ASCII.GetString(await ReadBoundedAsync("/proc/self/stat", 65536, token)));
        var parent = ProcessStat(Encoding.ASCII.GetString(await ReadBoundedAsync($"/proc/{profile.ParentPid}/stat", 65536, token)));
        if (self.Parent != profile.ParentPid || parent.Start != profile.ParentKernelStartTicks)
            throw new InvalidDataException("Actual owner parent differs.");
        var selfCommand = Encoding.UTF8.GetString(await ReadBoundedAsync("/proc/self/cmdline", 65536, token))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (selfCommand.Length != 3 || selfCommand[1] != profile.ExecutableDll
            || !NonredirectedPath(selfCommand[2]))
            throw new InvalidDataException("Actual front launch command differs.");
        string actualExecutable = new FileInfo($"/proc/{profile.ParentPid}/exe").LinkTarget ?? throw new InvalidDataException();
        if (actualExecutable != profile.ParentExecutablePath || await FileHashAsync($"/proc/{profile.ParentPid}/exe", token) != profile.ParentExecutableSha256)
            throw new InvalidDataException("Actual owner executable differs.");
        var command = Encoding.UTF8.GetString(await ReadBoundedAsync($"/proc/{profile.ParentPid}/cmdline", 65536, token))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (command.Length is < 2 or > 8 || command[0] != profile.ParentExecutablePath || command[1] != profile.ParentScriptPath)
            throw new InvalidDataException("Actual owner source command differs.");
        string pipe = "pipe:[" + profile.BootstrapPipeInode + "]";
        var parentDescriptors = Directory.EnumerateFiles($"/proc/{profile.ParentPid}/fd").Take(4097).ToArray();
        if (parentDescriptors.Length > 4096 || new FileInfo("/proc/self/fd/" + profile.BootstrapPipeHandle).LinkTarget != pipe
            || !parentDescriptors.Any(path => new FileInfo(path).LinkTarget == pipe))
            throw new InvalidDataException("Actual inherited owner pipe differs.");
        var final = ProcessStat(Encoding.ASCII.GetString(await ReadBoundedAsync($"/proc/{profile.ParentPid}/stat", 65536, token)));
        if (final.Start != parent.Start || DateTimeOffset.UtcNow >= profile.ExpiresUtc)
            throw new InvalidDataException("Owner generation or lease changed.");
    }

    internal static (int Parent, long Start) ProcessStat(string value)
    {
        int end = value.LastIndexOf(')');
        var fields = end < 1 ? [] : value[(end + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0] is "Z" or "X" or "x"
            || !int.TryParse(value.AsSpan(0, Math.Max(0, value.IndexOf(' '))), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) || parent <= 0
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long started) || started <= 0)
            throw new InvalidDataException("Actual kernel process generation unavailable.");
        return (parent, started);
    }

    internal static void UniqueObjects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new InvalidDataException("Duplicated owner profile field denied.");
            foreach (var property in properties) UniqueObjects(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueObjects(item);
    }

    private static bool HashShape(string value) => Regex.IsMatch(value, "^[0-9a-f]{64}$");
    private static bool ExactFile(string path, string repository, string hash) => Path.IsPathFullyQualified(path)
        && Path.GetFullPath(path).StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && HashShape(hash) && NonredirectedPath(path);

    internal static bool NonredirectedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) return false;
        if (new FileInfo(path).LinkTarget is not null) return false;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) return false;
        return true;
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length is < 1 or > 67108864) throw new InvalidDataException("Bounded actual executable required.");
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Owner observation exceeded its bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
