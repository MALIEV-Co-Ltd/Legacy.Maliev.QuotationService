using System.Globalization;

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
            var runner = new QuotationMigrationRunner(
                options,
                expectation,
                receipt,
                new EcdsaSchemaBaselineReceiptVerifier(expectation.AttestationKeyId, publicKey, TimeProvider.System),
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
