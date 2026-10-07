using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace HostedProcessStartObserver;

/// <summary>Linux component-bound nonblocking opens; only typed kernel executable links may be followed.</summary>
internal static class DescriptorBoundFrontFile
{
    internal readonly record struct Identity(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong Size,
        long ModifiedSeconds, uint ModifiedNanoseconds, long ChangedSeconds, uint ChangedNanoseconds, uint Uid, ushort Mode);
    internal sealed record Snapshot(byte[] Bytes, Identity Identity, string Sha256);
    private const int ReadOnly = 0, Nonblock = 0x800, Directory = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;

    internal static async Task<Snapshot> ReadAsync(string path, int maximum, CancellationToken token,
        Action? afterOpen = null, bool requirePrivateOwner = false)
    {
        token.ThrowIfCancellationRequested();
        using var handle = Open(path, followKernelExecutable: false);
        var before = Observe(handle, maximum, requirePrivateOwner);
        afterOpen?.Invoke();
        byte[] bytes = await Read(handle, maximum, token);
        var after = Observe(handle, maximum, requirePrivateOwner);
        using var current = Open(path, followKernelExecutable: false);
        if (before != after || after != Observe(current, maximum, requirePrivateOwner) || (ulong)bytes.Length != after.Size)
            throw new InvalidDataException("Actual regular file identity changed during readback.");
        return new(bytes, after, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    internal static async Task HashAsync(string path, string expected, CancellationToken token)
    {
        var value = await ReadAsync(path, 67108864, token);
        token.ThrowIfCancellationRequested();
        if (value.Sha256 != expected) throw new InvalidDataException("Actual front/parent executable digest differs.");
    }

    internal static async Task HashKernelExecutableAsync(int pid, string executable, string expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pid <= 0 || !Path.IsPathFullyQualified(executable)) throw new InvalidDataException("Typed kernel executable required.");
        string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/exe";
        if (new FileInfo(path).LinkTarget != executable) throw new InvalidDataException("Actual kernel executable target differs.");
        using var handle = Open(path, followKernelExecutable: true);
        var before = Observe(handle, 67108864, false);
        byte[] bytes = await Read(handle, 67108864, token);
        using var current = Open(path, followKernelExecutable: true);
        if (before != Observe(handle, 67108864, false) || before != Observe(current, 67108864, false)
            || (ulong)bytes.Length != before.Size || new FileInfo(path).LinkTarget != executable
            || Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new InvalidDataException("Actual kernel executable identity/digest changed.");
    }

    private static SafeFileHandle Open(string path, bool followKernelExecutable)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path
            || path == "/" || path.EndsWith('/')) throw new InvalidDataException("Exact Linux regular file path required.");
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (followKernelExecutable && (parts.Length != 3 || parts[0] != "proc" || parts[2] != "exe"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
            || parts[1] != pid.ToString(CultureInfo.InvariantCulture))) throw new InvalidDataException("Only typed /proc/PID/exe links allowed.");
        SafeFileHandle directory = Checked(open("/", ReadOnly | Directory | Nonblock | NoFollow | CloseOnExec));
        try
        {
            for (int index = 0; index < parts.Length - 1; index++)
            {
                var next = Checked(openat(directory, parts[index], ReadOnly | Directory | Nonblock | NoFollow | CloseOnExec));
                directory.Dispose(); directory = next;
            }
            return Checked(openat(directory, parts[^1], ReadOnly | Nonblock | CloseOnExec | (followKernelExecutable ? 0 : NoFollow)));
        }
        finally { directory.Dispose(); }
    }

    private static SafeFileHandle Checked(int descriptor)
    {
        if (descriptor < 0) throw new InvalidDataException("Nonblocking nonredirected file open rejected.");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static Identity Observe(SafeFileHandle handle, int maximum, bool requirePrivateOwner)
    {
        // statx has a fixed 256-byte Linux ABI, unlike architecture-dependent struct stat.
        if (statx(handle, "", 0x1000, 0x7ff, out var value) != 0 || (value.Mask & 0x7ff) != 0x7ff
            || (value.Mode & 0xf000) != 0x8000 || value.Size < 1 || value.Size > (ulong)maximum
            || (requirePrivateOwner && (value.Uid != geteuid() || (value.Mode & 0x1ff) != 0x180)))
            throw new InvalidDataException("Bounded actual regular file required.");
        return new(value.DeviceMajor, value.DeviceMinor, value.Inode, value.Size,
            value.ModifiedSeconds, value.ModifiedNanoseconds, value.ChangedSeconds, value.ChangedNanoseconds, value.Uid, value.Mode);
    }

    private static async Task<byte[]> Read(SafeFileHandle handle, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The descriptor was proved regular before FileStream construction; FIFO/device opens never reach here.
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read, 4096, isAsync: false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Actual regular file exceeded bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangedSeconds;
        [FieldOffset(104)] internal uint ChangedNanoseconds;
        [FieldOffset(112)] internal long ModifiedSeconds;
        [FieldOffset(120)] internal uint ModifiedNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(SafeFileHandle directory, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(SafeFileHandle descriptor, string path, int flags, uint mask, out Statx value);
    [DllImport("libc")] private static extern uint geteuid();
}
