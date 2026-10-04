using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

/// <summary>Characterizes the fixture assumption that a reused endpoint denotes a fresh backend.</summary>
public sealed class DisposablePostgresPoolLifetimeTests(ITestOutputHelper output)
{
    private const string Owner = "quotation100-pool-lifetime";

    [Fact]
    public async Task Sequential_owned_lifetimes_at_same_port_compare_default_pool_with_isolated_connection_control()
    {
        // Reserve dynamically, never use a production port. Release immediately before owned startup;
        // another process winning that race is an experiment failure, not grounds for a retry.
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        Assert.NotEqual(14333, port);
        reservation.Stop();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var endpoint = new Uri(await DisposableContainerStartup.LocalDockerEndpointAsync(deadline.Token));
        using var docker = new DockerClientBuilder().WithEndpoint(endpoint).Build();
        string? pooledKey = null;
        try
        {
            var pooledA = await ObserveLifetimeAsync(docker, endpoint, port, true, deadline.Token);
            pooledKey = pooledA.ConnectionString;
            RequireHealthy(pooledA);
            var pooledB = await ObserveLifetimeAsync(docker, endpoint, port, true, deadline.Token);
            Assert.True(StringComparer.Ordinal.Equals(pooledA.ConnectionString, pooledB.ConnectionString),
                "Pooled lifetime endpoint configuration changed; values are withheld.");
            Assert.NotEqual(pooledA.ContainerId, pooledB.ContainerId);
            Assert.Equal(pooledA.Image, pooledB.Image);

            var isolatedA = await ObserveLifetimeAsync(docker, endpoint, port, false, deadline.Token);
            var isolatedB = await ObserveLifetimeAsync(docker, endpoint, port, false, deadline.Token);
            RequireHealthy(isolatedA);
            RequireHealthy(isolatedB);
            Assert.Equal(pooledA.Image, isolatedA.Image);
            Assert.Equal(pooledA.Image, isolatedB.Image);
            Assert.True(StringComparer.Ordinal.Equals(isolatedA.ConnectionString, isolatedB.ConnectionString),
                "Isolated lifetime endpoint configuration changed; values are withheld.");
            Assert.True(StringComparer.Ordinal.Equals(
                new NpgsqlConnectionStringBuilder(pooledA.ConnectionString) { Pooling = false }.ConnectionString,
                isolatedA.ConnectionString), "Control differs beyond pooling; values are withheld.");
            // Missing /proc alone is never a conclusion. Successful CREATE needs positive owned lineage.
            if (pooledB.Failure is null) RequireHealthy(pooledB);
            else Assert.True(pooledB.Failure.Categories.Any(category => category is
                PostgresFailureCategory.Postgres or PostgresFailureCategory.EndOfStream or PostgresFailureCategory.Npgsql),
                "Unexpected failure category: the pool-lifetime hypothesis remains inconclusive.");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Experiment = "same-endpoint-sequential-owned-lifetimes",
                Image = pooledA.Image,
                Outcome = pooledB.Failure is null ? "not-reproduced" : "baseline-failed-isolated-control-passed",
                Baseline = pooledB.Failure,
                BaselinePreviousAfterCreate = pooledA.AfterCreate,
                BaselineBeforeCreate = pooledB.BeforeCreate,
                BaselineAfterCreate = pooledB.AfterCreate,
                ControlAfterCreate = isolatedB.AfterCreate
            }));
        }
        finally
        {
            // Retire only this exact experiment pool, after both baseline lifetimes; never ClearAllPools.
            if (pooledKey is not null)
            {
                using var pool = new NpgsqlConnection(pooledKey);
                NpgsqlConnection.ClearPool(pool);
            }
        }
    }

    private static void RequireHealthy(Lifetime observation)
    {
        Assert.Null(observation.Failure);
        Assert.NotNull(observation.AfterCreate);
        using var identity = JsonDocument.Parse(observation.AfterCreate!);
        Assert.Equal("Matched", identity.RootElement.GetProperty("Backend").GetString());
        Assert.Equal("Matched", identity.RootElement.GetProperty("Endpoint").GetString());
    }

    private static async Task<Lifetime> ObserveLifetimeAsync(DockerClient docker, Uri endpoint, int port,
        bool pooling, CancellationToken token)
    {
        var attempt = new ContainerAttempt(endpoint, Owner, Guid.NewGuid().ToString("N"), "pg", 1);
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDockerEndpoint(endpoint).WithName(attempt.Name).WithLabel(attempt.Labels)
            .WithCreateParameterModifier(parameters =>
            {
                parameters.HostConfig ??= new HostConfig();
                parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>();
                parameters.HostConfig.PortBindings["5432/tcp"] =
                    [new PortBinding { HostIP = "127.0.0.1", HostPort = port.ToString(CultureInfo.InvariantCulture) }];
                parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                {
                    ["/var/lib/postgresql"] = "rw,noexec,nosuid,size=268435456"
                };
            }).Build();
        var owned = new DisposableContainerPair.OwnedContainer(container, docker, attempt);
        try
        {
            await owned.StartAsync(token); // Exactly one startup attempt, unchanged module readiness.
            var rawConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString()).ConnectionString;
            var connectionString = pooling ? rawConnection : DisposablePostgresConnectionPolicy.Isolate(rawConnection);
            await using var connection = new NpgsqlConnection(connectionString);
            string? before = null;
            string? after = null;
            PostgresFailureSignal? failure = null;
            try
            {
                await connection.OpenAsync(token);
                before = await OwnedPostgresDiagnostics.ObserveConnectionAsync(container, Owner, "create-start", connection);
                await using var command = new NpgsqlCommand("CREATE DATABASE invoice_consumer_requests", connection);
                await command.ExecuteNonQueryAsync(token); // Original first operation, once, no retry.
                after = await OwnedPostgresDiagnostics.ObserveConnectionAsync(container, Owner, "create-complete", connection);
            }
            catch (Exception error) when (error is NpgsqlException or IOException)
            {
                failure = OwnedPostgresDiagnostics.ClassifyFailure(error);
            }
            var storage = await OwnedPostgresDiagnostics.ObserveAsync(container, Owner,
                failure is null ? "ready" : "initialization-failed");
            using var snapshot = JsonDocument.Parse(storage);
            Assert.True(snapshot.RootElement.GetProperty("Running").GetBoolean());
            Assert.False(snapshot.RootElement.GetProperty("OOMKilled").GetBoolean());
            Assert.Equal(0, snapshot.RootElement.GetProperty("RestartCount").GetInt32());
            return new(container.Id, snapshot.RootElement.GetProperty("Image").GetString()!, connectionString,
                before, after, failure);
        }
        finally
        {
            await owned.DisposeAndVerifyAsync(); // Ownership-checked exact name/labels and absence verification.
        }
    }

    // ConnectionString is kept in memory for equality/pool retirement only; never serialized or printed.
    private sealed record Lifetime(string ContainerId, string Image, string ConnectionString, string? BeforeCreate,
        string? AfterCreate, PostgresFailureSignal? Failure);
}
