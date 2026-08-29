using System.Security.Cryptography;
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
    string AttestationKeyId,
    string Host,
    int Port,
    string Database)
{
    public string WorkloadName => Workload == MigrationWorkload.Quotation ? "quotation" : "quotation-request";
}

public sealed record SchemaBaselineReceiptPayload(
    string SchemaVersion,
    string Workload,
    string SourceSnapshotId,
    string CopyPlanId,
    string SchemaHash,
    string AttestationKeyId,
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

public sealed class EcdsaSchemaBaselineReceiptVerifier(
    string trustedKeyId,
    string? trustedPublicKeyPem,
    TimeProvider timeProvider)
    : ISchemaBaselineReceiptVerifier
{
    public ReceiptVerificationResult Verify(SignedSchemaBaselineReceipt receipt, SchemaBaselineExpectation expected)
    {
        if (string.IsNullOrWhiteSpace(trustedPublicKeyPem))
        {
            return ReceiptVerificationResult.Invalid("trusted public key is unavailable");
        }

        SchemaBaselineReceiptPayload payload;
        byte[] canonicalPayload;
        byte[] signature;
        try
        {
            if (!SchemaBaselineReceiptCanonicalizer.TryParseAndCreatePayload(receipt.Payload, out payload, out canonicalPayload))
            {
                return ReceiptVerificationResult.Invalid("receipt schema or encoding is invalid");
            }

            signature = Convert.FromBase64String(receipt.Signature);
        }
        catch (FormatException)
        {
            return ReceiptVerificationResult.Invalid("receipt encoding is invalid");
        }

        if (payload.ExpiresUtc <= timeProvider.GetUtcNow())
        {
            return ReceiptVerificationResult.Invalid("receipt is missing or expired");
        }

        if (!Matches(payload, expected))
        {
            return ReceiptVerificationResult.Invalid("receipt target or migration identifiers do not match");
        }

        try
        {
            if (!TryImportSingleP256SubjectPublicKeyInfo(trustedPublicKeyPem, out var ecdsa))
            {
                return ReceiptVerificationResult.Invalid("trusted key encoding, algorithm, or curve is invalid");
            }

            using (ecdsa)
            {
                var curve = ecdsa.ExportParameters(false).Curve;
                if (ecdsa.KeySize != 256 || !string.Equals(curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal))
                {
                    return ReceiptVerificationResult.Invalid("trusted key algorithm or curve is invalid");
                }

                return ecdsa.VerifyData(canonicalPayload, signature, HashAlgorithmName.SHA256)
                    ? ReceiptVerificationResult.Valid()
                    : ReceiptVerificationResult.Invalid("receipt signature is invalid");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return ReceiptVerificationResult.Invalid("trusted key or signature is invalid");
        }
    }

    private bool Matches(SchemaBaselineReceiptPayload payload, SchemaBaselineExpectation expected) =>
        string.Equals(payload.SchemaVersion, "1.0", StringComparison.Ordinal) &&
        string.Equals(payload.Workload, expected.WorkloadName, StringComparison.Ordinal) &&
        string.Equals(payload.SourceSnapshotId, expected.SourceSnapshotId, StringComparison.Ordinal) &&
        string.Equals(payload.CopyPlanId, expected.CopyPlanId, StringComparison.Ordinal) &&
        string.Equals(payload.SchemaHash, expected.SchemaHash, StringComparison.Ordinal) &&
        string.Equals(payload.AttestationKeyId, expected.AttestationKeyId, StringComparison.Ordinal) &&
        string.Equals(payload.AttestationKeyId, trustedKeyId, StringComparison.Ordinal) &&
        string.Equals(payload.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        payload.Port == expected.Port &&
        string.Equals(payload.Database, expected.Database, StringComparison.Ordinal);

    private static bool TryImportSingleP256SubjectPublicKeyInfo(string pem, out ECDsa ecdsa)
    {
        ecdsa = ECDsa.Create();
        try
        {
            var characters = pem.AsSpan();
            if (!PemEncoding.TryFind(characters, out var fields) ||
                !characters[fields.Label].SequenceEqual("PUBLIC KEY") ||
                !characters[..fields.Location.Start.Value].Trim().IsEmpty ||
                !characters[fields.Location.End.Value..].Trim().IsEmpty)
            {
                ecdsa.Dispose();
                ecdsa = null!;
                return false;
            }

            var subjectPublicKeyInfo = new byte[fields.DecodedDataLength];
            if (!Convert.TryFromBase64Chars(characters[fields.Base64Data], subjectPublicKeyInfo, out var bytesWritten) ||
                bytesWritten != subjectPublicKeyInfo.Length)
            {
                ecdsa.Dispose();
                ecdsa = null!;
                return false;
            }

            ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            var curve = ecdsa.ExportParameters(false).Curve;
            if (bytesRead != subjectPublicKeyInfo.Length || ecdsa.KeySize != 256 ||
                !string.Equals(curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal))
            {
                ecdsa.Dispose();
                ecdsa = null!;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            ecdsa.Dispose();
            ecdsa = null!;
            return false;
        }
    }
}

public static class MigrationLogSanitizer
{
    public static string Sanitize(Exception _) => "Migration failed. Inspect the sanitized error category and correlation logs.";
}
