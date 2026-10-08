using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FinancialHttpCounters;

// Fresh output is created relative to a held, component-bound directory. No write occurs during admission.
public sealed class DescriptorBoundOutput : IDisposable
{
    private const int Nonblock = 0x800, DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    private readonly SafeFileHandle parent;
    private readonly string parentPath, name;
    private readonly (uint Major, uint Minor, ulong Inode) identity;
    private bool written;
    private DescriptorBoundOutput(SafeFileHandle directory, string path, string leaf)
    { parent = directory; parentPath = path; name = leaf; identity = Identity(parent); }

    public static DescriptorBoundOutput Admit(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CounterState.Require(OperatingSystem.IsLinux() && Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path
            && !path.EndsWith('/') && !path.Contains('\n'), "Exact Linux output path required");
        string? directory = Path.GetDirectoryName(path);
        CounterState.Require(directory is not null && Path.GetFileName(path) is not ("" or "." or ".."), "Output parent required");
        var handle = OpenDirectory(directory);
        try
        {
            token.ThrowIfCancellationRequested();
            // AT_SYMLINK_NOFOLLOW: reject existing files, symlinks and nonregular leaves alike.
            int existing = statx(handle, Path.GetFileName(path), 0x100, 0x100, out _);
            CounterState.Require(existing == -1 && Marshal.GetLastPInvokeError() == 2, "Fresh output leaf required");
            return new(handle, directory, Path.GetFileName(path));
        }
        catch { handle.Dispose(); throw; }
    }

    public async Task WriteAsync(byte[] bytes)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CounterState.Require(!written && bytes.Length <= 1024 * 1024, "Single bounded output required");
        using var current = OpenDirectory(parentPath);
        CounterState.Require(Identity(current) == identity && Identity(parent) == identity, "Output parent replaced");
        // O_WRONLY | O_CREAT | O_EXCL plus nofollow/nonblock; 0600 prevents unintended private receipt exposure.
        int fd = openat(parent, name, 1 | 0x40 | 0x80 | NoFollow | Nonblock | CloseOnExec, 0x180);
        CounterState.Require(fd >= 0, "Fresh descriptor output create rejected");
        written = true;
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        using var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        await stream.WriteAsync(bytes, deadline.Token); await stream.FlushAsync(deadline.Token);
        using var after = OpenDirectory(parentPath);
        CounterState.Require(Identity(after) == identity && Identity(parent) == identity, "Output parent changed during write");
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        CounterState.Require(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path, "Canonical output directory required");
        int fd = open("/", DirectoryFlag | NoFollow | Nonblock | CloseOnExec);
        CounterState.Require(fd >= 0, "Root directory unavailable");
        var held = new SafeFileHandle((IntPtr)fd, true);
        try
        {
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                fd = openat(held, part, DirectoryFlag | NoFollow | Nonblock | CloseOnExec, 0);
                CounterState.Require(fd >= 0, "Redirected output directory rejected");
                var next = new SafeFileHandle((IntPtr)fd, true); held.Dispose(); held = next;
            }
            _ = Identity(held); return held;
        }
        catch { held.Dispose(); throw; }
    }
    private static (uint Major, uint Minor, ulong Inode) Identity(SafeFileHandle handle)
    {
        CounterState.Require(statx(handle, "", 0x1000, 0x101, out var value) == 0 && (value.Mask & 0x101) == 0x101
            && (value.Mode & 0xf000) == 0x4000, "Actual output directory descriptor required");
        return (value.Major, value.Minor, value.Inode);
    }
    public void Dispose() => parent.Dispose();
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint Major;
        [FieldOffset(140)] internal uint Minor;
    }
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int openat(SafeFileHandle parent, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int statx(SafeFileHandle parent, string path, int flags, uint mask, out Statx value);
}
