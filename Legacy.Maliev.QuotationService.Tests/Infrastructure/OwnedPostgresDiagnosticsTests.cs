using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class OwnedPostgresDiagnosticsTests
{
    private const string Prefix = "2026-10-02 03:22:37.994 UTC [45] ";

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
