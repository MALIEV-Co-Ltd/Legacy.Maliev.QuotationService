using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public enum MigrationWorkload
{
    Quotation,
    QuotationRequest,
}

public sealed class MigrationConfigurationException(string message) : Exception(message);

public static class MigrationWorkloadParser
{
    public static MigrationWorkload Parse(string? value) => value switch
    {
        "quotation" => MigrationWorkload.Quotation,
        "quotation-request" => MigrationWorkload.QuotationRequest,
        _ => throw new MigrationConfigurationException("Workload must be exactly 'quotation' or 'quotation-request'."),
    };
}

public sealed record TargetDatabaseIdentity(string Host, int Port, string Database);

public sealed record MigrationRunnerOptions(MigrationWorkload Workload, string ConnectionString, TargetDatabaseIdentity TargetDatabase)
{
    public static MigrationRunnerOptions Parse(string[] args, IReadOnlyDictionary<string, string?> configuration)
    {
        configuration.TryGetValue("Migration__Workload", out var configuredWorkload);
        if (args.Length > 1 || (args.Length == 1 && !string.IsNullOrWhiteSpace(configuredWorkload)))
        {
            throw new MigrationConfigurationException("Specify the workload exactly once, as an argument or configuration value.");
        }

        var workload = MigrationWorkloadParser.Parse(args.Length == 1 ? args[0] : configuredWorkload);
        var key = workload == MigrationWorkload.Quotation
            ? "ConnectionStrings__QuotationDbContext"
            : "ConnectionStrings__QuotationRequestDbContext";
        if (!configuration.TryGetValue(key, out var connectionString) || string.IsNullOrWhiteSpace(connectionString))
        {
            throw new MigrationConfigurationException($"The selected workload requires {key}.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new MigrationConfigurationException("The selected PostgreSQL target identity is incomplete.");
        }

        return new(workload, connectionString, new(builder.Host, builder.Port, builder.Database));
    }
}

public sealed record SchemaBaselineExpectation(
    MigrationWorkload Workload,
    string SourceSnapshotId,
    string CopyPlanId,
    string SchemaHash,
    string Host,
    int Port,
    string Database)
{
    public string WorkloadName => Workload == MigrationWorkload.Quotation ? "quotation" : "quotation-request";
}

public sealed record SchemaBaselineReceiptPayload(
    string Workload,
    string SourceSnapshotId,
    string CopyPlanId,
    string SchemaHash,
    string Host,
    int Port,
    string Database,
    DateTimeOffset ExpiresUtc);

public sealed record SignedSchemaBaselineReceipt(string Payload, string Signature);

public readonly record struct ReceiptVerificationResult(bool IsValid, string Reason)
{
    public static ReceiptVerificationResult Valid() => new(true, "valid");
    public static ReceiptVerificationResult Invalid(string reason) => new(false, reason);
}

public interface ISchemaBaselineReceiptVerifier
{
    ReceiptVerificationResult Verify(SignedSchemaBaselineReceipt receipt, SchemaBaselineExpectation expected);
}

public sealed class RsaSchemaBaselineReceiptVerifier(string? trustedPublicKeyPem, TimeProvider timeProvider)
    : ISchemaBaselineReceiptVerifier
{
    public ReceiptVerificationResult Verify(SignedSchemaBaselineReceipt receipt, SchemaBaselineExpectation expected)
    {
        if (string.IsNullOrWhiteSpace(trustedPublicKeyPem))
        {
            return ReceiptVerificationResult.Invalid("trusted public key is unavailable");
        }

        SchemaBaselineReceiptPayload? payload;
        byte[] signature;
        try
        {
            payload = JsonSerializer.Deserialize<SchemaBaselineReceiptPayload>(receipt.Payload);
            signature = Convert.FromBase64String(receipt.Signature);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return ReceiptVerificationResult.Invalid("receipt encoding is invalid");
        }

        if (payload is null || payload.ExpiresUtc <= timeProvider.GetUtcNow())
        {
            return ReceiptVerificationResult.Invalid("receipt is missing or expired");
        }

        if (!Matches(payload, expected))
        {
            return ReceiptVerificationResult.Invalid("receipt target or migration identifiers do not match");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(trustedPublicKeyPem);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(receipt.Payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
                ? ReceiptVerificationResult.Valid()
                : ReceiptVerificationResult.Invalid("receipt signature is invalid");
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return ReceiptVerificationResult.Invalid("trusted key or signature is invalid");
        }
    }

    private static bool Matches(SchemaBaselineReceiptPayload payload, SchemaBaselineExpectation expected) =>
        string.Equals(payload.Workload, expected.WorkloadName, StringComparison.Ordinal) &&
        string.Equals(payload.SourceSnapshotId, expected.SourceSnapshotId, StringComparison.Ordinal) &&
        string.Equals(payload.CopyPlanId, expected.CopyPlanId, StringComparison.Ordinal) &&
        string.Equals(payload.SchemaHash, expected.SchemaHash, StringComparison.Ordinal) &&
        string.Equals(payload.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        payload.Port == expected.Port &&
        string.Equals(payload.Database, expected.Database, StringComparison.Ordinal);
}

public static class MigrationLogSanitizer
{
    public static string Sanitize(Exception _) => "Migration failed. Inspect the sanitized error category and correlation logs.";
}
