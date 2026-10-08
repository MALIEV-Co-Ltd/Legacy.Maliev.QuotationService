using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Exact expected run-owned immutable backend; declarations require actual Docker/process observation.</summary>
public sealed record StorageBackendLease(string ContainerId, string NetworkId, string ImageReference,
    string ImageId, string Created, string Started, string BridgeIp, int Port,
    string ExecutablePath, string ExecutableSha256, string[] Entrypoint, string[] Command,
    string RunId, string LeaseId, string GithubRunId, string GithubAttempt, DateTimeOffset ExpiresUtc,
    long MemoryLimitBytes, long NanoCpus, long KernelStartTicks,
    string FileSourceSha, string ExpiresText, string NetworkCreated, DateTimeOffset IssuedUtc)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BindOwnedIpv4 { get; init; }
}

/// <summary>Re-observes a private immutable backend without starting, stopping or altering resources.</summary>
public sealed class ObservedStorageBackend
{
    private readonly StorageBackendLease lease;
    private readonly SharedScannerBridgeLease? sharedScanner;
    private readonly SharedScannerBridgeObservation? shared;

    public ObservedStorageBackend(StorageBackendLease lease) : this(lease, null) { }

    public ObservedStorageBackend(StorageBackendLease lease, SharedScannerBridgeLease? sharedScanner)
    {
        this.lease = lease;
        this.sharedScanner = sharedScanner;
        shared = sharedScanner is null ? null : new(lease, sharedScanner);
    }
    internal const string PinnedImage = "fsouza/fake-gcs-server@sha256:9e6924ee1b609d9913c3ea836cb4d4a9bc0419cd036ddcd136695720b5713baf";
    internal const string PinnedConfig = "sha256:d79ded7272ddc99187d94f61959f37041792e1a79cb3e6d26c9dfb5b845aca21";
    /// <summary>Actual endpoint only; it is never a provider/cloud destination.</summary>
    public Uri Origin => new($"http://{lease.BridgeIp}:{lease.Port}/");

    internal Uri AdmitOrigin(string runId, DateTimeOffset expiresUtc)
    {
        Validate(DateTimeOffset.UtcNow);
        RequireLeaseBinding(runId, expiresUtc);
        return Origin;
    }

    internal void RequireLeaseBinding(string runId, DateTimeOffset expiresUtc)
    {
        if (runId != lease.RunId || expiresUtc != lease.ExpiresUtc)
            throw new InvalidDataException("Front and actual backend must belong to the same exact run lease.");
    }

