using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FinancialHttpCounters;
using Microsoft.Win32.SafeHandles;

if (!OperatingSystem.IsLinux() || args.Length != 2
    || args[0] is not ("fast" or "overflow" or "timeout" or "inherited")) return 2;

try { return await Execute(args); }
catch (Exception) { Console.WriteLine("{\"Outcome\":\"Failed\",\"FinancialAdmissionAccepted\":false}"); return 1; }

static async Task<int> Execute(string[] args)
{
    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
    string mode = args[0];
    if (mode == "inherited")
    {
        Require(Native.prctl(36, 1, 0, 0, 0) == 0);
        Require(Native.prctl_get(37, out int observed, 0, 0, 0) == 0 && observed == 1);
    }
    string root = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(root);
    string git = Path.Combine(root, "git");
    File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture_git.py"), git);
    File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    Environment.SetEnvironmentVariable("PATH", root + ":" + Environment.GetEnvironmentVariable("PATH"));
    Environment.SetEnvironmentVariable("SOURCE_HELPER_ROOT", root);
    Environment.SetEnvironmentVariable("SOURCE_HELPER_CASE", mode);

    // Warm up runtime task/JSON and cancellation infrastructure before comparing held pipe descriptors.
    await Task.Delay(1);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    _ = JsonSerializer.Serialize(new { Warmup = true });

    var pin = new ProcessPin("Accounting", 1, 1, 1, new("/unused", ""), new("/unused", ""),
        new("/unused", ""), "", new(), root, new string('1', 40), new string('2', 40), []);
    var clock = Stopwatch.StartNew();
    Exception? failure = null;
    SafeFileHandle? writerPidfd = null;
    Birth? heldWriter = null;
    Task verify = ProcessAdmission.VerifySourceAsync(pin, deadline.Token);
    try
    {
        if (mode == "inherited")
        {
            await Until(() => File.Exists(Path.Combine(root, "writer-ready")) && ReadBirths(root).Length == 2, deadline.Token);
            Birth writer = ReadBirths(root).Single(b => b.Kind == "pipe-writer");
            heldWriter = writer;
            Require(WriterLive(writer));
            int descriptor = Native.pidfd_open(writer.Pid, 0);
            Require(descriptor >= 0);
            writerPidfd = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            Require(WriterLive(writer) && !WriterExited(writerPidfd));
            string challenge = Guid.NewGuid().ToString("N");
            AtomicText(Path.Combine(root, "writer-bound"), challenge);
            await Until(() => File.Exists(Path.Combine(root, "writer-ack"))
                && File.ReadAllText(Path.Combine(root, "writer-ack")) == challenge + "|" + writer.Pid + "|" + writer.Ticks, deadline.Token);
            Require(WriterLive(writer, allowAdopted: true) && !WriterExited(writerPidfd));
        }
        await verify;
    }
    catch (Exception error) { failure = error; if (!verify.IsCompleted) deadline.Cancel(); }

    object? verifiedReceipt = null;
    try
    {
        var births = ReadBirths(root);
        Require(births.Length == (mode == "fast" ? 3 : mode == "inherited" ? 2 : 1));
        Require(births.Where(x => x.Kind == "git").All(x => x.Parent == Environment.ProcessId));
        bool retainedTasksObserved = false, handlesClosed = false, stickyRefusal = false, descendantExited = false;
        bool exactOriginalChildrenExited = births.Where(x => x.Kind == "git").All(OriginalAbsent);
        Require(exactOriginalChildrenExited);
        if (mode == "fast")
        {
            Require(failure is null && clock.Elapsed < TimeSpan.FromSeconds(10));
            Require(Static("SourceGitQuarantine") is null && !(bool)Static("SourceGitCleanupRefused")!);
        }
        else if (mode is "overflow" or "timeout")
        {
            Require(failure is not null && clock.Elapsed < TimeSpan.FromSeconds(15));
            if (mode == "overflow")
            {
                // Exact exception originates in the frozen bounded reader, rather than cancellation or exit.
                Require(failure is InvalidDataException && failure.Message == "Source observation output exceeded bound");
            }
            else Require(failure is OperationCanceledException && File.Exists(Path.Combine(root, "blocked")));
            Require(Static("SourceGitQuarantine") is null && !(bool)Static("SourceGitCleanupRefused")!);
        }

        else
        {
            Require(failure is not null && clock.Elapsed < TimeSpan.FromSeconds(18));
            object lease = Static("SourceGitQuarantine") ?? throw new InvalidDataException("Expected quarantine");
            var actualGit = (Process)Field(lease, "process")!;
            Require(actualGit.HasExited && actualGit.ExitCode == 0);
            Require((bool)Static("SourceGitCleanupRefused")!);
            var outputDrain = Field(lease, "outputDrainTask") as Task;
            var errorDrain = Field(lease, "errorDrainTask") as Task;
            var originalOutput = Field(lease, "outputTask") as Task;
            var originalError = Field(lease, "errorTask") as Task;
            var heldHandles = new[] { "stdoutHandle", "stderrHandle", "stdinHandle", "pidfd", "processHandle" }
            .Select(name => Field(lease, name) as System.Runtime.InteropServices.SafeHandle).ToArray();
            Require(originalOutput is not null && originalError is not null);
            Require(heldHandles.Take(3).All(handle => handle is not null));
            Require(!heldHandles[0]!.IsClosed && !heldHandles[1]!.IsClosed);
            Require(births.Single(x => x.Kind == "pipe-writer").Parent == births.Single(x => x.Kind == "git").Pid);
            retainedTasksObserved = originalOutput is not null && originalError is not null;
            Require(retainedTasksObserved);
            try
            {
                // Release only this synthetic descendant by its owned protocol. No PID, group, or broad signal.
                File.WriteAllText(Path.Combine(root, "release"), "release");
                await Until(() => File.Exists(Path.Combine(root, "writer-closed")), deadline.Token);
                Require(writerPidfd is not null);
                await Until(() => WriterExited(writerPidfd!), deadline.Token);
                await ReapWriter(heldWriter!, deadline.Token);
                descendantExited = true;
                await Until(() => (outputDrain is null || outputDrain.IsCompleted)
                    && (errorDrain is null || errorDrain.IsCompleted), deadline.Token);
                int priorBirths = births.Length;
                Exception? refused = null;
                try { await ProcessAdmission.VerifySourceAsync(pin, deadline.Token); }
                catch (Exception error) { refused = error; }
                Require(refused is InvalidDataException && ReadBirths(root).Length == priorBirths);
                Require(Static("SourceGitQuarantine") is null && (bool)Static("SourceGitCleanupRefused")!);
                Require(ReferenceEquals(originalOutput, Field(lease, "outputTask"))
                    && ReferenceEquals(originalError, Field(lease, "errorTask")));
                handlesClosed = heldHandles.All(handle => handle is null || handle.IsClosed);
                Require(handlesClosed && (bool)Field(lease, "closed")!);
                stickyRefusal = true;
            }
            finally { File.WriteAllText(Path.Combine(root, "release"), "release"); }
        }

        bool exactOwnedPipesAbsent = births.All(b => !Pipes().Contains(b.StdoutPipe) && !Pipes().Contains(b.StderrPipe));
        Require(exactOwnedPipesAbsent);
        // Private root path/PIDs/errors stay in memory. Only finite typed evidence is retained publicly.
        var receipt = new
        {
            Case = mode,
            Outcome = "Passed",
            ActualVerifySourceAsyncInvoked = true,
            ActualSyntheticGitBirths = births.Count(x => x.Kind == "git"),
            ExactOriginalChildrenExited = exactOriginalChildrenExited,
            RetainedOriginalTasksObserved = retainedTasksObserved,
            RecoveredHeldHandlesClosed = handlesClosed,
            StickyRefusalAndNoNewBirth = stickyRefusal,
            ExactOwnedPipeIdentitiesAbsent = exactOwnedPipesAbsent,
            ExactSyntheticWriterPidfdExitObserved = descendantExited,
            NaturalParentExitObserved = descendantExited,
            CausalOverflowObserved = mode == "overflow",
            FiniteTimeoutObserved = mode == "timeout",
            ElapsedMilliseconds = clock.ElapsedMilliseconds,
            FinancialAdmissionAccepted = false
        };
        verifiedReceipt = receipt;
    }
    finally
    {
        bool cleanupVerified = true;
        if (mode == "inherited")
        {
            try { File.WriteAllText(Path.Combine(root, "release"), "release"); }
            catch (Exception) { cleanupVerified = false; }
        }
        using var sourceQuiescence = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await verify.WaitAsync(sourceQuiescence.Token); }
        catch (Exception)
        {
            if (!verify.IsCompleted) cleanupVerified = false;
            if (verify.IsFaulted) _ = verify.Exception;
        }
        if (heldWriter is not null)
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool writerExited = false, writerReaped = false;
            if (writerPidfd is not null)
            {
                try { await Until(() => WriterExited(writerPidfd), cleanupDeadline.Token); writerExited = true; }
                catch (Exception) { cleanupVerified = false; }
            }
            try { await ReapWriter(heldWriter, cleanupDeadline.Token); writerReaped = true; }
            catch (Exception) { cleanupVerified = false; }
            if (writerPidfd is not null)
            {
                if (writerExited && writerReaped)
                {
                    try { writerPidfd.Dispose(); Require(writerPidfd.IsClosed); }
                    catch (Exception) { cleanupVerified = false; }
                }
                else cleanupVerified = false;
            }
        }
        Require(cleanupVerified);
    }
    Require(verifiedReceipt is not null);
    string receiptJson = JsonSerializer.Serialize(verifiedReceipt);
    File.WriteAllText(Path.Combine(root, "receipt.json"), receiptJson);
    Console.WriteLine(receiptJson);
    return 0;
}

