using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

    private static readonly SemaphoreSlim SourceGitGate = new(1, 1);
    private static SourceGitLease? SourceGitQuarantine;
    private static bool SourceGitCleanupRefused;

    public static async Task VerifySourceAsync(ProcessPin pin, CancellationToken token)
    {
        async Task<string> Git(params string[] args)
        {
            using var finite = CancellationTokenSource.CreateLinkedTokenSource(token);
            finite.CancelAfter(TimeSpan.FromSeconds(5));
            await SourceGitGate.WaitAsync(finite.Token);
            try
            {
                if (SourceGitQuarantine is not null)
                {
                    // A fresh cleanup attempt, never a cached failed Task. Recovery cannot erase refusal.
                    if (await SourceGitQuarantine.CleanupAsync()) SourceGitQuarantine = null;
                }
                CounterState.Require(!SourceGitCleanupRefused && SourceGitQuarantine is null, "Source helper cleanup remains refused");
                var info = new ProcessStartInfo("git")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                info.ArgumentList.Add("-C"); info.ArgumentList.Add(pin.Repository);
                foreach (string argument in args) info.ArgumentList.Add(argument);
                // Retained before attempted birth. No using/automatic disposal of an uncertain child.
                var lease = new SourceGitLease(info, finite.Token);
                Exception? primary = null;
                try { return await lease.RunAsync(); }
                catch (Exception error) { primary = error; throw; }
                finally
                {
                    bool closed = false;
                    try { closed = await lease.CleanupAsync(); }
                    catch (Exception) { lease.RefuseCleanup(); }
                    if (!closed || lease.CleanupRefused)
                    {
                        SourceGitCleanupRefused = true;
                        if (!closed) SourceGitQuarantine = lease;
                        if (primary is not null) primary.Data["SourceGitCleanupVerified"] = false;
                        else throw new InvalidDataException("Source helper cleanup refused");
                    }
                }
            }
            finally { SourceGitGate.Release(); }
        }
        CounterState.Require(await Git("rev-parse", "HEAD") == pin.SourceSha && await Git("rev-parse", "HEAD^{tree}") == pin.SourceTree
            && await Git("status", "--porcelain", "--untracked-files=all") == "", "Actual clean selected source differs");
    }

    // Concrete ownership for these three read-only Git commands; not a general process runner.
    private sealed class SourceGitLease
    {
        private readonly Process process;
        private readonly CancellationToken operation;
        private readonly CancellationTokenSource readers;
        private SafeProcessHandle? processHandle;
        private SafeFileHandle? pidfd;
        private StreamReader? stdout, stderr;
        private StreamWriter? stdin;
        private SafeHandle? stdoutHandle, stderrHandle, stdinHandle;
        private Task<string>? outputTask, errorTask;
        private Task? exitTask, cleanupExitTask, outputDrainTask, errorDrainTask;
        private (ulong Ticks, int Parent)? kernel;
        private long? native;
        private bool attempted, bound, outputEof, errorEof, closed, exitVerified;
        internal bool CleanupRefused { get; private set; }

        internal SourceGitLease(ProcessStartInfo info, CancellationToken operation)
        {
            process = new Process { StartInfo = info };
            this.operation = operation;
            readers = CancellationTokenSource.CreateLinkedTokenSource(operation);
        }

        internal void RefuseCleanup() => CleanupRefused = true;

        private static SafeHandle Handle(Stream stream) => stream switch
        {
            FileStream file => file.SafeFileHandle,
            PipeStream pipe => pipe.SafePipeHandle,
            _ => throw new InvalidDataException("Owned source pipe handle unavailable")
        };

        private void CaptureStreams()
        {
            stdout ??= process.StandardOutput; stdoutHandle ??= Handle(stdout.BaseStream);
            stderr ??= process.StandardError; stderrHandle ??= Handle(stderr.BaseStream);
            stdin ??= process.StandardInput; stdinHandle ??= Handle(stdin.BaseStream);
        }

        private async Task<string> BoundedText(StreamReader reader, bool output)
        {
            var text = new StringBuilder(); char[] buffer = new char[4096]; int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), readers.Token)) != 0)
            {
                CounterState.Require(text.Length + length <= 65536, "Source observation output exceeded bound");
                text.Append(buffer, 0, length);
            }
            if (output) outputEof = true; else errorEof = true;
            return text.ToString();
        }

        private bool Exited()
        {
            if (exitVerified) return true;
            process.Refresh();
            exitVerified = process.HasExited; // Exact retained child WaitState, not /proc PID absence.
            return exitVerified;
        }

        private async Task BindLiveAsync(CancellationToken token)
        {
            if (Exited()) return;
            processHandle ??= process.SafeHandle; // Linux pseudo-handle retained, never used to signal.
            if (Exited()) return;
            try
            {
                var observed = await KernelAsync(process.Id, token);
                long observedNative = process.StartTime.ToUniversalTime().Ticks;
                if (Exited()) return;
                CounterState.Require(observed.Parent == System.Environment.ProcessId
                    && (!kernel.HasValue || kernel.Value == observed) && (!native.HasValue || native.Value == observedNative), "Owned source generation differs");
                kernel ??= observed; native ??= observedNative;
                if (pidfd is null)
                {
                    int descriptor = pidfd_open(process.Id, 0);
                    if (descriptor < 0)
                    {
                        if (Exited()) return;
                        throw new InvalidDataException("Source pidfd unavailable");
                    }
                    pidfd = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                }
                // Runtime may reap asynchronously. Never signal a descriptor acquired after original exit.
                if (Exited()) return;
                var after = await KernelAsync(process.Id, token);
                long afterNative = process.StartTime.ToUniversalTime().Ticks;
                if (Exited()) return;
                CounterState.Require(after == kernel.Value && afterNative == native.Value && !pidfd.IsInvalid && !pidfd.IsClosed,
                    "Source pidfd generation association differs");
                bound = true;
            }
            catch (Exception)
            {
                if (Exited()) return; // Fast natural exit uses retained exit + both actual EOFs, no signal.
                throw;
            }
        }

        internal async Task<string> RunAsync()
        {
            CounterState.Require(OperatingSystem.IsLinux(), "Hosted Linux source helper required");
            operation.ThrowIfCancellationRequested();
            attempted = true;
            CounterState.Require(process.Start(), "Source read failed");
            CaptureStreams();
            stdin!.Dispose(); // Owned EOF: these read-only commands consume no interactive input.
            outputTask = BoundedText(stdout!, output: true);
            errorTask = BoundedText(stderr!, output: false);
            exitTask = process.WaitForExitAsync(readers.Token);
            await BindLiveAsync(operation);
            var pending = new List<Task> { outputTask, errorTask, exitTask };
            while (pending.Count != 0)
            {
                // A failed/overflowed reader stops observation immediately, before a blocked writer can deadlock.
                Task completed = await Task.WhenAny(pending).WaitAsync(operation);
                await completed; pending.Remove(completed);
            }
            string text = await outputTask; string discarded = await errorTask;
            CounterState.Require(Exited() && outputEof && errorEof && process.ExitCode == 0
                && text.Length <= 65536 && discarded.Length <= 65536, "Bounded actual git observation required");
            return text.TrimEnd('\r', '\n');
        }

        private async Task DrainAsync(StreamReader reader, bool output, CancellationToken token)
        {
            char[] buffer = new char[4096]; int total = 0, length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
                CounterState.Require((total += length) <= 65536, "Source cleanup drain exceeded bound");
            if (output) outputEof = true; else errorEof = true;
        }

        private async Task QuiesceAsync(Task? task, StreamReader? reader, bool output, CancellationToken token)
        {
            if (task is not null)
            {
                try { await task.WaitAsync(token); }
                catch (Exception) { if (!task.IsCompleted) throw; }
                CounterState.Require(task.IsCompleted, "Source reader remains active");
            }
            if (!(output ? outputEof : errorEof))
            {
                CounterState.Require(reader is not null, "Source reader ownership unavailable");
                Task? draining = output ? outputDrainTask : errorDrainTask;
                if (draining is null || draining.IsCompleted)
                {
                    // Observe a completed failure before releasing its last retained task reference.
                    if (draining?.IsFaulted == true) _ = draining.Exception;
                    // A completed failed drain can be retried; an in-flight read is retained and never overlapped.
                    draining = DrainAsync(reader!, output, token);
                    if (output) outputDrainTask = draining; else errorDrainTask = draining;
                }
                await draining.WaitAsync(token);
            }
        }

        internal async Task<bool> CleanupAsync()
        {
            if (closed) return true;
            if (!attempted)
            {
                // No Start invocation occurred: this is the only unassociated no-child disposal case.
                try { process.Dispose(); } catch (Exception) { RefuseCleanup(); }
                try { readers.Dispose(); } catch (Exception) { RefuseCleanup(); }
                closed = !CleanupRefused;
                return closed;
            }
            using var finite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                // Capture every redirected stream independently, including recovery after Start/readersetup throw.
                try { stdout ??= process.StandardOutput; stdoutHandle ??= Handle(stdout.BaseStream); } catch (Exception) { RefuseCleanup(); }
                try { stderr ??= process.StandardError; stderrHandle ??= Handle(stderr.BaseStream); } catch (Exception) { RefuseCleanup(); }
                try { stdin ??= process.StandardInput; stdinHandle ??= Handle(stdin.BaseStream); } catch (Exception) { RefuseCleanup(); }
                try { stdin?.Dispose(); } catch (Exception) { RefuseCleanup(); }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (Exited()) break;
                        await BindLiveAsync(finite.Token);
                        if (Exited()) break;
                        CounterState.Require(bound && pidfd is not null && !pidfd.IsClosed, "Source live signal ownership unavailable");
                        if (pidfd_send_signal(pidfd!, 9, IntPtr.Zero, 0) != 0)
                            CounterState.Require(Marshal.GetLastPInvokeError() == 3 && Exited(), "Source pidfd signal failed");
                        break;
                    }
                    catch (Exception) { RefuseCleanup(); }
                }
                bool exited = false;
                try
                {
                    if (cleanupExitTask is null || (cleanupExitTask.IsCompleted && !cleanupExitTask.IsCompletedSuccessfully))
                    {
                        if (cleanupExitTask?.IsFaulted == true) _ = cleanupExitTask.Exception;
                        cleanupExitTask = process.WaitForExitAsync();
                    }
                    await cleanupExitTask.WaitAsync(finite.Token);
                    exited = Exited();
                }
                catch (Exception) { RefuseCleanup(); }
                try { readers.Cancel(); } catch (Exception) { RefuseCleanup(); }
                try { await QuiesceAsync(outputTask, stdout, output: true, finite.Token); } catch (Exception) { RefuseCleanup(); }
                try { await QuiesceAsync(errorTask, stderr, output: false, finite.Token); } catch (Exception) { RefuseCleanup(); }
                bool tasksDone = (outputTask is null || outputTask.IsCompleted) && (errorTask is null || errorTask.IsCompleted)
                    && (exitTask is null || exitTask.IsCompleted) && (cleanupExitTask is null || cleanupExitTask.IsCompleted)
                    && (outputDrainTask is null || outputDrainTask.IsCompleted) && (errorDrainTask is null || errorDrainTask.IsCompleted);
                if (!attempted || !exited || !tasksDone || !outputEof || !errorEof || stdoutHandle is null || stderrHandle is null || stdinHandle is null)
                { RefuseCleanup(); return false; }
                foreach (var task in new[] { outputTask, errorTask, exitTask, cleanupExitTask, outputDrainTask, errorDrainTask })
                    if (task?.IsFaulted == true) _ = task.Exception;
                // Only a positively exited child and independently quiescent/EOF readers permit handle disposal.
                try { stdout!.Dispose(); } catch (Exception) { RefuseCleanup(); }
                try { stderr!.Dispose(); } catch (Exception) { RefuseCleanup(); }
                try { pidfd?.Dispose(); } catch (Exception) { RefuseCleanup(); }
                try { process.Dispose(); } catch (Exception) { RefuseCleanup(); }
                try { readers.Dispose(); } catch (Exception) { RefuseCleanup(); }
                closed = stdoutHandle.IsClosed && stderrHandle.IsClosed && stdinHandle.IsClosed
                    && (pidfd is null || pidfd.IsClosed) && (processHandle is null || processHandle.IsClosed);
                if (!closed) RefuseCleanup();
                return closed;
            }
            finally
            {
                try { finite.Cancel(); } catch (Exception) { RefuseCleanup(); }
            }
        }

        [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
        [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(SafeFileHandle pidfd, int signal, IntPtr info, uint flags);
    }
}
