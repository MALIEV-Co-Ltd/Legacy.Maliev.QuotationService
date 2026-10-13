using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

internal interface IStartupResource
{
    Task StartAsync(CancellationToken token);
    Task DisposeAndVerifyAsync();
}

internal static class DisposableContainerStartup
{
    internal static bool IsCollision(DockerApiException error)
    {
        if (error.StatusCode != HttpStatusCode.InternalServerError || string.IsNullOrEmpty(error.ResponseBody)
            || error.ResponseBody.Length > 64 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(error.ResponseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count(x => x.NameEquals("message")) != 1
                || !document.RootElement.TryGetProperty("message", out var property) || property.ValueKind != JsonValueKind.String) return false;
            var message = property.GetString()!;
            if (!message.StartsWith("failed to set up container networking: driver failed programming external connectivity", StringComparison.Ordinal)) return false;
            if (message.EndsWith(": failed to listen on TCP socket: address already in use", StringComparison.Ordinal)) return true;
            // Exact hosted Engine EADDRINUSE form; do not retry generic networking or allocation errors.
            const string bindPattern = @"\Afailed to set up container networking: driver failed programming external connectivity on endpoint "
                + @"[A-Za-z0-9][A-Za-z0-9_.-]{0,127} \([a-f0-9]{64}\): failed to bind host port for 0\.0\.0\.0::"
                + @"(?<target>[0-9]{1,3}(?:\.[0-9]{1,3}){3}):(?<port>[1-9][0-9]{0,4})/tcp: address already in use\z";
            var match = Regex.Match(message, bindPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success && IPAddress.TryParse(match.Groups["target"].Value, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && address.ToString() == match.Groups["target"].Value
                && int.TryParse(match.Groups["port"].Value, out var port) && port <= 65535;
        }
        catch (JsonException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static async Task<IStartupResource> StartAsync(Func<IStartupResource> create,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token = default, TimeSpan? cleanupBudget = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var resource = create();
            Task? startup = null;
            try
            {
                startup = resource.StartAsync(token);
                await startup.WaitAsync(token);
                return resource;
            }
            catch (Exception error)
            {
                var cleanup = CleanupAfterStartupAsync();
                await ContainerOperationCustody.CleanupPreservingAsync(error, cleanup,
                    cleanupBudget ?? TimeSpan.FromSeconds(15), "container-startup-cleanup", [resource, startup!]);
                if (!cleanup.IsCompletedSuccessfully || token.IsCancellationRequested) throw;
                if (attempt >= 3 || error is not DockerApiException docker || !IsCollision(docker)) throw;
                await delay(TimeSpan.FromMilliseconds(100 * attempt), token);

                async Task CleanupAfterStartupAsync()
                {
                    if (startup is not null)
                    {
                        try { await startup; }
                        catch (Exception failure) { ContainerOperationCustody.RecordFailures(error, "maliev.startup.eventual-failures", failure); }
                    }
                    await resource.DisposeAndVerifyAsync();
                }
            }
        }
    }

    internal static async Task<(IStartupResource First, IStartupResource Second)> StartPairAsync(
        Func<IStartupResource> first, Func<IStartupResource> second,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token = default, TimeSpan? cleanupBudget = null)
    {
        var siblings = CancellationTokenSource.CreateLinkedTokenSource(token);
        var resources = new List<IStartupResource>();
        var cancellationTasks = new List<Task>();
        void TrackCancellation(Task actual) { lock (cancellationTasks) cancellationTasks.Add(actual); }
        Exception? startupFailure = null;
        IStartupResource Record(Func<IStartupResource> factory)
        {
            var resource = new StartupResourceLifetime(factory());
            lock (resources) resources.Add(resource);
            return resource;
        }
        async Task<IStartupResource> Run(Func<IStartupResource> factory)
        {
            try { return await StartAsync(() => Record(factory), delay, siblings.Token, cleanupBudget); }
            catch (Exception failure)
            {
                Interlocked.CompareExchange(ref startupFailure, failure, null);
                await ContainerOperationCustody.CancelPreservingAsync(siblings, failure,
                    cleanupBudget ?? TimeSpan.FromSeconds(15), TrackCancellation);
                throw;
            }
        }
        var firstTask = Run(first);
        var secondTask = Run(second);
        var startup = Task.WhenAll(firstTask, secondTask);
        Task? cleanupLifetime = null;
        try
        {
            await startup.WaitAsync(siblings.Token);
            return (((StartupResourceLifetime)firstTask.Result).Resource, ((StartupResourceLifetime)secondTask.Result).Resource);
        }
        catch (Exception failure)
        {
            var primary = startupFailure ?? failure;
            var cleanup = CleanupAfterSiblingsAsync();
            cleanupLifetime = cleanup;
            await ContainerOperationCustody.CancelPreservingAsync(siblings, primary,
                cleanupBudget ?? TimeSpan.FromSeconds(15), TrackCancellation);
            await ContainerOperationCustody.CleanupPreservingAsync(primary, cleanup,
                cleanupBudget ?? TimeSpan.FromSeconds(15), "container-pair-cleanup", [resources, firstTask, secondTask]);
            ExceptionDispatchInfo.Capture(primary).Throw();
            throw; // Unreachable, required for tuple-return flow analysis.

            async Task CleanupAfterSiblingsAsync()
            {
                try { await startup; }
                catch { /* Observe both original tasks before independent owner cleanup. */ }
                IStartupResource[] allocated;
                lock (resources) allocated = resources.ToArray();
                var removals = Task.WhenAll(allocated.Select(async resource =>
                {
                    try { await resource.DisposeAndVerifyAsync(); }
                    finally
                    {
                        if (resource is StartupResourceLifetime { StartupFailure: { } late })
                            ContainerOperationCustody.RecordFailures(primary, "maliev.startup.eventual-failures", late);
                    }
                }));
                try { await removals; }
                catch
                {
                    // Await unwraps one fault; the settled aggregate retains every independently attempted owner failure.
                    if (removals.Exception is { } aggregate)
                    {
                        ContainerOperationCustody.RecordFailures(primary, "maliev.cleanup.failures",
                            aggregate.Flatten().InnerExceptions.ToArray());
                        ExceptionDispatchInfo.Capture(aggregate).Throw();
                    }
                    throw;
                }
            }
        }
        finally
        {
            Task[] callbacks;
            lock (cancellationTasks) callbacks = cancellationTasks.ToArray();
            var actualLifetime = Task.WhenAll(new[] { cleanupLifetime ?? startup }.Concat(callbacks));
            if (actualLifetime.IsCompleted)
            {
                _ = actualLifetime.Exception; // Observe the aggregate, including callback faults.
                siblings.Dispose();
            }
            else ContainerOperationCustody.Retain(actualLifetime, "container-pair-cancellation-source-release",
                [siblings, resources], siblings.Dispose);
        }
    }

    private sealed class StartupResourceLifetime(IStartupResource resource) : IStartupResource
    {
        internal IStartupResource Resource => resource;
        internal Exception? StartupFailure { get; private set; }
        private readonly object gate = new();
        private Task? startup;
        private Task? cleanup;
        public Task StartAsync(CancellationToken token)
        {
            lock (gate) return startup ??= resource.StartAsync(token);
        }
        public Task DisposeAndVerifyAsync()
        {
            lock (gate) return cleanup ??= DisposeAfterStartupAsync();
        }
        private async Task DisposeAfterStartupAsync()
        {
            if (startup is not null)
            {
                try { await startup; }
                catch (Exception failure) { StartupFailure = failure; }
            }
            await resource.DisposeAndVerifyAsync();
        }
    }

    internal static string NormalizeLocalEndpoint(string endpoint)
    {
        const string rawPrefix = "npipe:////./pipe/";
        if (endpoint.StartsWith(rawPrefix, StringComparison.Ordinal)) endpoint = "npipe://./pipe/" + endpoint[rawPrefix.Length..];
        const string prefix = "npipe://./pipe/";
        if (endpoint == "unix:///var/run/docker.sock"
            || endpoint.StartsWith(prefix, StringComparison.Ordinal)
                && Regex.IsMatch(endpoint[prefix.Length..], "\\A[A-Za-z0-9_.-]+\\z", RegexOptions.CultureInvariant)) return endpoint;
        throw new InvalidOperationException("Only a canonical local Docker socket or named pipe is allowed.");
    }

    internal static async Task<string> LocalDockerEndpointAsync(CancellationToken token = default)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_CONTEXT")))
            throw new InvalidOperationException("Ambient Docker host/context overrides are forbidden.");
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "context", "inspect", "--format", "{{json .Endpoints.docker.Host}}" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker context inspection could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, timeout.Token);
        var exited = process.WaitForExitAsync(timeout.Token);
        try
        {
            var pending = new List<Task> { stdout, stderr, exited };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed; // Observe overflow immediately, without waiting for the other pipe/process.
                pending.Remove(completed);
            }
        }
        catch
        {
            await timeout.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Only this owned inspection process.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* Pipe tasks settled or bounded; preserve the original fixed/redacted failure. */ }
            throw;
        }
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker context inspection failed.");
        return NormalizeLocalEndpoint(JsonSerializer.Deserialize<string>(await stdout)?.Trim()
            ?? throw new InvalidOperationException("Docker context endpoint is absent."));
    }

    internal static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken token = default)
    {
        const int budget = 16 * 1024;
        var bytes = new byte[budget + 1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), token);
            if (read == 0) return Encoding.UTF8.GetString(bytes, 0, count);
            count += read;
            if (count > budget) throw new IOException("Docker context output exceeded the 16 KiB budget.");
        }
    }
}

