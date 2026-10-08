using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Expected values acquired by the original scanner owner. A profile is not an ownership or build receipt.</summary>
public sealed record SharedScannerBridgeLease(string ScannerContainerId, string ScannerCreated,
    string ScannerStarted, int ScannerPid, string ScannerImageId, string ScannerBridgeIp,
    string NetworkName, string Subnet, string Gateway);

/// <summary>Read-only shared-bridge checks. Cannot release resources, mint lifecycle evidence or admit File by itself.</summary>
internal sealed class SharedScannerBridgeObservation(StorageBackendLease backend, SharedScannerBridgeLease scanner)
{
    internal const string BaseImage = "clamav/clamav@sha256:7659dcb0db47941d3cf8336af84bbb63c7e70b76fc00601774358412b42ed186";

    internal void ValidateDeclaration()
    {
        if (!backend.BindOwnedIpv4 || !Hex(scanner.ScannerContainerId, 64) || scanner.ScannerContainerId == backend.ContainerId
            || !scanner.ScannerImageId.StartsWith("sha256:", StringComparison.Ordinal) || !Hex(scanner.ScannerImageId[7..], 64)
            || scanner.ScannerPid <= 0 || scanner.NetworkName != "financial-scanner-" + backend.LeaseId[5..] + "-network"
            || ObservedStorageBackend.DockerInstant(scanner.ScannerCreated) < backend.IssuedUtc
            || ObservedStorageBackend.DockerInstant(scanner.ScannerStarted) < ObservedStorageBackend.DockerInstant(scanner.ScannerCreated)
            || ObservedStorageBackend.DockerInstant(scanner.ScannerStarted) >= backend.ExpiresUtc)
            throw new InvalidDataException("Exact original shared-scanner declaration required.");
        if (backend.ExpiresUtc.Offset != TimeSpan.Zero || backend.IssuedUtc.Offset != TimeSpan.Zero
            || backend.ExpiresUtc <= backend.IssuedUtc || backend.ExpiresUtc - backend.IssuedUtc > TimeSpan.FromMinutes(30)
            || ObservedStorageBackend.DockerInstant(backend.NetworkCreated) < backend.IssuedUtc
            || ObservedStorageBackend.DockerInstant(backend.NetworkCreated) > ObservedStorageBackend.DockerInstant(scanner.ScannerCreated))
            throw new InvalidDataException("Exact finite shared generation lease required.");
        uint first = Address(scanner.Subnet.Split('/')[0]);
        uint pool = Address("10.253.240.0");
        if (scanner.Subnet != new IPAddress(Binary(first)).ToString() + "/28" || first < pool || first > pool + 15 * 16
            || (first - pool) % 16 != 0 || Address(scanner.Gateway) != first + 1)
            throw new InvalidDataException("Source-selected canonical shared subnet required.");
        uint storage = Address(backend.BridgeIp), scanning = Address(scanner.ScannerBridgeIp);
        if (storage <= first + 1 || storage >= first + 15 || scanning <= first + 1 || scanning >= first + 15 || storage == scanning)
            throw new InvalidDataException("Distinct usable original private endpoints required.");
    }

    internal void VerifyNetwork(JsonElement value)
    {
        ValidateDeclaration();
        var item = One(value);
        if (Text(item, "Id") != backend.NetworkId || Text(item, "Created") != backend.NetworkCreated
            || Text(item, "Name") != scanner.NetworkName || Text(item, "Driver") != "bridge"
            || !item.GetProperty("Internal").GetBoolean() || item.GetProperty("EnableIPv6").GetBoolean()
            || item.GetProperty("Ingress").GetBoolean() || item.GetProperty("Attachable").GetBoolean()
            || Text(item, "Scope") != "local" || Text(item.GetProperty("Labels"), "financial.acceptance.run") != backend.LeaseId[5..])
            throw new InvalidDataException("Actual original shared bridge differs.");
        var members = item.GetProperty("Containers").EnumerateObject().Select(x => x.Name).ToArray();
        if (members.Length != 2 || !members.Contains(backend.ContainerId, StringComparer.Ordinal)
            || !members.Contains(scanner.ScannerContainerId, StringComparer.Ordinal))
            throw new InvalidDataException("Exact original scanner/backend census required.");
        var ipam = item.GetProperty("IPAM");
        if (Text(ipam, "Driver") != "default" || Nonempty(ipam, "Options")) throw new InvalidDataException("Shared IPAM policy differs.");
        var configs = ipam.GetProperty("Config");
        if (configs.ValueKind != JsonValueKind.Array || configs.GetArrayLength() != 1)
            throw new InvalidDataException("One exact original subnet required.");
        var config = configs[0];
        if (Text(config, "Subnet") != scanner.Subnet || Text(config, "Gateway") != scanner.Gateway
            || Nonempty(config, "IPRange") || Nonempty(config, "AuxiliaryAddresses"))
            throw new InvalidDataException("Actual original shared subnet differs.");
    }

