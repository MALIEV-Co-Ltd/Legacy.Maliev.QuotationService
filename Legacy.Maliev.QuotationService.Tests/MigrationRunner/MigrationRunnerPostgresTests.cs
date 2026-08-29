using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.MigrationRunner;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.MigrationRunner;

public sealed class MigrationRunnerPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer quotation = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer request = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Task InitializeAsync() => Task.WhenAll(quotation.StartAsync(), request.StartAsync());

    public async Task DisposeAsync()
    {
        signer.Dispose();
        await quotation.DisposeAsync();
        await request.DisposeAsync();
    }

    [Theory]
    [InlineData(MigrationWorkload.Quotation, 5)]
    [InlineData(MigrationWorkload.QuotationRequest, 3)]
    public async Task EmptyDatabase_MigratesOnlySelectedSchemaWithoutSeed(MigrationWorkload workload, int expectedTables)
    {
        await ResetBothAsync();
        var target = workload == MigrationWorkload.Quotation ? quotation.GetConnectionString() : request.GetConnectionString();
        await Runner(target, workload).RunAsync(CancellationToken.None);

        Assert.Equal(expectedTables, await ApplicationTableCountAsync(target));
        Assert.Equal(0, await DataRowCountAsync(target));
        var other = workload == MigrationWorkload.Quotation ? request.GetConnectionString() : quotation.GetConnectionString();
        Assert.Equal(0, await ApplicationTableCountAsync(other));
    }

    [Fact]
    public async Task NonEmptyDatabase_RequiresValidBoundReceiptBeforeMigration()
    {
        await ResetBothAsync();
        await ExecuteAsync(quotation.GetConnectionString(), "CREATE TABLE legacy_marker(id integer primary key)");
        var runner = Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation);

        await Assert.ThrowsAsync<SchemaBaselineRejectedException>(() => runner.RunAsync(CancellationToken.None));
        Assert.Equal(0, await HistoryCountAsync(quotation.GetConnectionString()));

        runner = Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation, ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.Quotation));
        await runner.RunAsync(CancellationToken.None);
        Assert.Equal(5, await ApplicationTableCountAsync(quotation.GetConnectionString()));
    }

    [Fact]
    public async Task NonEmptyDatabase_RejectsTamperedExpiredAndMismatchedReceiptsBeforeMigration()
    {
        foreach (var receipt in InvalidReceipts())
        {
            await ResetBothAsync();
            await ExecuteAsync(quotation.GetConnectionString(), "CREATE TABLE legacy_marker(id integer primary key)");
            var runner = Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation, receipt);
            await Assert.ThrowsAsync<SchemaBaselineRejectedException>(() => runner.RunAsync(CancellationToken.None));
            Assert.Equal(0, await HistoryCountAsync(quotation.GetConnectionString()));
        }
    }

    [Theory]
    [InlineData("CREATE VIEW legacy_view AS SELECT 1 AS id")]
    [InlineData("CREATE SEQUENCE legacy_sequence")]
    [InlineData("CREATE TYPE legacy_state AS ENUM ('open','closed')")]
    public async Task UserOwnedSchemaObject_RequiresValidReceiptBeforeMigration(string objectSql)
    {
        await ResetBothAsync();
        await ExecuteAsync(quotation.GetConnectionString(), objectSql);

        await Assert.ThrowsAsync<SchemaBaselineRejectedException>(() =>
            Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation).RunAsync(CancellationToken.None));
        Assert.Equal(0, await HistoryCountAsync(quotation.GetConnectionString()));

        await Runner(
            quotation.GetConnectionString(),
            MigrationWorkload.Quotation,
            ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.Quotation)).RunAsync(CancellationToken.None);
        Assert.Equal(5, await ApplicationTableCountAsync(quotation.GetConnectionString()));
    }

    [Fact]
    public async Task IdempotentRerun_SucceedsWithoutSeeding()
    {
        await ResetBothAsync();
        var first = Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation);
        await first.RunAsync(CancellationToken.None);
        var receipt = ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.Quotation);
        await Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation, receipt).RunAsync(CancellationToken.None);
        Assert.Equal(5, await ApplicationTableCountAsync(quotation.GetConnectionString()));
        Assert.Equal(0, await DataRowCountAsync(quotation.GetConnectionString()));
    }

    [Fact]
    public async Task ConcurrentRunner_FailsWithinBoundedLockTimeoutAndLockIsReusableAfterRelease()
    {
        await ResetBothAsync();
        await using var blocker = new NpgsqlConnection(quotation.GetConnectionString());
        await blocker.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(hashtext('legacy-maliev-quotation:migration:quotation'))", blocker))
        {
            await command.ExecuteScalarAsync();
        }

        var runner = Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation, lockTimeout: TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<MigrationLockUnavailableException>(() => runner.RunAsync(CancellationToken.None));
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext('legacy-maliev-quotation:migration:quotation'))", blocker))
        {
            await release.ExecuteScalarAsync();
        }
        await blocker.CloseAsync();

        await runner.RunAsync(CancellationToken.None);
        Assert.Equal(5, await ApplicationTableCountAsync(quotation.GetConnectionString()));
    }

    [Fact]
    public async Task Failure_ReleasesAdvisoryLock()
    {
        await ResetBothAsync();
        await ExecuteAsync(quotation.GetConnectionString(), "CREATE TABLE legacy_marker(id integer primary key)");
        await Assert.ThrowsAsync<SchemaBaselineRejectedException>(() =>
            Runner(quotation.GetConnectionString(), MigrationWorkload.Quotation).RunAsync(CancellationToken.None));

        await using var connection = new NpgsqlConnection(quotation.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext('legacy-maliev-quotation:migration:quotation'))", connection);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private QuotationMigrationRunner Runner(
        string connectionString,
        MigrationWorkload workload,
        SignedSchemaBaselineReceipt? receipt = null,
        TimeSpan? lockTimeout = null)
    {
        var target = Identity(connectionString);
        var expectation = new SchemaBaselineExpectation(
            workload, "source-20260829", "copy-plan-v1", "schema-sha256", "production-key", target.Host, target.Port, target.Database);
        return new(
            new MigrationRunnerOptions(workload, connectionString, target), expectation, receipt,
            new EcdsaSchemaBaselineReceiptVerifier("production-key", signer.ExportSubjectPublicKeyInfoPem(), TimeProvider.System),
            lockTimeout ?? TimeSpan.FromSeconds(2));
    }

    private SignedSchemaBaselineReceipt ValidReceipt(string connectionString, MigrationWorkload workload, DateTimeOffset? expiry = null)
    {
        var target = Identity(connectionString);
        var expected = new SchemaBaselineExpectation(
            workload, "source-20260829", "copy-plan-v1", "schema-sha256", "production-key", target.Host, target.Port, target.Database);
        var typedPayload = new SchemaBaselineReceiptPayload(
            "1.0", expected.WorkloadName, expected.SourceSnapshotId, expected.CopyPlanId, expected.SchemaHash, expected.AttestationKeyId,
            expected.Host, expected.Port, expected.Database, expiry ?? DateTimeOffset.UtcNow.AddMinutes(5));
        var payload = JsonSerializer.Serialize(typedPayload);
        return new(payload, Convert.ToBase64String(signer.SignData(
            SchemaBaselineReceiptCanonicalizer.CreatePayload(typedPayload), HashAlgorithmName.SHA256)));
    }

    private IEnumerable<SignedSchemaBaselineReceipt> InvalidReceipts()
    {
        var valid = ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.Quotation);
        yield return valid with { Signature = Convert.ToBase64String([1, 2, 3]) };
        yield return ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.Quotation, DateTimeOffset.UtcNow.AddMinutes(-1));
        yield return ValidReceipt(quotation.GetConnectionString(), MigrationWorkload.QuotationRequest);
    }

    private static TargetDatabaseIdentity Identity(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new(builder.Host!, builder.Port, builder.Database!);
    }

    private async Task ResetBothAsync() => await Task.WhenAll(
        ResetAsync(quotation.GetConnectionString()), ResetAsync(request.GetConnectionString()));

    private static Task ResetAsync(string connectionString) => ExecuteAsync(
        connectionString, "DROP SCHEMA public CASCADE; CREATE SCHEMA public");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ApplicationTableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' AND table_name <> '__EFMigrationsHistory' AND table_name <> 'legacy_marker'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> HistoryCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM information_schema.tables WHERE table_schema='public' AND table_name='__EFMigrationsHistory'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> DataRowCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(sum(n_live_tup),0)::bigint FROM pg_stat_user_tables WHERE relname <> '__EFMigrationsHistory'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
