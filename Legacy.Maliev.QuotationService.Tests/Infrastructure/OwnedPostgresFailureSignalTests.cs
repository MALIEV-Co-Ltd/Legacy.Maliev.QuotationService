using System.Runtime.CompilerServices;
using System.Text.Json;
using Docker.DotNet.Models;
using Npgsql;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

/// <summary>Pure diagnostic boundaries; never opens a database or Docker transport.</summary>
public sealed class OwnedPostgresFailureSignalTests(ITestOutputHelper output)
{
    private const string Owner = "quotation99-invoice-consumer";
    private static readonly string Id = new('a', 64);

    [Theory]
    [InlineData("stream", "{\"Categories\":[\"Npgsql\",\"EndOfStream\"],\"SqlState\":null}")]
    [InlineData("postgres", "{\"Categories\":[\"Postgres\"],\"SqlState\":\"57P01\"}")]
    public async Task Closed_failure_signal_reaches_actual_xunit_output_without_private_exception_body(string scenario, string expected)
    {
        Exception error = scenario == "stream"
            ? new NpgsqlException("private@example.test Password=synthetic SELECT", new EndOfStreamException("private body"))
            : new PostgresException("private@example.test Password=synthetic SELECT", "FATAL", "FATAL", "57P01");
        var emitted = new List<string>();
        await OwnedPostgresDiagnostics.EmitFailureAsync(() => Task.FromResult(OwnedPostgresDiagnostics.ClassifyFailure(error)), text =>
        {
            emitted.Add(text);
            output.WriteLine(text);
        });
        Assert.Equal(expected, Assert.Single(emitted));
        AssertSafe(emitted[0]);
    }

    [Theory]
    [InlineData("open-complete")]
    [InlineData("create-start")]
    public async Task Passive_pid_before_create_is_observed_without_borrowed_sql(string phase)
    {
        var sqlCalls = 0;
        var pidCalls = 0;
        var pid = OwnedPostgresDiagnostics.ReadPassivePid(true, () => { pidCalls++; return 72; });
        var sql = await OwnedPostgresDiagnostics.ReadCallerIdentityAsync(phase, true, () =>
        {
            sqlCalls++;
            throw new InvalidOperationException("Unexpected borrowed SQL");
        });
        Assert.Equal(72, pid);
        Assert.Equal(1, pidCalls);
        Assert.Equal(0, sqlCalls);
        Assert.Null(sql);
        Assert.Equal(PostgresCorrelation.Unknown, OwnedPostgresDiagnostics.CorrelateBackend(sql, null));
    }

    [Fact]
    public void Unopened_connection_never_reads_passive_pid() =>
        Assert.Null(OwnedPostgresDiagnostics.ReadPassivePid(false, () => throw new InvalidOperationException("Unexpected getter")));

