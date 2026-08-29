using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.MigrationRunner;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.MigrationRunner;

public sealed class MigrationRunnerContractTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));

    [Theory]
    [InlineData("quotation", MigrationWorkload.Quotation)]
    [InlineData("quotation-request", MigrationWorkload.QuotationRequest)]
    public void WorkloadParser_AcceptsOnlyFrozenNames(string value, MigrationWorkload expected) =>
        Assert.Equal(expected, MigrationWorkloadParser.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Quotation")]
    [InlineData("quotation_request")]
    [InlineData("both")]
    public void WorkloadParser_RejectsMissingOrAmbiguousNames(string? value) =>
        Assert.Throws<MigrationConfigurationException>(() => MigrationWorkloadParser.Parse(value));

    [Fact]
    public void Options_RequireOnlySelectedConnectionString()
    {
        var quotation = MigrationRunnerOptions.Parse(
            ["quotation"], NameValue("ConnectionStrings__QuotationDbContext", Connection("quotation")));
        var request = MigrationRunnerOptions.Parse(
            ["quotation-request"], NameValue("ConnectionStrings__QuotationRequestDbContext", Connection("quotation-request")));

        Assert.Equal("quotation", quotation.TargetDatabase.Database);
        Assert.Equal("quotation-request", request.TargetDatabase.Database);
    }

    [Fact]
    public void Options_AcceptExactConfiguredWorkloadWhenArgumentIsAbsent()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Migration__Workload"] = "quotation-request",
            ["ConnectionStrings__QuotationRequestDbContext"] = Connection("quotation-request"),
        };

        var options = MigrationRunnerOptions.Parse([], configuration);
        Assert.Equal(MigrationWorkload.QuotationRequest, options.Workload);
    }

    [Fact]
    public void Options_RejectAmbiguousArgumentAndConfiguredWorkload()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Migration__Workload"] = "quotation-request",
            ["ConnectionStrings__QuotationDbContext"] = Connection("quotation"),
        };

        Assert.Throws<MigrationConfigurationException>(() => MigrationRunnerOptions.Parse(["quotation"], configuration));
    }

    [Fact]
    public void ReceiptVerifier_RequiresValidUnexpiredTargetBoundSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var expected = Expected("quotation");
        var receipt = Sign(expected, now.AddMinutes(5), signer);
        var verifier = new EcdsaSchemaBaselineReceiptVerifier("production-key", signer.ExportSubjectPublicKeyInfoPem(), time);

        Assert.True(verifier.Verify(receipt, expected).IsValid);
        Assert.False(verifier.Verify(receipt, expected with { Database = "other" }).IsValid);
        Assert.False(verifier.Verify(receipt, expected with { AttestationKeyId = "caller-selected-key" }).IsValid);
        Assert.False(verifier.Verify(receipt with { Signature = Convert.ToBase64String([1, 2, 3]) }, expected).IsValid);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(verifier.Verify(receipt, expected).IsValid);
    }

    [Fact]
    public void ReceiptVerifier_FailsClosedWithoutTrustedPublicKey()
    {
        var verifier = new EcdsaSchemaBaselineReceiptVerifier("production-key", null, TimeProvider.System);
        Assert.False(verifier.Verify(new SignedSchemaBaselineReceipt("{}", "invalid"), Expected("quotation")).IsValid);
    }

    [Fact]
    public void ReceiptVerifier_FailsClosedWithMalformedTrustedPublicKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expected = Expected("quotation");
        var verifier = new EcdsaSchemaBaselineReceiptVerifier("production-key", "not-a-public-key", TimeProvider.System);
        Assert.False(verifier.Verify(Sign(expected, DateTimeOffset.UtcNow.AddMinutes(5), signer), expected).IsValid);
    }

    [Fact]
    public void Redaction_NeverReturnsCredentialsOrRawConnectionString()
    {
        var secret = Connection("quotation");
        var message = MigrationLogSanitizer.Sanitize(new InvalidOperationException($"failed {secret} Password=super-secret"));
        Assert.DoesNotContain("super-secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Application_RejectsUnknownWorkloadBeforeConnectionAndSanitizesOutput()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Migration__Workload"] = "both",
            ["ConnectionStrings__QuotationDbContext"] = "Host=never-connect;Password=must-not-leak",
        };
        using var output = new StringWriter();

        var exitCode = await MigrationRunnerApplication.RunAsync([], configuration, output, CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("never-connect", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_IsDedicatedNonRootAndContainsNoDeploymentOrCredentialMaterial()
    {
        var dockerfile = File.ReadAllText(Path.Combine(
            RepositoryRoot, "Legacy.Maliev.QuotationService.MigrationRunner", "Dockerfile"));
        Assert.Contains("mcr.microsoft.com/dotnet/runtime:10.0", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY Directory.Build.props .", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Legacy.Maliev.QuotationService.MigrationRunner.dll", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("kubectl", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gcloud", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings__", dockerfile, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string?> NameValue(string key, string value) =>
        new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value };

    private static string Connection(string database) => $"Host=localhost;Port=5432;Database={database};Username=runner;Password=super-secret";

    private static SchemaBaselineExpectation Expected(string database) => new(
        MigrationWorkload.Quotation, "source-20260829", "copy-plan-v1", "schema-sha256", "production-key", "localhost", 5432, database);

    private static SignedSchemaBaselineReceipt Sign(
        SchemaBaselineExpectation expected,
        DateTimeOffset expiresUtc,
        ECDsa signer)
    {
        var payload = JsonSerializer.Serialize(new SchemaBaselineReceiptPayload(
            expected.WorkloadName, expected.SourceSnapshotId, expected.CopyPlanId, expected.SchemaHash, expected.AttestationKeyId,
            expected.Host, expected.Port, expected.Database, expiresUtc));
        var signature = signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256);
        return new(payload, Convert.ToBase64String(signature));
    }
}