    /// <summary>Validates declaration and current hosted run before any helper or forwarding I/O.</summary>
    public void Validate(DateTimeOffset now)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("GITHUB_RUN_ID") != lease.GithubRunId
            || Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") != lease.GithubAttempt
            || !Hex(lease.ContainerId, 64) || !Hex(lease.NetworkId, 64)
            || lease.ImageReference != PinnedImage || lease.ImageId != PinnedConfig
            || !Hex(lease.ExecutableSha256, 64) || lease.ExecutablePath != "/bin/fake-gcs-server"
            || !IPAddress.TryParse(lease.BridgeIp, out var ip) || !PrivateBridge(ip)
            || lease.BindOwnedIpv4 && ip.ToString() != lease.BridgeIp
            || lease.Port is < 1024 or > 65535 || lease.KernelStartTicks <= 0
            || lease.ExpiresUtc.Offset != TimeSpan.Zero || lease.ExpiresUtc <= now || lease.ExpiresUtc > now.AddMinutes(30)
            || lease.MemoryLimitBytes is < 67108864 or > 1073741824 || lease.NanoCpus is <= 0 or > 2000000000
            || !Regex.IsMatch(lease.GithubRunId, "^[1-9][0-9]{0,19}$")
            || !Regex.IsMatch(lease.GithubAttempt, "^[1-9][0-9]{0,8}$")
            || lease.LeaseId != lease.RunId || !Hex(lease.FileSourceSha, 40)
            || lease.IssuedUtc.Offset != TimeSpan.Zero || lease.IssuedUtc > now
            || lease.ExpiresUtc - lease.IssuedUtc > TimeSpan.FromMinutes(30)
            || !DateTimeOffset.TryParse(lease.ExpiresText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            || expires != lease.ExpiresUtc
            || !lease.RunId.StartsWith("c821-", StringComparison.Ordinal)
            || !Guid.TryParseExact(lease.RunId[5..], "D", out var run) || run == Guid.Empty
            || lease.RunId != "c821-" + run.ToString("D")
            || lease.Entrypoint.Length != 1 || lease.Entrypoint[0] != lease.ExecutablePath
            || !lease.Command.SequenceEqual(["-scheme", "http", "-host", lease.BindOwnedIpv4 ? lease.BridgeIp : "0.0.0.0", "-port", lease.Port.ToString(CultureInfo.InvariantCulture),
                "-backend", "memory", "-external-url", Origin.GetLeftPart(UriPartial.Authority), "-public-host", Origin.Authority], StringComparer.Ordinal))
            throw new InvalidDataException("Current exact immutable private backend declaration required.");
        shared?.ValidateDeclaration();
    }

    /// <summary>Verifies Docker generation, image, private network, limits and real init executable/socket owner.</summary>
    public async Task ObserveAsync(CancellationToken cancellationToken)
    {
        Validate(DateTimeOffset.UtcNow);
        using var container = await InspectAsync("container", lease.ContainerId, cancellationToken);
        VerifyContainer(container.RootElement);
        using var network = await InspectAsync("network", lease.NetworkId, cancellationToken);
        VerifyNetwork(network.RootElement);
        if (shared is not null) await ObserveSharedScannerAsync(cancellationToken);
        using var image = await InspectAsync("image", lease.ImageId, cancellationToken);
        var actualImage = One(image.RootElement);
        if (Text(actualImage, "Id") != lease.ImageId
            || !actualImage.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString() == lease.ImageReference)
            || !Array(actualImage.GetProperty("Config"), "Env").SequenceEqual(Array(One(container.RootElement).GetProperty("Config"), "Env"), StringComparer.Ordinal))
            throw new InvalidDataException("Backend immutable image digest differs.");
        long before = await KernelStartAsync(cancellationToken);
        byte[] command = await ExecAsync(["cat", "/proc/1/cmdline"], 65536, cancellationToken);
        var actualArguments = Encoding.UTF8.GetString(command).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!actualArguments.SequenceEqual(lease.Entrypoint.Concat(lease.Command), StringComparer.Ordinal))
            throw new InvalidDataException("Actual backend process arguments differ.");
        string executable = Encoding.UTF8.GetString(await ExecAsync(["readlink", "/proc/1/exe"], 4096, cancellationToken)).Trim();
        string digest = Encoding.ASCII.GetString(await ExecAsync(["sha256sum", "/proc/1/exe"], 4096, cancellationToken)).Split(' ', 2)[0];
        if (executable != lease.ExecutablePath || digest != lease.ExecutableSha256)
            throw new InvalidDataException("Actual backend executable differs.");
        var tcp = Encoding.ASCII.GetString(await ExecAsync(["cat", "/proc/1/net/tcp"], 1048576, cancellationToken));
        string inode = lease.BindOwnedIpv4 ? ListeningInode(tcp, lease.Port, lease.BridgeIp) : ListeningInode(tcp, lease.Port);
        var descriptors = Encoding.ASCII.GetString(await ExecAsync(["ls", "-l", "/proc/1/fd"], 262144, cancellationToken));
        if (!descriptors.Split('\n').Any(value => value.TrimEnd().EndsWith("-> socket:[" + inode + "]", StringComparison.Ordinal)))
            throw new InvalidDataException("Actual backend listener is not owned by its immutable init process.");
        if (before != lease.KernelStartTicks || await KernelStartAsync(cancellationToken) != before)
            throw new InvalidDataException("Backend process generation changed.");
        using var final = await InspectAsync("container", lease.ContainerId, cancellationToken);
        VerifyContainer(final.RootElement);
        if (shared is not null)
        {
            if (One(final.RootElement).GetProperty("State").GetProperty("Pid").GetInt32()
                != One(container.RootElement).GetProperty("State").GetProperty("Pid").GetInt32())
                throw new InvalidDataException("Shared backend Engine process generation changed.");
            using var finalNetwork = await InspectAsync("network", lease.NetworkId, cancellationToken);
            VerifyNetwork(finalNetwork.RootElement);
            await ObserveSharedScannerAsync(cancellationToken);
        }
        if (DateTimeOffset.UtcNow >= lease.ExpiresUtc) throw new InvalidDataException("Backend lease expired during observation.");
    }

    internal void VerifyContainer(JsonElement value)
    {
        var item = One(value);
        var state = item.GetProperty("State");
        var configuration = item.GetProperty("Config");
        var host = item.GetProperty("HostConfig");
        RequireLabels(configuration.GetProperty("Labels"), false);
        if (Text(item, "Id") != lease.ContainerId || Text(item, "Created") != lease.Created
            || Text(item, "Image") != lease.ImageId || Text(configuration, "Image") != lease.ImageReference
            || !state.GetProperty("Running").GetBoolean() || state.GetProperty("Paused").GetBoolean()
            || state.GetProperty("Restarting").GetBoolean() || Text(state, "StartedAt") != lease.Started
            || state.GetProperty("Pid").GetInt32() <= 0 || item.GetProperty("RestartCount").GetInt32() != 0
            || !Array(configuration, "Entrypoint").SequenceEqual(lease.Entrypoint, StringComparer.Ordinal)
            || !Array(configuration, "Cmd").SequenceEqual(lease.Command, StringComparer.Ordinal)
            || Text(host, "NetworkMode") != lease.NetworkId || host.GetProperty("Privileged").GetBoolean()
            || !host.GetProperty("ReadonlyRootfs").GetBoolean() || Text(host.GetProperty("RestartPolicy"), "Name") != "no"
            || host.GetProperty("Memory").GetInt64() != lease.MemoryLimitBytes || host.GetProperty("NanoCpus").GetInt64() != lease.NanoCpus
            || !Array(host, "CapDrop").SequenceEqual(["ALL"], StringComparer.Ordinal)
            || Nonempty(host, "CapAdd") || Nonempty(host, "Binds") || Nonempty(host, "Devices")
            || Nonempty(host, "PortBindings") || Published(item.GetProperty("NetworkSettings").GetProperty("Ports"))
            || Nonempty(item, "Mounts") || Text(host, "PidMode") != "" || Text(host, "IpcMode") == "host"
            || !Array(host, "SecurityOpt").Contains("no-new-privileges:true", StringComparer.Ordinal)
            || Text(host, "IpcMode") != "private" || Text(host, "CgroupnsMode") != "private"
            || host.GetProperty("AutoRemove").GetBoolean() || Nonempty(host, "Tmpfs")
            || DockerInstant(lease.Created) < lease.IssuedUtc || DockerInstant(lease.Created) >= lease.ExpiresUtc
            || DockerInstant(lease.Started) < DockerInstant(lease.Created) || DockerInstant(lease.Started) >= lease.ExpiresUtc)
            throw new InvalidDataException("Actual backend ownership, bounds or immutable generation differ.");
        var networks = item.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().ToArray();
        if (networks.Length != 1 || Text(networks[0].Value, "NetworkID") != lease.NetworkId
            || Text(networks[0].Value, "IPAddress") != lease.BridgeIp)
            throw new InvalidDataException("Actual private backend bridge endpoint differs.");
    }

    internal void VerifyNetwork(JsonElement value)
    {
        if (shared is not null) { shared.VerifyNetwork(value); return; }
        var item = One(value);
        RequireLabels(item.GetProperty("Labels"), true);
        if (Text(item, "Id") != lease.NetworkId || Text(item, "Driver") != "bridge" || !item.GetProperty("Internal").GetBoolean()
            || item.GetProperty("EnableIPv6").GetBoolean() || Text(item, "Scope") != "local"
            || Text(item, "Created") != lease.NetworkCreated || DockerInstant(lease.NetworkCreated) < lease.IssuedUtc
            || DockerInstant(lease.NetworkCreated) >= lease.ExpiresUtc)
            throw new InvalidDataException("Actual owned internal bridge differs.");
        var members = item.GetProperty("Containers").EnumerateObject().ToArray();
        if (members.Length != 1 || members[0].Name != lease.ContainerId)
            throw new InvalidDataException("Storage backend bridge is not isolated to its exact container.");
    }

    private void RequireLabels(JsonElement labels, bool network)
    {
        foreach (var pair in new Dictionary<string, string>
        {
            ["com.maliev.c821.run"] = lease.GithubRunId,
            ["com.maliev.c821.attempt"] = lease.GithubAttempt,
            ["com.maliev.c821.file-source"] = lease.FileSourceSha,
            ["com.maliev.c821.lease"] = lease.LeaseId,
            ["com.maliev.c821.expires"] = lease.ExpiresText,
            ["com.maliev.c821.persistent"] = "false",
        })
            if (Text(labels, pair.Key) != pair.Value) throw new InvalidDataException("Actual resource ownership labels differ.");
        if (!network && Text(labels, "com.maliev.c821.role") != "storage") throw new InvalidDataException("Actual storage role differs.");
    }

    private async Task ObserveSharedScannerAsync(CancellationToken token)
    {
        if (shared is null || sharedScanner is null) throw new InvalidDataException("Original shared declaration absent.");
        using var scanner = await InspectAsync("container", sharedScanner.ScannerContainerId, token);
        shared.VerifyScanner(scanner.RootElement);
        using var original = await InspectAsync("image", SharedScannerBridgeObservation.BaseImage, token);
        using var derived = await InspectAsync("image", sharedScanner.ScannerImageId, token);
        shared.VerifyImageChain(original.RootElement, derived.RootElement);
    }

    private async Task<JsonDocument> InspectAsync(string kind, string id, CancellationToken token) =>
        JsonDocument.Parse(await BoundedOwnedCommand.RunAsync("docker", [kind, "inspect", id], 2097152, token));

    private Task<byte[]> ExecAsync(string[] arguments, int maximum, CancellationToken token) =>
        BoundedOwnedCommand.RunAsync("docker", ["exec", lease.ContainerId, .. arguments], maximum, token);

    private async Task<long> KernelStartAsync(CancellationToken token) =>
        KernelStart(Encoding.ASCII.GetString(await ExecAsync(["cat", "/proc/1/stat"], 65536, token)));

    internal static long KernelStart(string stat)
    {
        int end = stat.LastIndexOf(')');
        var fields = end < 1 ? [] : stat[(end + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!stat.StartsWith("1 (", StringComparison.Ordinal) || fields.Length < 20 || fields[0] is "Z" or "X" or "x"
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0)
            throw new InvalidDataException("Actual backend process stat unavailable.");
        return ticks;
    }

    internal static string ListeningInode(string table, int port) => ListeningInodeAddress(table, port, "00000000");

    internal static string ListeningInode(string table, int port, string bridgeIp)
    {
        if (!IPAddress.TryParse(bridgeIp, out var ip) || !PrivateBridge(ip) || ip.ToString() != bridgeIp)
            throw new InvalidDataException("Exact canonical private IPv4 listener required.");
        byte[] bytes = ip.GetAddressBytes();
        System.Array.Reverse(bytes);
        return ListeningInodeAddress(table, port, Convert.ToHexString(bytes));
    }

    private static string ListeningInodeAddress(string table, int port, string address)
    {
        var rows = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0 || !rows[0].Contains("local_address", StringComparison.Ordinal)) throw new InvalidDataException();
        var matches = new List<string>();
        foreach (var row in rows.Skip(1))
        {
            var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10) throw new InvalidDataException();
            if (fields[3] == "0A" && fields[1] == address + ":" + port.ToString("X4", CultureInfo.InvariantCulture))
            {
                if (!ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode) || inode == 0)
                    throw new InvalidDataException();
                matches.Add(fields[9]);
            }
        }
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException("One backend listener required.");
    }

    private static bool PrivateBridge(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168);
    }
    private static bool Hex(string value, int length) => value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonElement One(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1
        ? value[0] : throw new InvalidDataException("One exact observed resource required.");
    private static string Text(JsonElement item, string name) => item.GetProperty(name).GetString() ?? throw new InvalidDataException();
    private static string[] Array(JsonElement item, string name) => item.GetProperty(name).ValueKind == JsonValueKind.Null
        ? [] : item.GetProperty(name).EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException()).ToArray();
    private static bool Nonempty(JsonElement item, string name) => item.GetProperty(name).ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.Array => item.GetProperty(name).GetArrayLength() != 0,
        JsonValueKind.Object => item.GetProperty(name).EnumerateObject().Any(),
        _ => true,
    };

    private static bool Published(JsonElement value) => value.ValueKind != JsonValueKind.Null
        && (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Null));

    internal static DateTimeOffset DockerInstant(string value)
    {
        var match = Regex.Match(value, "^(?<seconds>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\\.(?<fraction>[0-9]{1,9}))?Z$");
        if (!match.Success) throw new InvalidDataException("Exact Docker UTC generation timestamp required.");
        string fraction = match.Groups["fraction"].Value;
        string normalized = match.Groups["seconds"].Value + (fraction.Length == 0 ? "" : "." + fraction[..Math.Min(7, fraction.Length)]) + "Z";
        return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