    [Fact]
    public void Throwing_open_connection_getter_is_reached_but_remains_best_effort_unknown()
    {
        var calls = 0;
        var result = OwnedPostgresDiagnostics.ReadPassivePid(true, () =>
        {
            calls++;
            throw new InvalidOperationException("private getter body");
        });
        Assert.Equal(1, calls);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_passive_pid_is_unknown_not_zero(int pid) =>
        Assert.Null(OwnedPostgresDiagnostics.ReadPassivePid(true, () => pid));

    [Fact]
    public async Task Exact_owned_process_reads_only_the_selected_positive_pid_and_literal_fields()
    {
        var reads = new List<int>();
        var result = await OwnedPostgresDiagnostics.ReadOwnedProcessAsync(Id, Owner, Owned(), 72, pid =>
        {
            reads.Add(pid);
            return Task.FromResult("72\n1\n28180\n");
        });
        Assert.Equal(new[] { 72 }, reads);
        Assert.Equal(new PassiveOwnedProcess(72, 1, 28180), result);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("owner")]
    [InlineData("name")]
    public async Task Ownership_mismatch_refuses_before_any_process_read(string field)
    {
        var inspected = Owned();
        if (field == "id") inspected.ID = new string('c', 64);
        if (field == "owner") inspected.Config!.Labels!["maliev.proof.owner"] = "unrelated";
        if (field == "name") inspected.Name = "/unrelated";
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => OwnedPostgresDiagnostics.ReadOwnedProcessAsync(Id, Owner, inspected, 72, _ =>
        {
            calls++;
            return Task.FromResult("72\n1\n28180\n");
        }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Missing_or_invalid_pid_never_reads_an_owned_process(int? pid)
    {
        var result = await OwnedPostgresDiagnostics.ReadOwnedProcessAsync(Id, Owner, Owned(), pid,
            _ => throw new InvalidOperationException("Unexpected process read"));
        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("73\n1\n28180\n")]
    [InlineData("72\n0\n28180\n")]
    [InlineData("72\n1\n0\n")]
    [InlineData("72\n1\n28180\nextra\n")]
    [InlineData("72\nprivate@example.test\n28180\n")]
    public async Task Missing_or_malformed_process_fields_remain_unknown(string fields)
    {
        var result = await OwnedPostgresDiagnostics.ReadOwnedProcessAsync(Id, Owner, Owned(), 72, _ => Task.FromResult(fields));
        Assert.Null(result);
    }

    [Fact]
    public void Original_stream_failure_reports_fixed_categories_without_exception_text()
    {
        var error = new NpgsqlException("private@example.test Password=synthetic SELECT", new EndOfStreamException("private body"));
        error.Data["private"] = "token-synthetic";
        var signal = OwnedPostgresDiagnostics.ClassifyFailure(error);
        Assert.Equal(new[] { PostgresFailureCategory.Npgsql, PostgresFailureCategory.EndOfStream }, signal.Categories);
        Assert.Null(signal.SqlState);
        AssertSafe(JsonSerializer.Serialize(signal));
    }

    [Fact]
    public void Actual_postgres_sqlstate_is_preserved_without_message_or_detail()
    {
        var signal = OwnedPostgresDiagnostics.ClassifyFailure(new PostgresException("private body", "FATAL", "FATAL", "57P01"));
        Assert.Equal(new[] { PostgresFailureCategory.Postgres }, signal.Categories);
        Assert.Equal("57P01", signal.SqlState);
        AssertSafe(JsonSerializer.Serialize(signal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("57p01")]
    [InlineData("57P01x")]
    [InlineData("SQL;!")]
    public void Malformed_sqlstate_is_not_emitted(string state) =>
        Assert.Null(OwnedPostgresDiagnostics.ClassifyFailure(new PostgresException("private body", "FATAL", "FATAL", state)).SqlState);

    [Fact]
    public void Long_inner_chain_is_bounded_to_exactly_eight_fixed_categories()
    {
        Exception error = new EndOfStreamException("private body");
        for (var index = 0; index < 20; index++) error = new IOException("private@example.test", error);
        var signal = OwnedPostgresDiagnostics.ClassifyFailure(error);
        Assert.Equal(Enumerable.Repeat(PostgresFailureCategory.Io, 8), signal.Categories);
        AssertSafe(JsonSerializer.Serialize(signal));
    }

    [Fact]
    public async Task Emission_serializes_closed_dto_and_rejects_forged_text_or_enum_values()
    {
        var emitted = new List<string>();
        await OwnedPostgresDiagnostics.EmitFailureAsync(() => Task.FromResult(new PostgresFailureSignal(
            [PostgresFailureCategory.Npgsql, (PostgresFailureCategory)999], "private@example.test")), emitted.Add);
        var text = Assert.Single(emitted);
        Assert.True(text.Length <= 2048);
        AssertSafe(text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(new[] { "Categories", "SqlState" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "Npgsql", "Unknown" }, document.RootElement.GetProperty("Categories").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("SqlState").ValueKind);
    }

    [Fact]
    public async Task Emission_independently_bounds_observer_supplied_categories_to_eight()
    {
        var emitted = new List<string>();
        await OwnedPostgresDiagnostics.EmitFailureAsync(() => Task.FromResult(new PostgresFailureSignal(
            Enumerable.Repeat(PostgresFailureCategory.Io, 1000).ToArray(), "57P01")), emitted.Add);
        var text = Assert.Single(emitted);
        Assert.True(text.Length <= 2048);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(Enumerable.Repeat("Io", 8), document.RootElement.GetProperty("Categories").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("57P01", document.RootElement.GetProperty("SqlState").GetString());
        AssertSafe(text);
    }

    [Theory]
    [InlineData("observe")]
    [InlineData("output")]
    public async Task Observer_or_output_failure_preserves_original_exception_instance_and_throw_site(string fault)
    {
        var original = new IOException("original sentinel");
        var operations = new List<string>();
        var returned = await Assert.ThrowsAsync<IOException>(() => OriginalFailureAsync(original, fault, operations));
        Assert.Same(original, returned);
        Assert.Contains(nameof(ThrowOriginal), returned.StackTrace);
        Assert.Equal(fault == "observe" ? new[] { "observe" } : new[] { "observe", "output" }, operations);
    }

    private static async Task OriginalFailureAsync(IOException original, string fault, List<string> operations)
    {
        try { ThrowOriginal(original); }
        catch
        {
            await OwnedPostgresDiagnostics.EmitFailureAsync(
                () =>
                {
                    operations.Add("observe");
                    if (fault == "observe") throw new InvalidOperationException("private observer body");
                    return Task.FromResult(new PostgresFailureSignal([PostgresFailureCategory.Io], null));
                },
                _ =>
                {
                    operations.Add("output");
                    if (fault == "output") throw new InvalidOperationException("private output body");
                });
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOriginal(IOException error) => throw error;

    private static void AssertSafe(string text)
    {
        foreach (var forbidden in new[] { "private", "Password", "SELECT", "token-synthetic", "StackTrace", "Message", "Data", "NpgsqlException", "EndOfStreamException" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    private static ContainerInspectResponse Owned() => new()
    {
        ID = Id,
        Name = $"/quotation97-{new string('b', 32)}-pg-1",
        Config = new()
        {
            Labels = new Dictionary<string, string>
            {
                ["maliev.proof.owner"] = Owner,
                ["maliev.proof.run"] = new string('b', 32),
                ["maliev.proof.resource"] = "pg",
                ["maliev.proof.attempt"] = "1"
            }
        }
    };
}
