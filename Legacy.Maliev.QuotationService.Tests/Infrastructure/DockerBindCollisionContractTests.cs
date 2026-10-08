using System.Net;
using System.Text.Json;
using Docker.DotNet;
using Xunit;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class DockerBindCollisionContractTests
{
    private const string BindCollision = "failed to set up container networking: driver failed programming external connectivity on endpoint quotation97-synthetic-pg-1 (aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa): failed to bind host port for 0.0.0.0::172.17.0.6:5432/tcp: address already in use";

    [Fact]
    public void ExactHostedHostBindShape_IsCollision() =>
        Assert.True(DisposableContainerStartup.IsCollision(Failure(BindCollision)));

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(503)]
    public void OtherStatusNeverAdmitsHostBindShape(int status) =>
        Assert.False(DisposableContainerStartup.IsCollision(Failure(BindCollision, (HttpStatusCode)status)));

    [Theory]
    [InlineData("prefix")]
    [InlineData("trailing")]
    [InlineData("permission")]
    [InlineData("allocated")]
    [InlineData("host")]
    [InlineData("protocol")]
    [InlineData("missing-id")]
    [InlineData("short-id")]
    [InlineData("uppercase-id")]
    [InlineData("endpoint-space")]
    [InlineData("invalid-ip")]
    [InlineData("noncanonical-ip")]
    [InlineData("zero-port")]
    [InlineData("large-port")]
    [InlineData("leading-zero-port")]
    public void SimilarButUnqualifiedErrorsNeverAdmitRetry(string mutation)
    {
        var message = mutation switch
        {
            "prefix" => BindCollision.Replace("failed to set up container networking", "unrelated networking", StringComparison.Ordinal),
            "trailing" => BindCollision + "; unrelated failure",
            "permission" => BindCollision.Replace("address already in use", "permission denied", StringComparison.Ordinal),
            "allocated" => BindCollision.Replace("address already in use", "port is already allocated", StringComparison.Ordinal),
            "host" => BindCollision.Replace("0.0.0.0::", "127.0.0.1::", StringComparison.Ordinal),
            "protocol" => BindCollision.Replace("/tcp:", "/udp:", StringComparison.Ordinal),
            "missing-id" => BindCollision.Replace(" (" + new string('a', 64) + ")", "", StringComparison.Ordinal),
            "short-id" => BindCollision.Replace(new string('a', 64), new string('a', 63), StringComparison.Ordinal),
            "uppercase-id" => BindCollision.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            "endpoint-space" => BindCollision.Replace("quotation97-synthetic-pg-1", "quotation97 synthetic", StringComparison.Ordinal),
            "invalid-ip" => BindCollision.Replace("172.17.0.6", "999.17.0.6", StringComparison.Ordinal),
            "noncanonical-ip" => BindCollision.Replace("172.17.0.6", "172.017.0.6", StringComparison.Ordinal),
            "zero-port" => BindCollision.Replace(":5432/tcp", ":0/tcp", StringComparison.Ordinal),
            "large-port" => BindCollision.Replace(":5432/tcp", ":65536/tcp", StringComparison.Ordinal),
            "leading-zero-port" => BindCollision.Replace(":5432/tcp", ":05432/tcp", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.False(DisposableContainerStartup.IsCollision(Failure(message)));
    }

    [Fact]
    public void DuplicateMessageOrOversizedBodyNeverAdmitsRetry()
    {
        var encoded = JsonSerializer.Serialize(BindCollision);
        Assert.False(DisposableContainerStartup.IsCollision(new DockerApiException(HttpStatusCode.InternalServerError,
            "{\"message\":\"other\",\"message\":" + encoded + "}")));
        Assert.False(DisposableContainerStartup.IsCollision(new DockerApiException(HttpStatusCode.InternalServerError,
            JsonSerializer.Serialize(new { message = BindCollision, padding = new string('x', 64 * 1024) }))));
    }

    [Fact]
    public async Task HostBindCollision_VerifiedCleanupPrecedesBackoffAndNewAllocation()
    {
        var trace = new List<string>();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var waits = 0;
        var first = new ScriptedResource("one", trace, Failure(BindCollision), cleanup.Task);
        var second = new ScriptedResource("two", trace);
        var startup = DisposableContainerStartup.StartAsync(() => ++calls == 1 ? first : second,
            (delay, _) => { waits++; Assert.Equal(TimeSpan.FromMilliseconds(100), delay); trace.Add("backoff"); return Task.CompletedTask; });
        Assert.Equal(1, calls);
        Assert.Equal(0, waits);
        Assert.False(startup.IsCompleted);
        cleanup.SetResult();
        Assert.Same(second, await startup);
        Assert.Equal(["one:start", "one:dispose", "one:verify", "backoff", "two:start"], trace);
        await second.DisposeAndVerifyAsync();
    }

    [Fact]
    public async Task ThreeHostBindCollisionsExhaustBoundAfterEveryVerifiedCleanup()
    {
        var trace = new List<string>();
        var calls = 0;
        var waits = 0;
        await Assert.ThrowsAsync<DockerApiException>(() => DisposableContainerStartup.StartAsync(
            () => new ScriptedResource((++calls).ToString(), trace, Failure(BindCollision)),
            (delay, _) => { waits++; Assert.Equal(TimeSpan.FromMilliseconds(100 * waits), delay); return Task.CompletedTask; }));
        Assert.Equal(3, calls);
        Assert.Equal(2, waits);
        Assert.Equal(["1:start", "1:dispose", "1:verify", "2:start", "2:dispose", "2:verify", "3:start", "3:dispose", "3:verify"], trace);
    }

    [Fact]
    public async Task FailedCleanupOfHostBindAttemptNeverAdmitsBackoffOrReplacement()
    {
        var calls = 0;
        var trace = new List<string>();
        var cleanup = Task.FromException(new IOException("Synthetic cleanup failure."));
        var failure = await Record.ExceptionAsync(() => DisposableContainerStartup.StartAsync(
            () => { calls++; return new ScriptedResource("one", trace, Failure(BindCollision), cleanup); },
            (_, _) => throw new InvalidOperationException("No backoff may occur.")));
        Assert.NotNull(failure);
        Assert.True(failure is IOException || failure is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(error => error is IOException));
        Assert.Equal(1, calls);
        Assert.Equal(["one:start", "one:dispose"], trace);
    }

    [Fact]
    public async Task CallerCancellationAfterCleanupDoesNotAdmitAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var trace = new List<string>();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DisposableContainerStartup.StartAsync(
            () => { calls++; return new ScriptedResource("one", trace, Failure(BindCollision)); },
            async (delay, token) => { cancellation.Cancel(); await Task.Delay(delay, token); }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal(["one:start", "one:dispose", "one:verify"], trace);
    }

    private static DockerApiException Failure(string message, HttpStatusCode status = HttpStatusCode.InternalServerError) =>
        new(status, JsonSerializer.Serialize(new { message }));

    private sealed class ScriptedResource(string name, List<string> trace, Exception? failure = null, Task? cleanup = null) : IStartupResource
    {
        public Task StartAsync(CancellationToken token)
        {
            trace.Add(name + ":start");
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public async Task DisposeAndVerifyAsync()
        {
            trace.Add(name + ":dispose");
            if (cleanup is not null) await cleanup;
            trace.Add(name + ":verify");
        }
    }
}