static void Require(bool value)
{
    if (!value) throw new InvalidDataException("Typed lifecycle control failed");
}

static void AtomicText(string path, string value)
{
    File.WriteAllText(path + ".tmp", value);
    File.Move(path + ".tmp", path);
}

static HashSet<string> Pipes()
{
    var result = new HashSet<string>(StringComparer.Ordinal);
    foreach (string descriptor in Directory.GetFiles("/proc/self/fd"))
    {
        string? target = new FileInfo(descriptor).LinkTarget;
        if (target?.StartsWith("pipe:[", StringComparison.Ordinal) == true) result.Add(target);
    }
    return result;
}

static object? Static(string name) => typeof(ProcessAdmission).GetField(name,
    BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

static object? Field(object instance, string name) => instance.GetType().GetField(name,
    BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

static Birth[] ReadBirths(string root) => File.ReadAllLines(Path.Combine(root, "births.jsonl"))
    .Select(line => JsonSerializer.Deserialize<Birth>(line, new JsonSerializerOptions
    { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Birth record")).ToArray();

static bool WriterLive(Birth birth, bool allowAdopted = false)
{
    string stat = File.ReadAllText($"/proc/{birth.Pid}/stat");
    string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return fields[0] is not ("Z" or "X") && ulong.Parse(fields[19]) == birth.Ticks
        && (int.Parse(fields[1]) == birth.Parent || (allowAdopted && int.Parse(fields[1]) == Environment.ProcessId));
}

static bool OriginalAbsent(Birth birth)
{
    string path = $"/proc/{birth.Pid}/stat";
    try
    {
        string stat = File.ReadAllText(path);
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A reused numeric PID is distinguished from the recorded original kernel generation.
        return ulong.Parse(fields[19]) != birth.Ticks;
    }
    catch (FileNotFoundException) { return true; }
    catch (DirectoryNotFoundException) { return true; }
}

static async Task Until(Func<bool> predicate, CancellationToken token)
{
    while (!predicate()) await Task.Delay(20, token);
}

static bool WriterExited(SafeFileHandle pidfd)
{
    var poll = new Native.PollDescriptor { Fd = (int)pidfd.DangerousGetHandle(), Events = 1, Returned = 0 };
    int result = Native.poll(ref poll, 1, 0);
    Require(result >= 0 && (poll.Returned & 32) == 0);
    return result == 1 && (poll.Returned & 1) != 0;
}

static async Task ReapWriter(Birth writer, CancellationToken token)
{
    while (!OriginalAbsent(writer))
    {
        int result = Native.waitpid(writer.Pid, out _, 1);
        Require(result == 0 || result == writer.Pid || (result == -1 && Marshal.GetLastPInvokeError() == 10));
        await Task.Delay(20, token);
    }
}

sealed record Birth(string Kind, int Pid, int Ppid, ulong Ticks, string StdoutPipe, string StderrPipe)
{
    public int Parent => Ppid;
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollDescriptor { internal int Fd; internal short Events; internal short Returned; }
    [DllImport("libc", SetLastError = true)] internal static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollDescriptor descriptor, nuint count, int milliseconds);
    [DllImport("libc", SetLastError = true)] internal static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc", SetLastError = true)] internal static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)] internal static extern int prctl_get(int option, out int arg2, nuint arg3, nuint arg4, nuint arg5);
}
