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
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var resource = create();
            try { await resource.StartAsync(token); return resource; }
            catch (Exception error)
            {
                await resource.DisposeAndVerifyAsync();
                token.ThrowIfCancellationRequested();
                if (attempt >= 3 || error is not DockerApiException docker || !IsCollision(docker)) throw;
                await delay(TimeSpan.FromMilliseconds(100 * attempt), token);
            }
        }
    }

    internal static async Task<(IStartupResource First, IStartupResource Second)> StartPairAsync(
        Func<IStartupResource> first, Func<IStartupResource> second,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token = default)
    {
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(token);
        var resources = new List<IStartupResource>();
        IStartupResource Record(Func<IStartupResource> factory)
        {
            var resource = factory();
            lock (resources) resources.Add(resource);
            return resource;
        }
        async Task<IStartupResource> Run(Func<IStartupResource> factory)
        {
            try { return await StartAsync(() => Record(factory), delay, siblings.Token); }
            catch { await siblings.CancelAsync(); throw; }
        }
        var firstTask = Run(first);
        var secondTask = Run(second);
        try
        {
            await Task.WhenAll(firstTask, secondTask);
            return (firstTask.Result, secondTask.Result);
        }
        catch
        {
            // Both tasks are settled; independently clean every allocated sibling, even when another cleanup fails.
            await Task.WhenAll(resources.Select(async x => await x.DisposeAndVerifyAsync()));
            var failure = new[] { firstTask, secondTask }.Where(x => x.IsFaulted)
                .SelectMany(x => x.Exception!.InnerExceptions).FirstOrDefault(x => x is not OperationCanceledException);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
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
        catch { docker.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Task.WhenAll(first.DisposeAndVerifyAsync(), second.DisposeAndVerifyAsync()); }
        finally { docker.Dispose(); }
    }

    internal sealed class OwnedContainer(IContainer container, DockerClient docker, ContainerAttempt identity) : IStartupResource
    {
        private bool removed;
        public IContainer Container => container;
        public Task StartAsync(CancellationToken token) => container.StartAsync(token);
        public async Task DisposeAndVerifyAsync()
        {
            if (removed) return;
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
            await container.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            removed = true;
        }
    }
}
