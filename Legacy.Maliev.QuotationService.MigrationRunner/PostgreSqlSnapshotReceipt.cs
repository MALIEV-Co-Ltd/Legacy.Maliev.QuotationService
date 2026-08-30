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
    string Database,
    string ClusterNamespace,
    string ClusterName)
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
    string BackupObjectUri,
    long BackupObjectGeneration,
    long BackupObjectByteLength,
    string AttestationKeyId,
    string Host,
    int Port,
    string Database,
    string ClusterNamespace,
    string ClusterName,
    string ClusterUid,
    long ClusterGeneration,
    long ClusterObservedGeneration,
    DateTimeOffset ExpiresUtc);

public sealed record SignedPostgreSqlSnapshotReceipt(string Payload, string Signature);

public static class P256PublicKeyFingerprint
{
    public static bool TryCompute(string? pem, out byte[] fingerprint)
    {
        fingerprint = [];
        if (string.IsNullOrWhiteSpace(pem)) return false;
        try
        {
            ReadOnlySpan<char> characters = pem.AsSpan();
            if (!PemEncoding.TryFind(characters, out PemFields fields) ||
                !characters[fields.Label].SequenceEqual("PUBLIC KEY") ||
                !characters[..fields.Location.Start.Value].Trim().IsEmpty ||
                !characters[fields.Location.End.Value..].Trim().IsEmpty) return false;
            byte[] der = new byte[fields.DecodedDataLength];
            if (!Convert.TryFromBase64Chars(characters[fields.Base64Data], der, out int written) || written != der.Length) return false;
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out int read);
            ECParameters parameters = key.ExportParameters(false);
            if (read != der.Length || key.KeySize != 256 || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7") return false;
            fingerprint = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }
}

public static partial class PostgreSqlSnapshotReceiptCanonicalizer
{
    private const string DomainSeparator = "Legacy.Maliev.QuotationService.PostgreSqlSnapshotReceipt.v1";
    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "SchemaVersion", "Workload", "RunId", "SourceSnapshotId", "CopyPlanId", "SchemaHash", "SnapshotId",
        "RecoveryPointUtc", "SnapshotChecksumSha256", "BackupObjectUri", "BackupObjectGeneration", "BackupObjectByteLength",
        "AttestationKeyId", "Host", "Port", "Database", "ClusterNamespace", "ClusterName", "ClusterUid",
        "ClusterGeneration", "ClusterObservedGeneration", "ExpiresUtc",
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
                !Text(values, "SnapshotChecksumSha256", out string checksum) || !Text(values, "BackupObjectUri", out string objectUri) ||
                !values["BackupObjectGeneration"].TryGetInt64(out long objectGeneration) ||
                !values["BackupObjectByteLength"].TryGetInt64(out long objectByteLength) || !Text(values, "AttestationKeyId", out string key) ||
                !Text(values, "Host", out string host) || !values["Port"].TryGetInt32(out int port) || port is < 1 or > 65535 ||
                !Text(values, "Database", out string database) || values["ExpiresUtc"].ValueKind != JsonValueKind.String ||
                !Text(values, "ClusterNamespace", out string clusterNamespace) || !Text(values, "ClusterName", out string clusterName) ||
                !Text(values, "ClusterUid", out string clusterUid) || !values["ClusterGeneration"].TryGetInt64(out long clusterGeneration) ||
                !values["ClusterObservedGeneration"].TryGetInt64(out long clusterObservedGeneration) ||
                !values["ExpiresUtc"].TryGetDateTimeOffset(out DateTimeOffset expires)) return false;
            payload = new(version, workload, runId, source, plan, schema, snapshot, recovery, checksum, objectUri,
                objectGeneration, objectByteLength, key, host, port, database, clusterNamespace, clusterName, clusterUid,
                clusterGeneration, clusterObservedGeneration, expires);
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
            Write(writer, payload.SnapshotChecksumSha256); Write(writer, payload.BackupObjectUri);
            writer.Write(payload.BackupObjectGeneration); writer.Write(payload.BackupObjectByteLength);
            Write(writer, payload.AttestationKeyId); Write(writer, payload.Host);
            writer.Write(payload.Port); Write(writer, payload.Database);
            Write(writer, payload.ClusterNamespace); Write(writer, payload.ClusterName); Write(writer, payload.ClusterUid);
            writer.Write(payload.ClusterGeneration); writer.Write(payload.ClusterObservedGeneration);
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
        return Verify(receipt, expected, out _);
    }

    public ReceiptVerificationResult Verify(
        SignedPostgreSqlSnapshotReceipt receipt,
        PostgreSqlSnapshotExpectation expected,
        out PostgreSqlSnapshotReceiptPayload verifiedPayload)
    {
        verifiedPayload = null!;
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
            payload.Database != expected.Database || payload.ClusterNamespace != expected.ClusterNamespace ||
            payload.ClusterName != expected.ClusterName || !Identifier().IsMatch(payload.ClusterUid) ||
            payload.ClusterGeneration <= 0 || payload.ClusterObservedGeneration != payload.ClusterGeneration ||
            !Identifier().IsMatch(payload.SnapshotId) || !Sha256().IsMatch(payload.SnapshotChecksumSha256) ||
            !BackupUri().IsMatch(payload.BackupObjectUri) || payload.BackupObjectGeneration <= 0 || payload.BackupObjectByteLength <= 0)
            return ReceiptVerificationResult.Invalid("snapshot receipt does not match the run, target, or recovery evidence");
        try
        {
            byte[] signature = Convert.FromBase64String(receipt.Signature);
            if (!TryImportSingleP256SubjectPublicKeyInfo(publicKeyPem, out ECDsa key))
                return ReceiptVerificationResult.Invalid("snapshot trusted key is invalid");
            using (key)
            {
                if (!key.VerifyData(canonical, signature, HashAlgorithmName.SHA256))
                    return ReceiptVerificationResult.Invalid("snapshot receipt signature is invalid");
                verifiedPayload = payload;
                return ReceiptVerificationResult.Valid();
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
    [GeneratedRegex("^gs://[A-Za-z0-9._-]+/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)] private static partial Regex BackupUri();
}

public sealed class PostgreSqlSnapshotRejectedException(string message) : Exception(message);

public sealed record CloudNativePgRuntimeObservation(
    string Namespace,
    string Cluster,
    string Uid,
    long Generation,
    long ObservedGeneration,
    bool Healthy);

public interface ICloudNativePgRuntimeObserver
{
    Task<CloudNativePgRuntimeObservation> ObserveAsync(string clusterNamespace, string clusterName, CancellationToken cancellationToken);
}

public sealed class PostgreSqlSnapshotGate(
    EcdsaPostgreSqlSnapshotReceiptVerifier verifier,
    ICloudNativePgRuntimeObserver targetObserver)
{
    public async Task EnsureSafeAsync(
        PostgreSqlSnapshotExpectation expectation,
        SignedPostgreSqlSnapshotReceipt? receipt,
        CancellationToken cancellationToken)
    {
        if (receipt is null) throw new PostgreSqlSnapshotRejectedException("A signed recoverable PostgreSQL snapshot receipt is required before connecting.");
        ReceiptVerificationResult result = verifier.Verify(receipt, expectation, out PostgreSqlSnapshotReceiptPayload payload);
        if (!result.IsValid) throw new PostgreSqlSnapshotRejectedException($"The PostgreSQL snapshot receipt was rejected: {result.Reason}.");
        CloudNativePgRuntimeObservation first = await targetObserver.ObserveAsync(
            expectation.ClusterNamespace, expectation.ClusterName, cancellationToken).ConfigureAwait(false);
        CloudNativePgRuntimeObservation immediateRecheck = await targetObserver.ObserveAsync(
            expectation.ClusterNamespace, expectation.ClusterName, cancellationToken).ConfigureAwait(false);
        if (!first.Healthy || first != immediateRecheck || first.Namespace != payload.ClusterNamespace ||
            first.Cluster != payload.ClusterName || first.Uid != payload.ClusterUid || first.Generation != payload.ClusterGeneration ||
            first.ObservedGeneration != payload.ClusterObservedGeneration)
        {
            throw new PostgreSqlSnapshotRejectedException("The CloudNativePG target changed, is unhealthy, or does not match the signed snapshot evidence.");
        }
    }
}