    internal void VerifyScanner(JsonElement value)
    {
        ValidateDeclaration();
        var item = One(value);
        var state = item.GetProperty("State");
        var config = item.GetProperty("Config");
        var host = item.GetProperty("HostConfig");
        if (Text(item, "Id") != scanner.ScannerContainerId || Text(item, "Created") != scanner.ScannerCreated
            || Text(state, "StartedAt") != scanner.ScannerStarted || state.GetProperty("Pid").GetInt32() != scanner.ScannerPid
            || !state.GetProperty("Running").GetBoolean() || state.GetProperty("Paused").GetBoolean() || state.GetProperty("Restarting").GetBoolean()
            || item.GetProperty("RestartCount").GetInt32() != 0 || Text(item, "Image") != scanner.ScannerImageId
            || Text(config, "Image") != scanner.ScannerImageId || Text(config.GetProperty("Labels"), "financial.acceptance.run") != backend.LeaseId[5..]
            || !Strings(config, "Entrypoint").SequenceEqual(["/usr/sbin/clamd"], StringComparer.Ordinal)
            || !Strings(config, "Cmd").SequenceEqual(["--foreground", "--config-file=/etc/clamav/acceptance.conf"], StringComparer.Ordinal)
            || Nonempty(config, "Volumes") || Text(host, "NetworkMode") != backend.NetworkId
            || !host.GetProperty("ReadonlyRootfs").GetBoolean() || host.GetProperty("Privileged").GetBoolean()
            || host.GetProperty("Memory").GetInt64() != 1536L * 1024 * 1024 || host.GetProperty("NanoCpus").GetInt64() != 2000000000
            || !Strings(host, "CapDrop").SequenceEqual(["ALL"], StringComparer.Ordinal) || Nonempty(host, "CapAdd")
            || Nonempty(host, "Binds") || Nonempty(host, "Devices") || Nonempty(host, "PortBindings") || host.GetProperty("PublishAllPorts").GetBoolean()
            || Text(host, "PidMode") != "" || Text(host, "IpcMode") != "private" || Text(host, "CgroupnsMode") != "private"
            || !Strings(host, "SecurityOpt").Any(x => x is "no-new-privileges" or "no-new-privileges:true")
            || Text(host.GetProperty("RestartPolicy"), "Name") != "no" || host.GetProperty("AutoRemove").GetBoolean())
            throw new InvalidDataException("Actual original scanner generation or Engine policy differs.");
        var mounts = item.GetProperty("Mounts");
        if (mounts.ValueKind != JsonValueKind.Array || mounts.GetArrayLength() != 2
            || mounts.EnumerateArray().Any(x => Text(x, "Type") != "tmpfs")
            || !mounts.EnumerateArray().Select(x => Text(x, "Destination")).Order(StringComparer.Ordinal).SequenceEqual(["/run", "/tmp"], StringComparer.Ordinal))
            throw new InvalidDataException("Only original scanner tmpfs destinations permitted.");
        var tmpfs = host.GetProperty("Tmpfs");
        if (tmpfs.EnumerateObject().Count() != 2 || Text(tmpfs, "/tmp") != "rw,nosuid,nodev,size=32m"
            || Text(tmpfs, "/run") != "rw,nosuid,nodev,size=4m") throw new InvalidDataException("Original finite scanner tmpfs policy required.");
        var settings = item.GetProperty("NetworkSettings");
        if (settings.GetProperty("Ports").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
            || settings.GetProperty("Ports").ValueKind == JsonValueKind.Object && settings.GetProperty("Ports").EnumerateObject().Any(x => x.Value.ValueKind != JsonValueKind.Null))
            throw new InvalidDataException("Scanner Docker publication denied.");
        var attachments = settings.GetProperty("Networks").EnumerateObject().ToArray();
        if (attachments.Length != 1 || Text(attachments[0].Value, "NetworkID") != backend.NetworkId
            || Text(attachments[0].Value, "IPAddress") != scanner.ScannerBridgeIp)
            throw new InvalidDataException("Actual scanner private endpoint differs.");
    }

    internal void VerifyImageChain(JsonElement baseValue, JsonElement derivedValue)
    {
        ValidateDeclaration();
        var original = One(baseValue); var derived = One(derivedValue);
        var layers = Strings(original.GetProperty("RootFS"), "Layers");
        var inherited = Strings(derived.GetProperty("RootFS"), "Layers");
        if (!Strings(original, "RepoDigests").Contains(BaseImage, StringComparer.Ordinal) || layers.Length == 0
            || Text(derived, "Id") != scanner.ScannerImageId || inherited.Length != layers.Length + 1
            || !inherited.Take(layers.Length).SequenceEqual(layers, StringComparer.Ordinal)
            || Text(derived.GetProperty("Config").GetProperty("Labels"), "financial.acceptance.run") != backend.LeaseId[5..]
            || Nonempty(derived.GetProperty("Config"), "Volumes"))
            throw new InvalidDataException("Actual pinned base/derived scanner image association differs.");
    }

    private static byte[] Binary(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
    private static uint Address(string value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && ip.ToString() == value ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()) : throw new InvalidDataException("Canonical IPv4 required.");
    private static bool Hex(string value, int size) => value.Length == size && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonElement One(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 ? value[0] : throw new InvalidDataException("One exact inspected object required.");
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString() ?? throw new InvalidDataException();
    private static string[] Strings(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null ? [] : value.GetProperty(key).EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException()).ToArray();
    private static bool Nonempty(JsonElement value, string key) => value.TryGetProperty(key, out var item) && (item.ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.String => item.GetString() != "",
        JsonValueKind.Array => item.GetArrayLength() != 0,
        JsonValueKind.Object => item.EnumerateObject().Any(),
        _ => true,
    });
}
