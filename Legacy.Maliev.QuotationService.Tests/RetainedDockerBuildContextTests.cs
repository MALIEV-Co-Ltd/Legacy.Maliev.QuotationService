using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests;

/// <summary>Exercises retained ignore rules through Docker's actual scratch COPY boundary.</summary>
/// <param name="output">Receives proof counts and owned command cleanup metadata.</param>
public sealed class RetainedDockerBuildContextTests(ITestOutputHelper output)
{
    /// <summary>Excludes original root/nested categories while preserving admitted build inputs byte for byte.</summary>
    [Fact]
    public async Task ActualDockerCopyRetainsProductionInputsAndExcludesAllOriginalCategories()
    {
        var endpoint = await RequireLocalDockerAsync();
        var buildx = FindBuildx();
        output.WriteLine((await CommandAsync(buildx, endpoint, "version")).Trim());
        var inspection = await CommandAsync(buildx, endpoint, "inspect", "default");
        Assert.True(IsDefaultDockerBuilderInspection(inspection),
            "Only the unambiguous default Docker builder is admitted; raw inspection withheld.");
        var root = FindRoot();
        var parent = Path.Combine(root, "TestResults", "retained-docker-context");
        var owned = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var input = Path.Combine(owned, "input");
        var destination = Path.Combine(owned, "output");
        var directoryExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        Exception? primary = null;
        try
        {
            Directory.CreateDirectory(input);
            output.WriteLine(JsonSerializer.Serialize(new { owned, purpose = "retained Docker context proof", expires = directoryExpiry, persistentData = false }));
            File.Copy(Path.Combine(root, ".dockerignore"), Path.Combine(input, ".dockerignore"));
            await File.WriteAllTextAsync(Path.Combine(input, "Dockerfile"), "FROM scratch\nCOPY . /context/\n");
            string[] included =
            [
                "nuget.config",
                "Directory.Build.props",
                ".dependencies/Legacy.Maliev.ServiceDefaults/Directory.Build.props",
                ".dependencies/Legacy.Maliev.ServiceDefaults/global.json",
                ".dependencies/Legacy.Maliev.ServiceDefaults/nuget.config",
                ".dependencies/Legacy.Maliev.ServiceDefaults/README.md",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/Directory.Build.props",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/global.json",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/nuget.config",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/README.md",
            ];
            string[] directories =
            [
                "Legacy.Maliev.QuotationService.Api",
                "Legacy.Maliev.QuotationService.Application",
                "Legacy.Maliev.QuotationService.Domain",
                "Legacy.Maliev.QuotationService.Data",
                "Legacy.Maliev.QuotationService.MigrationRunner",
                ".dependencies/Legacy.Maliev.ServiceDefaults/src",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/src",
            ];
            included = included.Concat(directories.SelectMany(directory =>
                    Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories))
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !path.Split('/').Any(segment => segment is "bin" or "obj" or ".git"))
                .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".resx" or ".json" or ".xml" or ".props" or ".targets"))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var path in included)
            {
                var target = Path.Combine(input, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(root, path), target);
            }

            string[] categories =
            [
                ".git/config", ".gitignore", ".github/workflow.yml", ".vscode/settings.json",
                ".vs/state", ".idea/workspace.xml", "bin/output.dll", "obj/generated.cs",
                "out/output.txt", "node_modules/package/index.js", "dist/bundle.js",
                "coverage/data.xml", "TestResults/result.trx", "private.suo", "private.user",
                "private.csproj.user", "private.cache", "private.log", "private.tmp",
                "private.nupkg", "private.swp", "private.swo", "private.env", ".env",
                ".env.synthetic", "private.ps1", "private.cmd", "private.sh",
                "private.yml", "private.yaml",
            ];
            string[] excluded =
            [
                .. categories,
                .. categories.Select(path => ".dependencies/Legacy.Maliev.ServiceDefaults/" + path),
                ".dependencies/Legacy.Maliev.ServiceDefaults/nested/Dockerfile.dockerignore",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/nested/Dockerfile.dockerignore",
                ".dependencies/Legacy.Maliev.ServiceDefaults/runtime.env",
                ".dependencies/Legacy.Maliev.CompatibilityContracts/.git/config",
                "Legacy.Maliev.QuotationService.Api/bin/synthetic.txt",
                "Legacy.Maliev.QuotationService.Api/obj/synthetic.txt",
                "private.pfx", "private.key", "private.pem",
            ];
            foreach (var path in excluded)
            {
                var target = Path.Combine(input, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, "SYNTHETIC_CONTEXT_EXCLUSION_SENTINEL");
            }
            // A Dockerfile.dockerignore beside the scratch Dockerfile would replace the
            // root policy. Its two fixtures are deliberately nested instead.
            await CommandAsync(buildx, endpoint, "build", "--builder", "default", "--network=none", "--no-cache", "--progress=quiet",
                "--output", $"type=local,dest={destination}", "--file", Path.Combine(input, "Dockerfile"), input);
            foreach (var path in included)
            {
                var expected = await File.ReadAllBytesAsync(Path.Combine(input, path));
                var actual = await File.ReadAllBytesAsync(Path.Combine(destination, "context", path));
                Assert.True(expected.AsSpan().SequenceEqual(actual), "Admitted Docker input bytes changed.");
            }
            var leaked = excluded.Where(path => File.Exists(Path.Combine(destination, "context", path))).ToArray();
            Assert.True(leaked.Length == 0, $"Excluded synthetic paths reached COPY: {string.Join(", ", leaked)}");
            output.WriteLine($"Actual Docker context: {included.Length} admitted files, {excluded.Length} excluded sentinels.");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            void RetainDirectory(string reason)
            {
                var owner = new
                {
                    owned,
                    purpose = "retained Docker context proof directory",
                    expires = directoryExpiry,
                    commandRuns = retainedCommandRuns.ToArray(),
                    reason,
                    persistentData = false,
                    reobserveBeforeRecovery = true,
                };
                RetainedDirectories[owned] = owner;
                try { output.WriteLine(JsonSerializer.Serialize(owner)); }
                catch (Exception) { }
                if (primary is not null)
                {
                    try { primary.Data["RetainedDockerDirectoryOwner"] = owner; }
                    catch (Exception) { }
                }
            }
            try
            {
                if (retainedCommandRuns.Count != 0)
                    RetainDirectory("Exact command or readers remain in retained custody; directory deletion refused.");
                else
                {
                    var boundary = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(owned).StartsWith(boundary, StringComparison.Ordinal))
                        throw new InvalidOperationException("Owned context directory escaped its declared boundary.");
                    if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
                    Assert.False(Directory.Exists(owned));
                }
            }
            catch (Exception error)
            {
                RetainDirectory("Directory cleanup failed; retained for exact recovery.");
                if (primary is null) throw;
                try { primary.Data["RetainedDockerDirectoryCleanup"] = error; }
                catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Admits only unambiguous default-builder Docker driver headers from supported buildx inspect output.
    /// </summary>
    /// <param name="inspection">The bounded synthetic inspect output, including header and node-section controls.</param>
    /// <param name="allowed">Whether the top-level builder identity and driver are admitted.</param>
    [Theory]
    [InlineData("Name:    default\nDriver:  docker\n\nNodes:\nName: another-node\nEndpoint: unix:///var/run/docker.sock\n", true)]
    [InlineData("Name:\tdefault\r\nDriver:\tdocker\r\nLast Activity: 2026-10-22 00:00:00 +0000 UTC\r\n\r\nNodes:\r\nName: default\r\n", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Name: other\nDriver: docker\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: docker-container\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: remote\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: Docker\nNodes:\n", false)]
    [InlineData("Name: default *\nDriver: docker\nNodes:\n", false)]
    [InlineData("Name: default\nName: default\nDriver: docker\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: docker\nDriver: docker\nNodes:\n", false)]
    [InlineData("Driver: docker\nNodes:\nName: default\n", false)]
    [InlineData("Name: default\nNodes:\nDriver: docker\n", false)]
    [InlineData("Nodes:\nName: default\nDriver: docker\n", false)]
    [InlineData("Name: default\nDriver: docker\n", false)]
    [InlineData("Name: default\nDriver: docker\nError: synthetic status failure\n", false)]
    [InlineData("Name: default\nDriver: docker\nError: synthetic status failure\nNodes:\n", false)]
    [InlineData(" Name: default\n Driver: docker\nNodes:\n", false)]
    [InlineData("Name: default\nDriver : docker\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: docker\nNodes: not-a-section\n", false)]
    [InlineData("Name: default\nDriver: docker\u001b\nNodes:\n", false)]
    [InlineData("Name: default\nDriver: docker\nUnexpected: value\nNodes:\n", false)]
    public void BuildxInspection_RequiresUnambiguousTopLevelDefaultDocker(string? inspection, bool allowed)
    {
        Assert.Equal(allowed, IsDefaultDockerBuilderInspection(inspection));
    }

    /// <summary>
    /// Refuses an oversized inspect result even when its prefix contains otherwise valid builder headers.
    /// </summary>
    [Fact]
    public void BuildxInspection_RefusesOutputBeyondExistingCommandBudget()
    {
        var inspection = "Name: default\nDriver: docker\nNodes:\n" + new string('x', 16384);
        Assert.False(IsDefaultDockerBuilderInspection(inspection));
    }

    private static bool IsDefaultDockerBuilderInspection(string? inspection)
    {
        if (string.IsNullOrEmpty(inspection) || inspection.Length > 16384
            || inspection.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            return false;

        string? name = null;
        string? driver = null;
        foreach (var rawLine in inspection.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line == "Nodes:") return name == "default" && driver == "docker";
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                if (name is not null) return false;
                name = line["Name:".Length..].Trim(' ', '\t');
            }
            else if (line.StartsWith("Driver:", StringComparison.Ordinal))
            {
                if (driver is not null) return false;
                driver = line["Driver:".Length..].Trim(' ', '\t');
            }
            else if (line.StartsWith("Error:", StringComparison.Ordinal)
                || line.Length != 0 && !line.StartsWith("Last Activity:", StringComparison.Ordinal))
            {
                return false;
            }
        }
        return false;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.QuotationService.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository test root unavailable.");
    }

    private async Task<string> RequireLocalDockerAsync()
    {
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrEmpty(host)) Assert.True(IsLocal(host), "Remote Docker endpoint refused.");
        var context = (await DockerAsync("context", "show")).Trim();
        using var document = JsonDocument.Parse(await DockerAsync("context", "inspect", context));
        var endpoint = document.RootElement[0].GetProperty("Endpoints").GetProperty("docker").GetProperty("Host").GetString();
        Assert.True(IsLocal(endpoint), "Non-local Docker context refused.");
        return string.IsNullOrEmpty(host) ? endpoint! : host;
    }

    private static string FindBuildx()
    {
        var dockerConfig = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        if (string.IsNullOrEmpty(dockerConfig))
            dockerConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        var file = OperatingSystem.IsWindows() ? "docker-buildx.exe" : "docker-buildx";
        string[] candidates =
        [
            Path.Combine(dockerConfig, "cli-plugins", file),
            "/usr/libexec/docker/cli-plugins/docker-buildx",
            "/usr/lib/docker/cli-plugins/docker-buildx",
            "/usr/local/lib/docker/cli-plugins/docker-buildx",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "resources", "cli-plugins", file),
        ];
        return candidates.FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path))
            ?? throw new FileNotFoundException("A standalone installed Docker buildx executable is required.");
    }

    private static bool IsLocal(string? endpoint)
    {
        const string prefix = "npipe:////./pipe/";
        return endpoint is not null && endpoint.StartsWith(prefix, StringComparison.Ordinal)
            && endpoint.Length > prefix.Length
            && endpoint[prefix.Length..].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            || endpoint is "unix:///var/run/docker.sock" or "unix:///run/docker.sock";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> RetainedCommands = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> RetainedDirectories = new();
    private readonly List<string> retainedCommandRuns = [];

    private Task<string> DockerAsync(params string[] arguments) => CommandAsync("docker", null, arguments);

    private async Task<string> CommandAsync(string executable, string? endpoint, params string[] arguments)
    {
        var run = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (endpoint is not null)
        {
            // Invoke buildx directly: no unowned Docker-to-plugin subprocess chain.
            process.StartInfo.Environment["DOCKER_HOST"] = endpoint;
            process.StartInfo.Environment.Remove("DOCKER_CONTEXT");
            process.StartInfo.Environment.Remove("DOCKER_CLI_PLUGIN_ORIGINAL_CLI_COMMAND");
        }
        var work = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string>? stdout = null, stderr = null;
        CommandIdentity? identity = null;
        int? pid = null;
        Microsoft.Win32.SafeHandles.SafeProcessHandle? handle = null;
        Exception? primary = null;
        var cleanup = new List<Exception>();
        var started = false;
        try
        {
            var memory = File.Exists("/proc/meminfo")
                ? File.ReadLines("/proc/meminfo")
                    .Where(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal)).ToArray()
                : Array.Empty<string>();
            var fields = memory.Length == 1
                ? memory[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            Assert.True(fields.Length == 3 && fields[0] == "MemAvailable:" && fields[2] == "kB"
                && long.TryParse(fields[1], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var availableKiB)
                && availableKiB >= 4194304,
                "Docker command memory admission failed; setup failure.");
            started = process.Start();
            if (!started) throw new InvalidOperationException("Owned Docker command did not start.");
            handle = process.SafeHandle;
            pid = process.Id;
            // Fast normal commands may exit before executable observation. An absent initial
            // identity never authorizes a signal; an actual normal exit needs no signal.
            try { identity = Observe(process); }
            catch (Exception) { }
            stdout = ReadBoundedAsync(process.StandardOutput, work.Token);
            stderr = ReadBoundedAsync(process.StandardError, work.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(work.Token)).WaitAsync(work.Token);
            Assert.True(process.ExitCode == 0, $"Docker context command failed, exit {process.ExitCode}; raw output withheld.");
            return await stdout;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { cleanup.Add(error); }
            }
            bool Verify()
            {
                if (process.HasExited) return false;
                if (identity is null || handle is null || handle.IsInvalid || handle.IsClosed
                    || !ReferenceEquals(process.SafeHandle, handle))
                    throw new InvalidOperationException("Owned Docker command identity unproved; signal refused.");
                try
                {
                    if (Observe(process) != identity)
                        throw new InvalidOperationException("Owned Docker command identity changed; signal refused.");
                }
                catch (Exception) when (process.HasExited) { return false; }
                return !process.HasExited;
            }
            try
            {
                if (started && !process.HasExited)
                {
                    if (Verify())
                    {
                        if (OperatingSystem.IsLinux()) _ = SendSignal(identity!.Pid, 2);
                        else _ = process.CloseMainWindow();
                        if (!process.WaitForExit(250) && Verify())
                            process.Kill(); // Exact retained process only; never a name or process tree.
                    }
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(stop.Token);
                }
            }
            catch (Exception error) { cleanup.Add(error); }
            Attempt(work.Cancel);
            if (started)
            {
                Attempt(() => process.StandardOutput.Dispose());
                Attempt(() => process.StandardError.Dispose());
            }
            var readers = new[] { stdout, stderr }.OfType<Task>().ToArray();
            try { await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error)
            {
                if (readers.Any(task => !task.IsCompleted)) cleanup.Add(error);
            }
            var exited = !started;
            if (started) Attempt(() => exited = process.HasExited);
            var settled = readers.All(task => task.IsCompleted);
            var processClosed = false;
            var lifetimeClosed = false;
            if (exited && settled)
            {
                Attempt(() => { process.Dispose(); processClosed = true; });
                Attempt(() => { work.Dispose(); lifetimeClosed = true; });
            }
            var released = exited && settled && processClosed && lifetimeClosed;
            if (!released)
            {
                RetainedCommands[run] = new
                {
                    process,
                    work,
                    stdout,
                    stderr,
                    identity,
                    handle,
                    expires,
                    purpose = "retained Docker context proof command",
                    arguments = arguments.ToArray(),
                };
                retainedCommandRuns.Add(run);
                cleanup.Add(new InvalidOperationException("Owned Docker command custody retained; re-observation required."));
            }
            Attempt(() => output.WriteLine(JsonSerializer.Serialize(new
            {
                run,
                purpose = "retained Docker context proof",
                pid,
                identity,
                expires,
                exited,
                readersSettled = settled,
                handleClosed = handle is null || handle.IsClosed,
                retained = !released,
                persistentData = false,
            })));
            if (cleanup.Count != 0)
            {
                var aggregate = new AggregateException("Owned Docker command cleanup failed.", cleanup);
                if (primary is null) throw aggregate;
                try { primary.Data["RetainedDockerCommandCleanup"] = aggregate; }
                catch (Exception) { }
            }
        }
    }

    private sealed record CommandIdentity(int Pid, DateTime StartedUtc, string Executable);

    private static CommandIdentity Observe(Process process)
    {
        // A new observer avoids the original Process object's cached birth/module data.
        using var current = Process.GetProcessById(process.Id);
        return new(current.Id, current.StartTime.ToUniversalTime(),
            current.MainModule?.FileName ?? throw new InvalidOperationException("Executable identity unavailable."));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > 16384)
                throw new InvalidOperationException("Docker proof output budget exceeded; raw output withheld.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
