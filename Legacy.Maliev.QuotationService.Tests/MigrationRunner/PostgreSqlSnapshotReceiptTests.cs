using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.QuotationService.MigrationRunner;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.MigrationRunner;

public sealed class PostgreSqlSnapshotReceiptTests
{
    [Fact]
    public void Verifier_AcceptsOnlyExactSignedRunTargetAndRecoveryPoint()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var expected = Expected();
        SignedPostgreSqlSnapshotReceipt receipt = Sign(expected, now, signer);
        var verifier = new EcdsaPostgreSqlSnapshotReceiptVerifier(
            "quotation-snapshot-v1", signer.ExportSubjectPublicKeyInfoPem(), new FakeTimeProvider(now));

        Assert.True(verifier.Verify(receipt, expected).IsValid);
        Assert.False(verifier.Verify(receipt, expected with { RunId = Guid.NewGuid() }).IsValid);
        Assert.False(verifier.Verify(receipt, expected with { SchemaHash = new string('c', 64) }).IsValid);
        Assert.False(verifier.Verify(receipt, expected with { Database = "QuotationRequest" }).IsValid);
        Assert.False(verifier.Verify(receipt with { Signature = Convert.ToBase64String([1, 2, 3]) }, expected).IsValid);
    }

    [Fact]
    public void Fingerprint_EquivalentPemWrappingHasSameCanonicalIdentity()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        string compactBase64 = string.Concat(pem.Split('\n').Where(line => !line.StartsWith("---", StringComparison.Ordinal)).Select(line => line.Trim()));
        string rewrapped = $"-----BEGIN PUBLIC KEY-----\n{compactBase64[..20]}\n{compactBase64[20..]}\n-----END PUBLIC KEY-----";

        Assert.True(P256PublicKeyFingerprint.TryCompute(pem, out byte[] first));
        Assert.True(P256PublicKeyFingerprint.TryCompute(rewrapped, out byte[] second));
        Assert.True(CryptographicOperations.FixedTimeEquals(first, second));
    }

    [Fact]
    public async Task Gate_RechecksObservedClusterAndRejectsDriftBeforeConnection()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PostgreSqlSnapshotExpectation expected = Expected();
        SignedPostgreSqlSnapshotReceipt receipt = Sign(expected, now, signer);
        var observer = new SequenceObserver(
            new(expected.ClusterNamespace, expected.ClusterName, "11111111-2222-3333-4444-555555555555", 7, 7, true),
            new(expected.ClusterNamespace, expected.ClusterName, "different-uid", 8, 8, true));
        var gate = new PostgreSqlSnapshotGate(
            new("quotation-snapshot-v1", signer.ExportSubjectPublicKeyInfoPem(), TimeProvider.System), observer);

        _ = await Assert.ThrowsAsync<PostgreSqlSnapshotRejectedException>(() =>
            gate.EnsureSafeAsync(expected, receipt, CancellationToken.None));
        Assert.Equal(2, observer.Calls);
    }

    [Fact]
    public void Verifier_RejectsPrivateOrMultiplePemTrustMaterial()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var expected = Expected();
        SignedPostgreSqlSnapshotReceipt receipt = Sign(expected, now, signer);

        Assert.False(new EcdsaPostgreSqlSnapshotReceiptVerifier("quotation-snapshot-v1", signer.ExportECPrivateKeyPem(), TimeProvider.System).Verify(receipt, expected).IsValid);
        string publicPem = signer.ExportSubjectPublicKeyInfoPem();
        Assert.False(new EcdsaPostgreSqlSnapshotReceiptVerifier("quotation-snapshot-v1", publicPem + Environment.NewLine + publicPem, TimeProvider.System).Verify(receipt, expected).IsValid);
    }

    [Fact]
    public async Task Application_RejectsMissingSnapshotReceiptBeforeConnecting()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Migration__Workload"] = "quotation",
            ["ConnectionStrings__QuotationDbContext"] = "Host=never-connect;Port=5432;Database=Quotation;Username=x;Password=secret",
            ["Migration__SourceSnapshotId"] = "source-20260830",
            ["Migration__CopyPlanId"] = "copy-plan-20260830",
            ["Migration__SchemaHash"] = new string('a', 64),
            ["Migration__TrustedKeyId"] = "schema-v1",
            ["Migration__SnapshotTrustedKeyId"] = "snapshot-v1",
            ["Migration__RunId"] = Guid.NewGuid().ToString("D"),
        };
        using var output = new StringWriter();

        int exitCode = await MigrationRunnerApplication.RunAsync([], configuration, output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("PostgreSqlSnapshotRejectedException", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("never-connect", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    private static PostgreSqlSnapshotExpectation Expected() => new(
        MigrationWorkload.Quotation,
        Guid.Parse("34829fe9-1b24-42b5-8bdf-e38c9ed1e4bb"),
        "source-20260830",
        "copy-plan-20260830",
        new string('a', 64),
        "quotation-snapshot-v1",
        "legacy-postgres-pooler-rw.maliev-legacy.svc.cluster.local",
        5432,
        "Quotation",
        "maliev-legacy",
        "legacy-postgres-main");

    private static SignedPostgreSqlSnapshotReceipt Sign(
        PostgreSqlSnapshotExpectation expected,
        DateTimeOffset now,
        ECDsa signer)
    {
        var payload = new PostgreSqlSnapshotReceiptPayload(
            "1.0", expected.WorkloadName, expected.RunId.ToString("D"), expected.SourceSnapshotId,
            expected.CopyPlanId, expected.SchemaHash, "cnpg-20260830-001", now.AddMinutes(-2),
            new string('b', 64), "gs://maliev-backups/quotation/cnpg-20260830-001.dump", 42, 8192,
            expected.AttestationKeyId, expected.Host, expected.Port, expected.Database,
            expected.ClusterNamespace, expected.ClusterName, "11111111-2222-3333-4444-555555555555", 7, 7,
            now.AddMinutes(10));
        byte[] signature = signer.SignData(PostgreSqlSnapshotReceiptCanonicalizer.CreatePayload(payload), HashAlgorithmName.SHA256);
        return new(JsonSerializer.Serialize(payload), Convert.ToBase64String(signature));
    }

    private sealed class SequenceObserver(params CloudNativePgRuntimeObservation[] observations) : ICloudNativePgRuntimeObserver
    {
        public int Calls { get; private set; }
        public Task<CloudNativePgRuntimeObservation> ObserveAsync(string clusterNamespace, string clusterName, CancellationToken cancellationToken)
        {
            return Task.FromResult(observations[Calls++]);
        }
    }
}
