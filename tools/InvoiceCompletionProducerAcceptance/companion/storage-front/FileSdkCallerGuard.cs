using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Exact observed File host prerequisite, not JSON supplied as process authority.</summary>
public sealed record ObservedFileHost(int Pid, DateTimeOffset StartedUtc, string ExecutableDll,
    string ExecutableSha256, string RunId, DateTimeOffset ExpiresUtc);

/// <summary>Admits SDK transport only when the actual request's client socket belongs to the observed File process.</summary>
public sealed class FileSdkCallerGuard(ObservedFileHost file)
{
    public async Task VerifyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;
        if (!OperatingSystem.IsLinux() || remote is null || local is null
            || !IPAddress.IsLoopback(remote) || !IPAddress.IsLoopback(local)
            || context.Connection.RemotePort <= 0 || context.Connection.LocalPort <= 0)
            throw new InvalidDataException("Actual loopback SDK client connection required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await VerifyProcessAsync(deadline.Token);
        var tables = new[]
        {
            await ReadAsync($"/proc/{file.Pid}/net/tcp", 1048576, deadline.Token),
            await ReadAsync($"/proc/{file.Pid}/net/tcp6", 1048576, deadline.Token),
        };
        string inode = FindClientInode(tables, remote, context.Connection.RemotePort, local, context.Connection.LocalPort);
        int count = 0;
        bool owned = false;
        foreach (string path in Directory.EnumerateFiles($"/proc/{file.Pid}/fd"))
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (++count > 4096) throw new InvalidDataException("Bounded File socket ownership observation exceeded.");
            if (new FileInfo(path).LinkTarget == "socket:[" + inode + "]") owned = true;
        }
        if (!owned) throw new InvalidDataException("Actual SDK socket is not owned by the observed File host.");
        await VerifyProcessAsync(deadline.Token);
        // A peer cannot be admitted by presenting another process's PID, source, labels or HTTP header.
        if (file.ExpiresUtc <= DateTimeOffset.UtcNow) throw new InvalidDataException("File SDK owner lease expired.");
    }

    private Task VerifyProcessAsync(CancellationToken cancellationToken) => CompanionLauncherAdmission.VerifyProcessAsync(
        "File", file.Pid, file.StartedUtc, file.ExecutableDll, file.ExecutableSha256.ToUpperInvariant(),
        file.RunId, file.ExpiresUtc, cancellationToken);

    internal static string FindClientInode(IEnumerable<string> tables, IPAddress clientIp, int clientPort,
        IPAddress frontIp, int frontPort)
    {
        var matches = new List<string>();
        foreach (string table in tables)
        {
            var rows = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length == 0 || !rows[0].Contains("local_address", StringComparison.Ordinal))
                throw new InvalidDataException("Actual kernel TCP table unavailable.");
            foreach (string row in rows.Skip(1))
            {
                var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10) throw new InvalidDataException("Incomplete kernel TCP row.");
                if (fields[3] != "01") continue;
                var client = DecodeEndpoint(fields[1]);
                var front = DecodeEndpoint(fields[2]);
                if (Normalize(client.Address).Equals(Normalize(clientIp)) && client.Port == clientPort
                    && Normalize(front.Address).Equals(Normalize(frontIp)) && front.Port == frontPort)
                {
                    if (!ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out ulong inode) || inode == 0)
                        throw new InvalidDataException("Actual socket inode missing.");
                    matches.Add(fields[9]);
                }
            }
        }
        if (matches.Count != 1) throw new InvalidDataException("One unambiguous actual SDK client socket required.");
        return matches[0];
    }

    private static IPEndPoint DecodeEndpoint(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2) throw new InvalidDataException("Malformed kernel TCP endpoint.");
        byte[] bytes = Convert.FromHexString(parts[0]);
        if (bytes.Length is not (4 or 16)) throw new InvalidDataException("Unknown kernel address shape.");
        for (int index = 0; index < bytes.Length; index += 4)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index, 4), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)));
        return new IPEndPoint(new IPAddress(bytes), int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static IPAddress Normalize(IPAddress value) => value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;

    private static async Task<string> ReadAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Kernel readback exceeded bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }
}
