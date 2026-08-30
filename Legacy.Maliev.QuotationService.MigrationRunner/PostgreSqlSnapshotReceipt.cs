using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public sealed record PostgreSqlSnapshotExpectation(
    MigrationWorkload Workload,
    Guid RunId,
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

public sealed record PostgreSqlSnapshotReceiptPayload(
    string SchemaVersion,
    string Workload,
    string RunId,
    string SourceSnapshotId,
    string CopyPlanId,
    string SchemaHash,
    string SnapshotId,
    DateTimeOffset RecoveryPointUtc,
    string SnapshotChecksumSha256,
    string AttestationKeyId,
    string Host,
    int Port,
    string Database,
    DateTimeOffset ExpiresUtc);

public sealed record SignedPostgreSqlSnapshotReceipt(string Payload, string Signature);

public static partial class PostgreSqlSnapshotReceiptCanonicalizer
{
    private const string DomainSeparator = "Legacy.Maliev.QuotationService.PostgreSqlSnapshotReceipt.v1";
    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "SchemaVersion", "Workload", "RunId", "SourceSnapshotId", "CopyPlanId", "SchemaHash", "SnapshotId",
        "RecoveryPointUtc", "SnapshotChecksumSha256", "AttestationKeyId", "Host", "Port", "Database", "ExpiresUtc",
    };

    public static bool TryParseAndCreatePayload(string json, out PostgreSqlSnapshotReceiptPayload payload, out byte[] canonical)
    {
        payload = null!;
        canonical = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (!Required.Contains(property.Name) || !values.TryAdd(property.Name, property.Value)) return false;
            if (values.Count != Required.Count || !Text(values, "SchemaVersion", out string version) ||
                !Text(values, "Workload", out string workload) || !Text(values, "RunId", out string runId) ||
                !Text(values, "SourceSnapshotId", out string source) || !Text(values, "CopyPlanId", out string plan) ||
                !Text(values, "SchemaHash", out string schema) || !Text(values, "SnapshotId", out string snapshot) ||
                values["RecoveryPointUtc"].ValueKind != JsonValueKind.String || !values["RecoveryPointUtc"].TryGetDateTimeOffset(out DateTimeOffset recovery) ||
                !Text(values, "SnapshotChecksumSha256", out string checksum) || !Text(values, "AttestationKeyId", out string key) ||
                !Text(values, "Host", out string host) || !values["Port"].TryGetInt32(out int port) || port is < 1 or > 65535 ||
                !Text(values, "Database", out string database) || values["ExpiresUtc"].ValueKind != JsonValueKind.String ||
                !values["ExpiresUtc"].TryGetDateTimeOffset(out DateTimeOffset expires)) return false;
            payload = new(version, workload, runId, source, plan, schema, snapshot, recovery, checksum, key, host, port, database, expires);
            canonical = CreatePayload(payload);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static byte[] CreatePayload(PostgreSqlSnapshotReceiptPayload payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
        {
            Write(writer, DomainSeparator); Write(writer, payload.SchemaVersion); Write(writer, payload.Workload);
            Write(writer, payload.RunId); Write(writer, payload.SourceSnapshotId); Write(writer, payload.CopyPlanId);
            Write(writer, payload.SchemaHash); Write(writer, payload.SnapshotId);
            Write(writer, payload.RecoveryPointUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Write(writer, payload.SnapshotChecksumSha256); Write(writer, payload.AttestationKeyId); Write(writer, payload.Host);
            writer.Write(payload.Port); Write(writer, payload.Database);
            Write(writer, payload.ExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        return stream.ToArray();
    }

    private static bool Text(IReadOnlyDictionary<string, JsonElement> values, string name, out string value)
    { value = values[name].ValueKind == JsonValueKind.String ? values[name].GetString() ?? "" : ""; return !string.IsNullOrWhiteSpace(value); }
    private static void Write(BinaryWriter writer, string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
}

public sealed partial class EcdsaPostgreSqlSnapshotReceiptVerifier(string trustedKeyId, string? publicKeyPem, TimeProvider timeProvider)
{
    public ReceiptVerificationResult Verify(SignedPostgreSqlSnapshotReceipt receipt, PostgreSqlSnapshotExpectation expected)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) ||
            !PostgreSqlSnapshotReceiptCanonicalizer.TryParseAndCreatePayload(receipt.Payload, out var payload, out var canonical))
            return ReceiptVerificationResult.Invalid("snapshot receipt schema or trust material is invalid");
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (payload.SchemaVersion != "1.0" || payload.ExpiresUtc.Offset != TimeSpan.Zero || payload.RecoveryPointUtc.Offset != TimeSpan.Zero ||
            payload.ExpiresUtc <= now || payload.RecoveryPointUtc > now || now - payload.RecoveryPointUtc > TimeSpan.FromHours(24) ||
            !Guid.TryParseExact(payload.RunId, "D", out Guid runId) || runId != expected.RunId ||
            payload.Workload != expected.WorkloadName || payload.SourceSnapshotId != expected.SourceSnapshotId ||
            payload.CopyPlanId != expected.CopyPlanId || payload.SchemaHash != expected.SchemaHash ||
            payload.AttestationKeyId != expected.AttestationKeyId || payload.AttestationKeyId != trustedKeyId ||
            !string.Equals(payload.Host, expected.Host, StringComparison.OrdinalIgnoreCase) || payload.Port != expected.Port ||
            payload.Database != expected.Database || !Identifier().IsMatch(payload.SnapshotId) || !Sha256().IsMatch(payload.SnapshotChecksumSha256))
            return ReceiptVerificationResult.Invalid("snapshot receipt does not match the run, target, or recovery evidence");
        try
        {
            byte[] signature = Convert.FromBase64String(receipt.Signature);
            if (!TryImportSingleP256SubjectPublicKeyInfo(publicKeyPem, out ECDsa key))
                return ReceiptVerificationResult.Invalid("snapshot trusted key is invalid");
            using (key)
            {
                return key.VerifyData(canonical, signature, HashAlgorithmName.SHA256)
                    ? ReceiptVerificationResult.Valid()
                    : ReceiptVerificationResult.Invalid("snapshot receipt signature is invalid");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        { return ReceiptVerificationResult.Invalid("snapshot receipt signature or key is invalid"); }
    }

    private static bool TryImportSingleP256SubjectPublicKeyInfo(string pem, out ECDsa key)
    {
        key = ECDsa.Create();
        try
        {
            ReadOnlySpan<char> characters = pem.AsSpan();
            if (!PemEncoding.TryFind(characters, out PemFields fields) ||
                !characters[fields.Label].SequenceEqual("PUBLIC KEY") ||
                !characters[..fields.Location.Start.Value].Trim().IsEmpty ||
                !characters[fields.Location.End.Value..].Trim().IsEmpty)
                throw new CryptographicException();
            byte[] subjectPublicKeyInfo = new byte[fields.DecodedDataLength];
            if (!Convert.TryFromBase64Chars(characters[fields.Base64Data], subjectPublicKeyInfo, out int written) || written != subjectPublicKeyInfo.Length)
                throw new CryptographicException();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int read);
            ECParameters parameters = key.ExportParameters(false);
            if (read != subjectPublicKeyInfo.Length || key.KeySize != 256 || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException();
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            key.Dispose(); key = null!; return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256();
}

public sealed class PostgreSqlSnapshotRejectedException(string message) : Exception(message);

public sealed class PostgreSqlSnapshotGate(EcdsaPostgreSqlSnapshotReceiptVerifier verifier)
{
    public void EnsureSafe(PostgreSqlSnapshotExpectation expectation, SignedPostgreSqlSnapshotReceipt? receipt)
    {
        if (receipt is null) throw new PostgreSqlSnapshotRejectedException("A signed recoverable PostgreSQL snapshot receipt is required before connecting.");
        ReceiptVerificationResult result = verifier.Verify(receipt, expectation);
        if (!result.IsValid) throw new PostgreSqlSnapshotRejectedException($"The PostgreSQL snapshot receipt was rejected: {result.Reason}.");
    }
}
