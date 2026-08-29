using System.Diagnostics;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public sealed class MigrationLockUnavailableException(string message) : Exception(message);

public sealed class QuotationMigrationRunner
{
    private readonly MigrationRunnerOptions options;
    private readonly SchemaBaselineExpectation expectation;
    private readonly SignedSchemaBaselineReceipt? receipt;
    private readonly SchemaBaselineGate baselineGate;
    private readonly TimeSpan lockTimeout;

    public QuotationMigrationRunner(
        MigrationRunnerOptions options,
        SchemaBaselineExpectation expectation,
        SignedSchemaBaselineReceipt? receipt,
        ISchemaBaselineReceiptVerifier receiptVerifier,
        TimeSpan lockTimeout)
    {
        this.options = options;
        this.expectation = expectation;
        this.receipt = receipt;
        baselineGate = new(receiptVerifier);
        this.lockTimeout = lockTimeout;
        ValidateConfiguration();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var lockName = $"legacy-maliev-quotation:migration:{expectation.WorkloadName}";
        await AcquireLockAsync(connection, lockName, cancellationToken);
        try
        {
            await baselineGate.EnsureSafeAsync(connection, expectation, receipt, cancellationToken);
            await MigrateSelectedContextAsync(connection, cancellationToken);
        }
        finally
        {
            await ReleaseLockAsync(connection, lockName);
        }
    }

    private async Task MigrateSelectedContextAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (options.Workload == MigrationWorkload.Quotation)
        {
            await using var context = new QuotationDbContext(
                new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(connection).Options);
            await context.Database.MigrateAsync(cancellationToken);
            return;
        }

        await using var requestContext = new QuotationRequestDbContext(
            new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(connection).Options);
        await requestContext.Database.MigrateAsync(cancellationToken);
    }

    private async Task AcquireLockAsync(NpgsqlConnection connection, string lockName, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@name))", connection);
            command.Parameters.AddWithValue("name", lockName);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return;
            }

            if (stopwatch.Elapsed >= lockTimeout)
            {
                throw new MigrationLockUnavailableException("The selected migration workload is already running.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, string lockName)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtext(@name))", connection);
        command.Parameters.AddWithValue("name", lockName);
        await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private void ValidateConfiguration()
    {
        if (options.Workload != expectation.Workload ||
            !string.Equals(options.TargetDatabase.Host, expectation.Host, StringComparison.OrdinalIgnoreCase) ||
            options.TargetDatabase.Port != expectation.Port ||
            !string.Equals(options.TargetDatabase.Database, expectation.Database, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(expectation.SourceSnapshotId) ||
            string.IsNullOrWhiteSpace(expectation.CopyPlanId) ||
            string.IsNullOrWhiteSpace(expectation.SchemaHash) ||
            lockTimeout <= TimeSpan.Zero || lockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new MigrationConfigurationException("Migration target, attestation identifiers, or lock timeout are invalid or ambiguous.");
        }
    }
}
