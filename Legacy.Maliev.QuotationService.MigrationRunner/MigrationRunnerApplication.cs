using System.Globalization;
using System.Security.Cryptography;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public static class MigrationRunnerApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> configuration,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = MigrationRunnerOptions.Parse(args, configuration);
            var expectation = new SchemaBaselineExpectation(
                options.Workload,
                Require(configuration, "Migration__SourceSnapshotId"),
                Require(configuration, "Migration__CopyPlanId"),
                Require(configuration, "Migration__SchemaHash"),
                Require(configuration, "Migration__TrustedKeyId"),
                options.TargetDatabase.Host,
                options.TargetDatabase.Port,
                options.TargetDatabase.Database);
            var lockTimeout = ParseLockTimeout(configuration);
            var receipt = await ReadReceiptAsync(configuration, cancellationToken);
            var publicKey = await ReadOptionalFileAsync(configuration, "Migration__TrustedPublicKeyPath", cancellationToken);
            var snapshotKeyId = Require(configuration, "Migration__SnapshotTrustedKeyId");
            var snapshotReceipt = await ReadSnapshotReceiptAsync(configuration, cancellationToken);
            if (snapshotReceipt is null)
            {
                throw new PostgreSqlSnapshotRejectedException("A signed recoverable PostgreSQL snapshot receipt is required before target observation or connection.");
            }
            if (string.Equals(snapshotKeyId, expectation.AttestationKeyId, StringComparison.Ordinal))
            {
                throw new MigrationConfigurationException("Schema-baseline and PostgreSQL snapshot trust roles must use different keys.");
            }
            var snapshotPublicKey = await ReadOptionalFileAsync(configuration, "Migration__SnapshotTrustedPublicKeyPath", cancellationToken);
            if (!P256PublicKeyFingerprint.TryCompute(publicKey, out byte[] schemaFingerprint) ||
                !P256PublicKeyFingerprint.TryCompute(snapshotPublicKey, out byte[] snapshotFingerprint))
            {
                throw new MigrationConfigurationException("Schema-baseline and PostgreSQL snapshot trust keys must be exact P-256 SPKI public keys.");
            }
            if (CryptographicOperations.FixedTimeEquals(schemaFingerprint, snapshotFingerprint))
            {
                throw new MigrationConfigurationException("Schema-baseline and PostgreSQL snapshot trust material must be distinct.");
            }
            var snapshotExpectation = new PostgreSqlSnapshotExpectation(
                options.Workload, ParseRunId(configuration), expectation.SourceSnapshotId, expectation.CopyPlanId,
                expectation.SchemaHash, snapshotKeyId, expectation.Host, expectation.Port, expectation.Database,
                Require(configuration, "Migration__ClusterNamespace"), Require(configuration, "Migration__ClusterName"));
            var snapshotVerifier = new EcdsaPostgreSqlSnapshotReceiptVerifier(snapshotKeyId, snapshotPublicKey, TimeProvider.System);
            ReceiptVerificationResult snapshotVerification = snapshotVerifier.Verify(snapshotReceipt, snapshotExpectation);
            if (!snapshotVerification.IsValid)
            {
                throw new PostgreSqlSnapshotRejectedException($"The PostgreSQL snapshot receipt was rejected before target observation: {snapshotVerification.Reason}.");
            }
            using var targetObserver = new InClusterCloudNativePgRuntimeObserver();
            var runner = new QuotationMigrationRunner(
                options,
                expectation,
                receipt,
                new EcdsaSchemaBaselineReceiptVerifier(expectation.AttestationKeyId, publicKey, TimeProvider.System),
                snapshotExpectation,
                snapshotReceipt,
                snapshotVerifier,
                targetObserver,
                lockTimeout);

            await runner.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Migration cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Migration failed ({exception.GetType().Name}). No database credentials or target coordinates were logged.");
            return 2;
        }
    }

    private static string Require(IReadOnlyDictionary<string, string?> configuration, string key)
    {
        if (!configuration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new MigrationConfigurationException($"Required migration configuration {key} is missing.");
        }

        return value;
    }

    private static Guid ParseRunId(IReadOnlyDictionary<string, string?> configuration)
    {
        string value = Require(configuration, "Migration__RunId");
        if (!Guid.TryParseExact(value, "D", out Guid runId) || runId == Guid.Empty)
        {
            throw new MigrationConfigurationException("Migration run identity must be a non-empty canonical UUID.");
        }
        return runId;
    }

    private static TimeSpan ParseLockTimeout(IReadOnlyDictionary<string, string?> configuration)
    {
        if (!configuration.TryGetValue("Migration__LockTimeoutSeconds", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 30)
        {
            throw new MigrationConfigurationException("Migration lock timeout must be between 1 and 30 seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<SignedSchemaBaselineReceipt?> ReadReceiptAsync(
        IReadOnlyDictionary<string, string?> configuration,
        CancellationToken cancellationToken)
    {
        var json = await ReadOptionalFileAsync(configuration, "Migration__ReceiptPath", cancellationToken);
        if (json is null)
        {
            return null;
        }

        if (!SignedSchemaBaselineReceiptParser.TryParse(json, out var receipt))
        {
            throw new MigrationConfigurationException("The schema-baseline receipt envelope is invalid.");
        }

        return receipt;
    }

    private static async Task<SignedPostgreSqlSnapshotReceipt?> ReadSnapshotReceiptAsync(
        IReadOnlyDictionary<string, string?> configuration,
        CancellationToken cancellationToken)
    {
        string? json = await ReadOptionalFileAsync(configuration, "Migration__SnapshotReceiptPath", cancellationToken);
        if (json is null) return null;
        if (!SignedSchemaBaselineReceiptParser.TryParse(json, out SignedSchemaBaselineReceipt envelope))
        {
            throw new MigrationConfigurationException("The PostgreSQL snapshot receipt envelope is invalid.");
        }
        return new(envelope.Payload, envelope.Signature);
    }

    private static async Task<string?> ReadOptionalFileAsync(
        IReadOnlyDictionary<string, string?> configuration,
        string key,
        CancellationToken cancellationToken)
    {
        if (!configuration.TryGetValue(key, out var path) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }
}
