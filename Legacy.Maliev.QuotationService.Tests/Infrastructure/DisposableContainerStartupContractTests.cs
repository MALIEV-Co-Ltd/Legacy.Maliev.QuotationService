using System.Net;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;
using Xunit.Abstractions;
using StartupPrototype = Legacy.Maliev.QuotationService.Tests.Infrastructure.DisposableContainerStartup;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class DisposableContainerStartupContractTests(ITestOutputHelper output)
{
    internal const string Collision = "failed to set up container networking: driver failed programming external connectivity on endpoint synthetic: failed to listen on TCP socket: address already in use";

    [Fact]
    public async Task ExactCollision_DisposesAndVerifiesBeforeFreshAllocation()
    {
        var trace = new List<string>();
        var first = new ScriptedResource("first", trace, Failure(Collision));
        var second = new ScriptedResource("second", trace);
        var count = 0;
        var result = await StartupPrototype.StartAsync(() => ++count == 1 ? first : second,
            (delay, token) => { Assert.Equal(TimeSpan.FromMilliseconds(100), delay); trace.Add("backoff"); return Task.CompletedTask; });
        Assert.Same(second, result);
        Assert.Equal(["first:start", "first:dispose", "first:verify", "backoff", "second:start"], trace);
        Assert.Equal(2, count);
        await result.DisposeAndVerifyAsync();
    }

    [Theory]
    [InlineData(400, Collision)]
    [InlineData(409, Collision)]
    [InlineData(503, Collision)]
    [InlineData(500, "address already in use")]
    [InlineData(500, "failed to set up container networking: port is already allocated")]
    [InlineData(500, "failed to set up container networking: driver failed programming external connectivity: permission denied")]
    [InlineData(500, "failed to listen on TCP socket: address already in use; failed to set up container networking: driver failed programming external connectivity")]
    public async Task OtherDockerFaults_AreCleanedAndNeverRetried(int status, string message)
    {
        var failure = Failure(message, (HttpStatusCode)status);
        var trace = new List<string>();
        var calls = 0;
        var result = await Assert.ThrowsAsync<DockerApiException>(() => StartupPrototype.StartAsync(
            () => { calls++; return new ScriptedResource("one", trace, failure); }, NoDelay));
        Assert.Same(failure, result);
        Assert.Equal(1, calls);
        Assert.Equal(["one:start", "one:dispose", "one:verify"], trace);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"message\":null}")]
    [InlineData("{\"message\":[]}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"message\":\"other\",\"message\":\"failed to set up container networking: driver failed programming external connectivity on endpoint synthetic: failed to listen on TCP socket: address already in use\"}")]
    public void MalformedDockerResponse_IsNotCollision(string response) =>
        Assert.False(StartupPrototype.IsCollision(new DockerApiException(HttpStatusCode.InternalServerError, response)));

    [Fact]
    public void CollisionWithTrailingUnrelatedSuffix_IsNotRetried() =>
        Assert.False(StartupPrototype.IsCollision(Failure(Collision + "; unrelated failure")));

    [Fact]
    public async Task ContextOutput_ExactByteBudgetIsAccepted()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 16 * 1024)));
        Assert.Equal(16 * 1024, (await StartupPrototype.ReadBoundedAsync(stream)).Length);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task ContextOutput_OverflowStopsAtBudgetPlusOneWithoutEchoingPayload()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1024 * 1024)));
        var error = await Assert.ThrowsAsync<IOException>(() => StartupPrototype.ReadBoundedAsync(stream));
        Assert.Equal("Docker context output exceeded the 16 KiB budget.", error.Message);
        Assert.Equal(16 * 1024 + 1, stream.Position); // Actual read bound, not a post-buffer length check.
    }

    [Fact]
    public async Task ContextOutput_CanceledReadDoesNotConsumePayload()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupPrototype.ReadBoundedAsync(stream, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ThreeCollisions_ExhaustBoundAndRemoveEveryAttempt()
    {
        var trace = new List<string>();
        var calls = 0;
        var waits = 0;
        await Assert.ThrowsAsync<DockerApiException>(() => StartupPrototype.StartAsync(
            () => new ScriptedResource((++calls).ToString(), trace, Failure(Collision)),
            (delay, token) => { waits++; Assert.Equal(TimeSpan.FromMilliseconds(100 * waits), delay); return Task.CompletedTask; }));
        Assert.Equal(3, calls);
        Assert.Equal(2, waits);
        Assert.Equal(3, trace.Count(x => x.EndsWith(":verify", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CleanupFailure_StopsBeforeBackoffOrReplacement()
    {
        var calls = 0;
        var failure = new IOException("Synthetic removal verification failure.");
        var resource = new ScriptedResource("one", [], Failure(Collision), failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => StartupPrototype.StartAsync(
            () => { calls++; return resource; }, (_, _) => throw new InvalidOperationException("Must not wait."))));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallerCanceledBeforeStart_AllocatesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupPrototype.StartAsync(
            () => { calls++; return new ScriptedResource("one", []); }, NoDelay, cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDuringBackoff_RemovesFailureAndAllocatesNothingElse()
    {
        using var cancellation = new CancellationTokenSource();
        var trace = new List<string>();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupPrototype.StartAsync(
            () => { calls++; return new ScriptedResource("one", trace, Failure(Collision)); },
            async (delay, token) => { cancellation.Cancel(); await Task.Delay(delay, token); }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal(["one:start", "one:dispose", "one:verify"], trace);
    }

    [Fact]
    public async Task CanceledStart_IsCleanedWithoutRetry()
    {
        var calls = 0;
        var trace = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupPrototype.StartAsync(
            () => { calls++; return new ScriptedResource("one", trace, new OperationCanceledException()); }, NoDelay));
        Assert.Equal(1, calls);
        Assert.Equal(["one:start", "one:dispose", "one:verify"], trace);
    }

    [Fact]
    public async Task ReadinessOrApplicationFailure_IsNotCollisionRetry()
    {
        var failure = new TimeoutException("Synthetic readiness failure.");
        var calls = 0;
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => StartupPrototype.StartAsync(
            () => { calls++; return new ScriptedResource("one", [], failure); }, NoDelay)));
        Assert.Equal(1, calls);
        // Application/EF work is intentionally outside this startup helper.
    }

    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", "npipe://./pipe/dockerDesktopLinuxEngine")]
    [InlineData("npipe://./pipe/docker_engine-1.0", "npipe://./pipe/docker_engine-1.0")]
    [InlineData("unix:///var/run/docker.sock", "unix:///var/run/docker.sock")]
    public void PrecreateLocalEndpoint_PreservesExactPipeIdentity(string input, string expected) =>
        Assert.Equal(expected, StartupPrototype.NormalizeLocalEndpoint(input));

    [Theory]
    [InlineData("tcp://remote:2375")]
    [InlineData("npipe://remote/pipe/docker_engine")]
    [InlineData("npipe://./pipe/")]
    [InlineData("npipe://./pipe/docker_engine/other")]
    [InlineData("npipe://./pipe/docker%2fengine")]
    [InlineData("npipe://./pipe/docker_engine?x=1")]
    [InlineData("npipe://./pipe/docker engine")]
    [InlineData("unix:///tmp/other.sock")]
    public void PrecreateRemoteOrNoncanonicalEndpoint_IsRejected(string input) =>
        Assert.Throws<InvalidOperationException>(() => StartupPrototype.NormalizeLocalEndpoint(input));

    [Fact]
    public async Task PairTerminalFailure_CancelsSettlesAndRemovesBothSiblings()
    {
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Synthetic terminal startup failure.");
        var first = new ControlledResource(async token => { await secondEntered.Task.WaitAsync(token); throw failure; });
        var second = new ControlledResource(async token => { secondEntered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => StartupPrototype.StartPairAsync(() => first, () => second, NoDelay)));
        Assert.True(first.Removed);
        Assert.True(second.Removed);
        Assert.True(second.Canceled);
    }

    [Fact]
    public async Task PairFactoryFailureBeforeId_CancelsAndRemovesAllocatedSibling()
    {
        var failure = new IOException("Synthetic factory failure before container creation.");
        var first = new ControlledResource(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => StartupPrototype.StartPairAsync(() => first, () => throw failure, NoDelay)));
        Assert.True(first.Canceled);
        Assert.True(first.Removed);
    }

    [Fact]
    public async Task PairCallerCanceled_AllocatesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var allocations = 0;
        IStartupResource Create() { allocations++; return new ScriptedResource("none", []); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupPrototype.StartPairAsync(Create, Create, NoDelay, cancellation.Token));
        Assert.Equal(0, allocations);
    }

    [Fact]
    public async Task PairRetryableCollision_DoesNotCancelRunningSibling()
    {
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = new ControlledResource(async token => { await secondEntered.Task.WaitAsync(token); throw Failure(Collision); });
        var replacement = new ControlledResource(_ => Task.CompletedTask);
        var sibling = new ControlledResource(_ => { secondEntered.SetResult(); return Task.CompletedTask; });
        var allocations = 0;
        var pair = await StartupPrototype.StartPairAsync(() => ++allocations == 1 ? firstAttempt : replacement, () => sibling, NoDelay);
        Assert.True(firstAttempt.Removed);
        Assert.False(sibling.Canceled);
        Assert.False(sibling.Removed);
        Assert.Same(replacement, pair.First);
        await Task.WhenAll(pair.First.DisposeAndVerifyAsync(), pair.Second.DisposeAndVerifyAsync());
    }

    [Fact]
    public async Task PairCleanupFailure_StillAttemptsOtherSiblingRemoval()
    {
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ControlledResource(async token => { await secondEntered.Task.WaitAsync(token); throw new IOException("Startup fault."); }, true);
        var second = new ControlledResource(async token => { secondEntered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await Assert.ThrowsAsync<IOException>(() => StartupPrototype.StartPairAsync(() => first, () => second, NoDelay));
        Assert.True(first.Removed);
        Assert.True(second.Removed);
    }

    [Fact]
    public async Task PairCallerCanceledDuringStartup_SettlesAndRemovesBoth()
    {
        using var cancellation = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ControlledResource(async token => { firstEntered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        var second = new ControlledResource(async token => { secondEntered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        var startup = StartupPrototype.StartPairAsync(() => first, () => second, NoDelay, cancellation.Token);
        await Task.WhenAll(firstEntered.Task, secondEntered.Task);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.True(first.Canceled && second.Canceled);
        Assert.True(first.Removed && second.Removed);
    }

    [Fact]
    public async Task RealOwnedDocker_NormalStartupAndOccupiedPortDoNotBroadenPredicate()
    {
        var endpoint = await StartupPrototype.LocalDockerEndpointAsync();
        var run = Guid.NewGuid().ToString("N");
        using var docker = new DockerClientBuilder().WithEndpoint(new Uri(endpoint)).Build();
        var absentIdentity = new ContainerAttempt(new Uri(endpoint), "quotation97", run, "never-created", 1);
        var absentContainer = new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(new Uri(endpoint))
            .WithName(absentIdentity.Name).WithLabel(absentIdentity.Labels).Build();
        Assert.Equal(TestcontainersStates.Undefined, absentContainer.State);
        await new DisposableContainerPair.OwnedContainer(absentContainer, docker, absentIdentity).DisposeAndVerifyAsync();
        output.WriteLine("Precreate/missing-SDK-ID cleanup: exact owned name absent404; SDK disposed without Id getter.");
        DockerResource NewResource(int attempt, int? port = null)
        {
            var builder = new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(new Uri(endpoint))
                .WithName($"quotation97-{run}-{attempt}")
                .WithLabel("maliev.proof.owner", "quotation97").WithLabel("maliev.proof.run", run)
                .WithLabel("maliev.proof.attempt", attempt.ToString())
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379));
            builder = port.HasValue ? builder.WithPortBinding(port.Value, 6379) : builder.WithPortBinding(6379, true);
            builder = builder.WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.PortBindings!["6379/tcp"] =
                    [new PortBinding { HostIP = "127.0.0.1", HostPort = port?.ToString() ?? "" }];
                parameters.HostConfig.Tmpfs = new Dictionary<string, string> { ["/data"] = "rw,noexec,nosuid,size=16777216" };
            });
            return new DockerResource(builder.Build(), docker, run, attempt);
        }
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var first = (DockerResource)await StartupPrototype.StartAsync(() => NewResource(1), Task.Delay, bounded.Token);
        try
        {
            Assert.Equal(TestcontainersStates.Running, first.Container.State);
            var inspect = await docker.Containers.InspectContainerAsync(first.Container.Id, bounded.Token);
            Assert.Equal("quotation97", inspect.Config!.Labels!["maliev.proof.owner"]);
            Assert.All(inspect.NetworkSettings!.Ports!["6379/tcp"], binding => Assert.Equal("127.0.0.1", binding.HostIP));
            var calls = 0;
            DockerApiException? collision = null;
            TimeoutException? mappingTimeout = null;
            DockerResource? replacement = null;
            try
            {
                replacement = (DockerResource)await StartupPrototype.StartAsync(() =>
                {
                    calls++;
                    return NewResource(calls + 1, calls == 1 ? first.Container.GetMappedPublicPort(6379) : null);
                }, Task.Delay, bounded.Token);
            }
            catch (DockerApiException error) { collision = error; }
            catch (TimeoutException error) when (error.StackTrace?.Contains("DotNet.Testcontainers.Containers.DockerContainer.CheckReadinessAsync", StringComparison.Ordinal) == true)
            { mappingTimeout = error; }
            if (collision is not null)
            {
                Assert.False(StartupPrototype.IsCollision(collision));
                Assert.Equal(1, calls); // Docker Desktop may report "port is already allocated", not CI EADDRINUSE.
                output.WriteLine("Occupied-port control: nonmatching Docker error; one attempt, removed and verified.");
            }
            else if (mappingTimeout is not null)
            {
                Assert.Equal(1, calls);
                output.WriteLine("Occupied-port control: Testcontainers port-map readiness timeout; one attempt, removed and verified; NOT CI collision reproduction.");
            }
            else
            {
                Assert.NotNull(replacement);
                Assert.Equal(2, calls);
                Assert.NotEqual(first.Container.Id, replacement.Container.Id);
                await replacement.DisposeAndVerifyAsync();
                output.WriteLine("Occupied-port control: exact matching collision then fresh dynamic replacement.");
            }
            Assert.Equal(TestcontainersStates.Running, first.Container.State);
        }
        finally { await first.DisposeAndVerifyAsync(); }
    }

    private static DockerApiException Failure(string message, HttpStatusCode status = HttpStatusCode.InternalServerError) =>
        new(status, JsonSerializer.Serialize(new { message }));
    private static Task NoDelay(TimeSpan delay, CancellationToken token) => Task.CompletedTask;


    private sealed class ScriptedResource(string name, List<string> trace, Exception? failure = null, Exception? cleanupFailure = null) : IStartupResource
    {
        public Task StartAsync(CancellationToken token)
        {
            trace.Add(name + ":start");
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
        public Task DisposeAndVerifyAsync()
        {
            trace.Add(name + ":dispose");
            if (cleanupFailure is not null) return Task.FromException(cleanupFailure);
            trace.Add(name + ":verify");
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledResource(Func<CancellationToken, Task> start, bool cleanupFailure = false) : IStartupResource
    {
        public bool Removed { get; private set; }
        public bool Canceled { get; private set; }
        public async Task StartAsync(CancellationToken token)
        {
            try { await start(token); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public Task DisposeAndVerifyAsync()
        {
            Removed = true;
            if (cleanupFailure) throw new IOException("Synthetic independent cleanup failure.");
            return Task.CompletedTask;
        }
    }

    private sealed class DockerResource(IContainer container, DockerClient docker, string run, int attempt) : IStartupResource
    {
        public IContainer Container => container;
        public Task StartAsync(CancellationToken token) => container.StartAsync(token);
        public async Task DisposeAndVerifyAsync()
        {
            var id = container.Id;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var inspect = await docker.Containers.InspectContainerAsync(id, timeout.Token);
            Assert.Equal("quotation97", inspect.Config!.Labels!["maliev.proof.owner"]);
            Assert.Equal(run, inspect.Config.Labels["maliev.proof.run"]);
            Assert.Equal(attempt.ToString(), inspect.Config.Labels["maliev.proof.attempt"]);
            await container.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            var error = await Assert.ThrowsAnyAsync<DockerApiException>(() => docker.Containers.InspectContainerAsync(id, timeout.Token));
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        }
    }
}
