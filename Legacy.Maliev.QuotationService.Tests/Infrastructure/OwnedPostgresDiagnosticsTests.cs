using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class OwnedPostgresDiagnosticsTests
{
    private const string Prefix = "2026-10-02 03:22:37.994 UTC [45] ";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Localhost_selected_endpoint_matches_actual_explicit_or_wildcard_mapping(string binding)
    {
        Assert.Equal(PostgresCorrelation.Matched, OwnedPostgresDiagnostics.CorrelateEndpoint(
            new("localhost", 49123), [new(binding, 49123)]));
    }

    [Theory]
    [InlineData("wrong-port")]
    [InlineData("ambiguous")]
    [InlineData("wrong-host")]
    public void Different_or_ambiguous_mapping_is_evidence_mismatch_not_readiness(string scenario)
    {
        DiagnosticEndpoint[] mappings = scenario switch
        {
            "wrong-port" => [new("127.0.0.1", 49124)],
            "ambiguous" => [new("127.0.0.1", 49123), new("127.0.0.1", 49124)],
            _ => [new("192.0.2.1", 49123)]
        };
        Assert.Equal(PostgresCorrelation.Mismatched, OwnedPostgresDiagnostics.CorrelateEndpoint(new("localhost", 49123), mappings));
    }

    [Fact]
    public void Dual_stack_wildcards_on_the_same_port_are_not_ambiguous()
    {
        Assert.Equal(PostgresCorrelation.Matched, OwnedPostgresDiagnostics.CorrelateEndpoint(
            new("localhost", 49123), [new("0.0.0.0", 49123), new("::", 49123)]));
    }

    [Fact]
    public void Missing_endpoint_or_backend_observation_remains_unknown()
    {
        Assert.Equal(PostgresCorrelation.Unknown, OwnedPostgresDiagnostics.CorrelateEndpoint(null, [new("127.0.0.1", 49123)]));
        Assert.Equal(PostgresCorrelation.Unknown, OwnedPostgresDiagnostics.CorrelateEndpoint(new("localhost", 49123), null));
        Assert.Equal(PostgresCorrelation.Unknown, OwnedPostgresDiagnostics.CorrelateBackend(null, OwnedBackend()));
        Assert.Equal(PostgresCorrelation.Unknown, OwnedPostgresDiagnostics.CorrelateBackend(SqlBackend(), null));
    }

    [Fact]
    public void Independent_backend_pid_parent_start_and_cluster_agree()
    {
        Assert.Equal(PostgresCorrelation.Matched, OwnedPostgresDiagnostics.CorrelateBackend(SqlBackend(), OwnedBackend()));
    }

    [Theory]
    [InlineData("npgsql-pid")]
    [InlineData("proc-pid")]
    [InlineData("parent")]
    [InlineData("process-start")]
    [InlineData("postmaster-start")]
    [InlineData("system-id")]
    public void Backend_identity_disagreement_is_not_compatible_evidence(string scenario)
    {
        var sql = SqlBackend();
        var owned = OwnedBackend();
        if (scenario == "npgsql-pid") sql = sql with { NpgsqlPid = 73 };
        if (scenario == "proc-pid") owned = owned with { Pid = 73 };
        if (scenario == "parent") owned = owned with { ParentPid = 2 };
        if (scenario == "process-start") owned = owned with { StartTicks = 0 };
        if (scenario == "postmaster-start") owned = owned with { PostmasterStart = owned.PostmasterStart.AddSeconds(1) };
        if (scenario == "system-id") owned = owned with { SystemId = 9002 };
        Assert.Equal(PostgresCorrelation.Mismatched, OwnedPostgresDiagnostics.CorrelateBackend(sql, owned));
    }

    [Fact]
    public void Exact_unexpected_postmaster_FATAL_is_structural_without_retaining_body()
    {
        var result = Assert.Single(OwnedPostgresDiagnostics.Classify(Prefix + "FATAL:  terminating connection due to unexpected postmaster exit"));
        Assert.Equal("UnexpectedPostmasterExit", result.Event.ToString());
        Assert.Null(result.Signal);
        Assert.Empty(OwnedPostgresDiagnostics.Classify(Prefix + "LOG:  terminating connection due to unexpected postmaster exit"));
        Assert.Empty(OwnedPostgresDiagnostics.Classify(Prefix + "FATAL:  user private said terminating connection due to unexpected postmaster exit"));
    }

    private static SqlBackendIdentity SqlBackend() => new(72, 72, new(2026, 10, 2, 4, 25, 37, TimeSpan.Zero), 9001);
    private static OwnedBackendIdentity OwnedBackend() => new(72, 1, 14200, 1, new(2026, 10, 2, 4, 25, 37, TimeSpan.Zero), 9001);

    [Theory]
    [InlineData("localhost", 0)]
    [InlineData("localhost", 65536)]
    [InlineData("localhost ", 49123)]
    [InlineData("https://localhost", 49123)]
    [InlineData("localhost;Password=synthetic", 49123)]
    public void Malformed_endpoint_never_counts_as_matched(string host, int port) =>
        Assert.Equal(PostgresCorrelation.Mismatched, OwnedPostgresDiagnostics.CorrelateEndpoint(new(host, port), [new(host, port)]));

    [Theory]
    [InlineData("pid")]
    [InlineData("system")]
    [InlineData("start")]
    [InlineData("postmaster")]
    public void Invalid_identity_values_even_if_equal_never_count_as_matched(string field)
    {
        var sql = SqlBackend();
        var owned = OwnedBackend();
        if (field == "pid") { sql = sql with { NpgsqlPid = 0, SqlPid = 0 }; owned = owned with { Pid = 0 }; }
        if (field == "system") { sql = sql with { SystemId = 0 }; owned = owned with { SystemId = 0 }; }
        if (field == "start") { sql = sql with { PostmasterStart = default }; owned = owned with { PostmasterStart = default }; }
        if (field == "postmaster") owned = owned with { ParentPid = 0, PostmasterPid = 0 };
        Assert.Equal(PostgresCorrelation.Mismatched, OwnedPostgresDiagnostics.CorrelateBackend(sql, owned));
    }

    [Fact]
    public async Task Actual_caller_backend_correlates_with_owned_mapping_socket_and_process_without_a_surrogate()
    {
        var fixture = new Controllers.InvoiceConsumerFixture();
        try
        {
            await fixture.InitializeAsync();
            Assert.Equal(3, fixture.StorageDiagnostics.Count);
            using var snapshot = JsonDocument.Parse(fixture.StorageDiagnostics[^1]);
            var phases = snapshot.RootElement.GetProperty("ConnectionPhases").EnumerateArray().ToArray();
            Assert.Equal(new[] { "open-start", "open-complete", "create-start", "create-complete" }, phases.Select(item => item.GetProperty("phase").GetString()));
            Assert.Equal("Unknown", phases[0].GetProperty("Endpoint").GetString());
            Assert.Equal(JsonValueKind.Null, phases[0].GetProperty("SelectedPort").ValueKind);
            Assert.Equal("Unknown", phases[0].GetProperty("Backend").GetString());
            Assert.Equal(JsonValueKind.Null, phases[0].GetProperty("Sql").ValueKind);
            foreach (var phase in phases.Skip(1))
            {
                Assert.Equal("Matched", phase.GetProperty("Endpoint").GetString());
                Assert.Equal("Matched", phase.GetProperty("Backend").GetString());
                var sql = phase.GetProperty("Sql"); var owned = phase.GetProperty("Owned");
                Assert.Equal(sql.GetProperty("NpgsqlPid").GetInt32(), sql.GetProperty("SqlPid").GetInt32());
                Assert.Equal(sql.GetProperty("SqlPid").GetInt32(), owned.GetProperty("Pid").GetInt32());
                Assert.Equal(owned.GetProperty("ParentPid").GetInt32(), owned.GetProperty("PostmasterPid").GetInt32());
                Assert.Equal(sql.GetProperty("SystemId").GetUInt64(), owned.GetProperty("SystemId").GetUInt64());
                Assert.Equal(sql.GetProperty("PostmasterStart").GetString(), owned.GetProperty("PostmasterStart").GetString());
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData("LOG:  database system is ready to accept connections", "Ready")]
    [InlineData("LOG:  received fast shutdown request", "FastShutdown")]
    [InlineData("LOG:  received immediate shutdown request", "ImmediateShutdown")]
    [InlineData("LOG:  all server processes terminated; reinitializing", "Reinitializing")]
    [InlineData("ERROR:  could not write file: No space left on device", "NoSpace")]
    [InlineData("ERROR:  could not resize shared memory segment: synthetic detail", "SharedMemoryResize")]
    [InlineData("PANIC:  synthetic private detail", "Panic")]
    public void Known_events_emit_only_classification(string text, string expected)
    {
        var observed = Assert.Single(OwnedPostgresDiagnostics.Classify(Prefix + text));
        Assert.Equal(expected, observed.Event.ToString());
        Assert.Null(observed.Signal);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(15)]
    public void Process_signal_has_numeric_only_output(int signal)
    {
        var classified = OwnedPostgresDiagnostics.Classify(Prefix + $"LOG:  server process (PID 84) was terminated by signal {signal}: private text");
        Assert.Equal(new PostgresEvent(PostgresDiagnosticEvent.ProcessSignal, signal), Assert.Single(classified));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(classified));
    }

    [Theory]
    [InlineData("unknown customer@example.test password=secret SQL SELECT")]
    [InlineData("LOG:  received fast shutdown request")]
    [InlineData("2026-10-02 03:22:37.994 UTC [45] LOG:  user customer@example.test said received fast shutdown request")]
    [InlineData("2026-10-02 03:22:37.994 UTC [45] LOG:  server process (PID 84) was terminated by signal 99: private")]
    public void Unknown_or_untrusted_text_emits_nothing(string text) => Assert.Empty(OwnedPostgresDiagnostics.Classify(text));

    [Fact]
    public void Classification_does_not_retain_private_log_body()
    {
        var input = Prefix + "ERROR:  could not resize shared memory segment customer@example.test password=secret SQL SELECT";
        var output = JsonSerializer.Serialize(OwnedPostgresDiagnostics.Classify(input));
        Assert.Contains("Event", output);
        Assert.DoesNotContain("customer", output);
        Assert.DoesNotContain("password", output);
        Assert.DoesNotContain("SELECT", output);
    }

    [Theory]
    [InlineData("database system is ready to accept connections")]
    [InlineData("received fast shutdown request")]
    [InlineData("received immediate shutdown request")]
    [InlineData("all server processes terminated; reinitializing")]
    [InlineData("server process (PID 84) was terminated by signal 11: private")]
    public void Structural_events_at_ERROR_level_are_not_trusted_LOG_events(string text) =>
        Assert.Empty(OwnedPostgresDiagnostics.Classify(Prefix + "ERROR:  " + text));

    [Fact]
    public void Exact_byte_budget_accepts_boundary_and_rejects_overflow()
    {
        Assert.Empty(OwnedPostgresDiagnostics.Classify(new string('x', OwnedPostgresDiagnostics.InputBudget)));
        Assert.Throws<IOException>(() => OwnedPostgresDiagnostics.Classify(new string('x', OwnedPostgresDiagnostics.InputBudget + 1)));
        Assert.Throws<IOException>(() => OwnedPostgresDiagnostics.Classify(new string('ก', OwnedPostgresDiagnostics.InputBudget)));
    }

    [Theory]
    [InlineData("memory.current=123\nmemory.max=max\npids.current=5\nshm_free=65536\n", true)]
    [InlineData("password=secret\n", false)]
    [InlineData("memory.current=123\nmemory.current=124\n", false)]
    [InlineData("memory.current=customer@example.test\n", false)]
    [InlineData("memory.current=max\n", false)]
    [InlineData("memory.current=-1\n", false)]
    public void Metrics_accept_only_allowlisted_unique_numeric_fields(string input, bool valid)
    {
        if (valid) Assert.Equal(4, OwnedPostgresDiagnostics.ParseMetrics(input).Count);
        else Assert.Throws<InvalidOperationException>(() => OwnedPostgresDiagnostics.ParseMetrics(input));
    }

    [Fact]
    public void Exact_owned_identity_is_required()
    {
        var inspected = Owned();
        OwnedPostgresDiagnostics.VerifyOwnership(new string('a', 64), "quotation99-invoice-consumer", inspected);
        inspected.Config!.Labels!["maliev.proof.owner"] = "unrelated";
        Assert.Throws<InvalidOperationException>(() => OwnedPostgresDiagnostics.VerifyOwnership(new string('a', 64), "quotation99-invoice-consumer", inspected));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("run")]
    [InlineData("resource")]
    [InlineData("attempt")]
    public void Mismatched_owned_identity_fails_closed(string mismatch)
    {
        var inspected = Owned();
        if (mismatch == "id") inspected.ID = new string('b', 64);
        if (mismatch == "name") inspected.Name = "/unrelated";
        if (mismatch == "run") inspected.Config!.Labels!["maliev.proof.run"] = "../unrelated";
        if (mismatch == "resource") inspected.Config!.Labels!["maliev.proof.resource"] = "redis";
        if (mismatch == "attempt") inspected.Config!.Labels!["maliev.proof.attempt"] = "4";
        Assert.Throws<InvalidOperationException>(() => OwnedPostgresDiagnostics.VerifyOwnership(new string('a', 64), "quotation99-invoice-consumer", inspected));
    }

    [Fact]
    public async Task Observer_diagnostic_failure_cannot_replace_original_failure_or_emit_message()
    {
        var original = new IOException("original sentinel");
        try { throw original; }
        catch (IOException caught)
        {
            var safe = await OwnedPostgresDiagnostics.PreserveFailureAsync(() => throw new InvalidOperationException("customer@example.test password=secret"));
            Assert.Equal("{\"DiagnosticUnavailable\":true}", safe);
            Assert.Same(original, caught);
        }
    }

    [Fact]
    public async Task Nested_observer_failure_then_bare_rethrow_preserves_original_instance_and_throw_site()
    {
        var original = new IOException("original sentinel");
        var observed = await Assert.ThrowsAsync<IOException>(() => OriginalBoundaryAsync(original));
        Assert.Same(original, observed);
        Assert.Contains(nameof(ThrowOriginal), observed.StackTrace);
    }

    private static async Task OriginalBoundaryAsync(IOException original)
    {
        try { ThrowOriginal(original); }
        catch
        {
            var safe = await OwnedPostgresDiagnostics.PreserveFailureAsync(() => throw new IOException("private observer body"));
            Assert.Equal("{\"DiagnosticUnavailable\":true}", safe);
            throw;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowOriginal(IOException original) => throw original;

    [Fact]
    public async Task Bounded_reader_stops_before_retaining_overflow()
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => OwnedPostgresDiagnostics.ReadBoundedAsync((buffer, _) =>
        {
            calls++;
            Array.Fill(buffer, (byte)'x');
            return Task.FromResult(buffer.Length);
        }));
        Assert.Equal(17, calls);
    }

    [Fact]
    public async Task Bounded_reader_retains_exact_boundary_and_observes_cancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', OwnedPostgresDiagnostics.InputBudget)));
        Assert.Equal(OwnedPostgresDiagnostics.InputBudget, (await OwnedPostgresDiagnostics.ReadBoundedAsync(async (buffer, token) =>
            await stream.ReadAsync(buffer, token))).Length);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedPostgresDiagnostics.ReadBoundedAsync(async (buffer, token) =>
            await stream.ReadAsync(buffer, token), canceled.Token));
    }

    [Fact]
    public async Task Actual_owned_two_database_fixture_emits_available_numeric_metadata_only()
    {
        var fixture = new Controllers.InvoiceConsumerFixture();
        try
        {
            await fixture.InitializeAsync();
            Assert.Equal(3, fixture.StorageDiagnostics.Count);
            foreach (var observation in fixture.StorageDiagnostics)
            {
                using var document = JsonDocument.Parse(observation);
                Assert.False(document.RootElement.TryGetProperty("DiagnosticUnavailable", out _), "Owned numeric observation must be reached.");
                Assert.True(document.RootElement.GetProperty("Running").GetBoolean());
                var metrics = document.RootElement.GetProperty("Metrics");
                Assert.True(metrics.GetProperty("postmaster_pid").GetString() is { Length: > 0 });
                Assert.True(ulong.Parse(metrics.GetProperty("pgdata_free").GetString()!) > 0);
                Assert.True(ulong.Parse(metrics.GetProperty("shm_total").GetString()!) > 0);
                Assert.NotEmpty(document.RootElement.GetProperty("Events").EnumerateArray());
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static ContainerInspectResponse Owned() => new()
    {
        ID = new string('a', 64),
        Name = $"/quotation97-{new string('b', 32)}-pg-1",
        Config = new()
        {
            Labels = new Dictionary<string, string>
            {
                ["maliev.proof.owner"] = "quotation99-invoice-consumer",
                ["maliev.proof.run"] = new string('b', 32),
                ["maliev.proof.resource"] = "pg",
                ["maliev.proof.attempt"] = "1"
            }
        }
    };
}