internal sealed record ContainerAttempt(Uri Endpoint, string Owner, string Run, string Resource, int Number)
{
    internal string Name => $"quotation97-{Run}-{Resource}-{Number}";
    internal Dictionary<string, string> Labels => new()
    {
        ["maliev.proof.owner"] = Owner,
        ["maliev.proof.run"] = Run,
        ["maliev.proof.resource"] = Resource,
        ["maliev.proof.attempt"] = Number.ToString()
    };
}

internal sealed class DisposableContainerPair : IAsyncDisposable
{
    private readonly DockerClient docker;
    private readonly OwnedContainer first;
    private readonly OwnedContainer second;
    private int clientReleased;
    private void ReleaseClient()
    {
        if (Interlocked.Exchange(ref clientReleased, 1) == 0) docker.Dispose();
    }
    private DisposableContainerPair(DockerClient docker, OwnedContainer first, OwnedContainer second)
    { this.docker = docker; this.first = first; this.second = second; }
    internal IContainer First => first.Container;
    internal IContainer Second => second.Container;

    internal static async Task<DisposableContainerPair> StartAsync(string owner,
        Func<ContainerAttempt, IContainer> postgres, Func<ContainerAttempt, IContainer> redis, CancellationToken token = default,
        string secondResource = "redis")
    {
        var endpoint = new Uri(await DisposableContainerStartup.LocalDockerEndpointAsync(token));
        var docker = new DockerClientBuilder().WithEndpoint(endpoint).Build();
        var run = Guid.NewGuid().ToString("N");
        var pgAttempt = 0;
        var redisAttempt = 0;
        OwnedContainer Create(string resource, int number, Func<ContainerAttempt, IContainer> factory)
        {
            var identity = new ContainerAttempt(endpoint, owner, run, resource, number);
            return new OwnedContainer(factory(identity), docker, identity);
        }
        try
        {
            var result = await DisposableContainerStartup.StartPairAsync(
                () => Create("pg", ++pgAttempt, postgres), () => Create(secondResource, ++redisAttempt, redis), Task.Delay, token);
            return new(docker, (OwnedContainer)result.First, (OwnedContainer)result.Second);
        }
        catch (Exception failure)
        {
            if (ContainerOperationCustody.CustodyFor(failure) is { } retained)
                ContainerOperationCustody.Retain(retained.Operation, "container-pair-client-release", [docker, retained], docker.Dispose);
            else docker.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cleanup = Task.WhenAll(first.DisposeAndVerifyAsync(), second.DisposeAndVerifyAsync());
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception failure)
        {
            ContainerOperationCustody.AttachCustody(failure, ContainerOperationCustody.Retain(cleanup,
                "container-pair-disposal-client-release", [docker, first, second], ReleaseClient));
            throw;
        }
        finally
        {
            if (cleanup.IsCompleted) ReleaseClient();
        }
    }

    internal sealed class OwnedContainer(IContainer container, DockerClient docker, ContainerAttempt identity) : IStartupResource
    {
        private bool removed;
        private Task? startup;
        private Task? disposal;
        private readonly object gate = new();
        public IContainer Container => container;
        public Task StartAsync(CancellationToken token)
        {
            lock (gate) return startup ??= container.StartAsync(token);
        }
        public async Task DisposeAndVerifyAsync()
        {
            Task actual;
            lock (gate) actual = disposal ??= DisposeCoreAsync();
            await actual;
        }

        private async Task DisposeCoreAsync()
        {
            if (removed) return;
            if (startup is not null)
            {
                try { await startup; }
                catch { /* Startup failure is preserved by its caller; actual startup must settle before removal. */ }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // Independent of canceled startup token.
            ContainerInspectResponse? inspect;
            try { inspect = await docker.Containers.InspectContainerAsync(identity.Name, timeout.Token); }
            catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { inspect = null; }
            if (inspect is not null)
            {
                if (inspect.Config?.Labels is not { } labels || identity.Labels.Any(pair => !labels.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw new InvalidOperationException("Disposable container ownership mismatch; removal refused.");
                await docker.Containers.RemoveContainerAsync(inspect.ID,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, timeout.Token);
            }
            // Name lookup also covers Docker-created resources for which SDK Id was never populated.
            try
            {
                await docker.Containers.InspectContainerAsync(identity.Name, timeout.Token);
                throw new InvalidOperationException("Disposable container removal was not verified.");
            }
            catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            await container.DisposeAsync(); // Actual task stays owned; pair bounds waiting without releasing its client early.
            removed = true;
        }
    }
}
