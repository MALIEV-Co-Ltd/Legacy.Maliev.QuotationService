using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Draft cryptographic recipient tests only; invoice ownership, live IAM and actual host acceptance are separate gates.</summary>
public sealed class QuotationInvoiceCapabilityVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operation = Guid.Parse("61d3b811-b1b1-4ec2-9f42-18cfd49ca7de");
    private const int Quotation = 84;
    private const int Invoice = 901;
    private const string Issuer = "https://synthetic-auth.invalid";

    [Fact]
    public void InvoiceBoundAuthExtension_VerifiesExactActorFinancialOperation()
    {
        using var fixture = new ProofFixture();
        var result = fixture.Verifier.Verify("Bearer " + fixture.Sign(fixture.Payload()), Caller(), Quotation, Operation, Invoice, Now);
        Assert.NotNull(result);
        Assert.Equal("employee-42", result.EmployeeSubject);
        Assert.Equal(Issuer, result.Issuer);
        Assert.Equal(Quotation, result.QuotationId);
        Assert.Equal(Operation, result.OperationId);
        Assert.Equal(Now.AddSeconds(60), result.ExpiresAt);
        Assert.Equal(Invoice, result.InvoiceId);
        Assert.Equal(new string('A', 64), result.FinancialBinding);
        Assert.Equal(Now, result.OriginalQuotationVersion);
    }

    public static TheoryData<string> InvalidProofs => new()
    {
        "wrong-issuer", "accounting-audience", "wrong-requester", "wrong-executor", "wrong-scope",
        "other-quotation", "other-operation", "noncanonical-operation", "empty-jti", "service-subject",
        "mixed-case-service-subject", "blank-subject", "long-subject", "expired", "future-issued",
        "future-not-before", "too-long-lifetime", "string-time", "duplicate-payload-key", "multiple-audiences",
        "unknown-field", "tampered-signature", "wrong-algorithm", "duplicate-header-key", "oversized", "malformed",
        "old-unbound", "wrong-invoice", "wrong-version", "wrong-binding-version", "lowercase-binding", "missing-binding",
    };

    [Theory]
    [MemberData(nameof(InvalidProofs))]
    public void InvalidOrOverbroadProof_FailsClosed(string scenario)
    {
        using var fixture = new ProofFixture();
        var payload = fixture.Payload();
        switch (scenario)
        {
            case "wrong-issuer": payload["iss"] = "https://other-issuer.invalid"; break;
            case "accounting-audience": payload["aud"] = "legacy-accounting:invoice-create"; break;
            case "wrong-requester": payload["azp"] = "service:legacy-accounting"; break;
            case "wrong-executor": payload["executor"] = "service:legacy-intranet"; break;
            case "wrong-scope": payload["scope"] = "legacy.quotation.*"; break;
            case "other-quotation": payload["quotation_id"] = "85"; break;
            case "other-operation": payload["operation_id"] = Guid.NewGuid().ToString("D"); break;
            case "noncanonical-operation": payload["operation_id"] = Operation.ToString("D").ToUpperInvariant(); break;
            case "empty-jti": payload["jti"] = Guid.Empty.ToString("D"); break;
            case "service-subject": payload["sub"] = "service:legacy-intranet"; break;
            case "mixed-case-service-subject": payload["sub"] = "SERVICE:legacy-intranet"; break;
            case "blank-subject": payload["sub"] = " "; break;
            case "long-subject": payload["sub"] = new string('x', 257); break;
            case "expired": payload["iat"] = Now.AddSeconds(-60).ToUnixTimeSeconds(); payload["nbf"] = payload["iat"]; payload["exp"] = Now.ToUnixTimeSeconds(); break;
            case "future-issued": payload["iat"] = Now.AddSeconds(1).ToUnixTimeSeconds(); break;
            case "future-not-before": payload["nbf"] = Now.AddSeconds(1).ToUnixTimeSeconds(); break;
            case "too-long-lifetime": payload["exp"] = Now.AddSeconds(121).ToUnixTimeSeconds(); break;
            case "string-time": payload["iat"] = Now.ToUnixTimeSeconds().ToString(); break;
            case "multiple-audiences": payload["aud"] = new[] { QuotationInvoiceCapabilityVerifier.Audience, "other" }; break;
            case "unknown-field": payload["unexpected"] = "value"; break;
            case "old-unbound": payload.Remove("invoice_id"); payload.Remove("quotation_version"); payload.Remove("financial_binding"); payload.Remove("financial_binding_version"); break;
            case "wrong-invoice": payload["invoice_id"] = "902"; break;
            case "wrong-version": payload["quotation_version"] = Now.AddSeconds(1).UtcDateTime.ToString("O"); break;
            case "wrong-binding-version": payload["financial_binding_version"] = "invoice-creation-financial-v2"; break;
            case "lowercase-binding": payload["financial_binding"] = new string('a', 64); break;
            case "missing-binding": payload.Remove("financial_binding"); break;
        }
        var token = scenario switch
        {
            "duplicate-payload-key" => fixture.SignJson(JsonSerializer.Serialize(payload).Replace("\"sub\":\"employee-42\"", "\"sub\":\"employee-42\",\"sub\":\"employee-42\"", StringComparison.Ordinal)),
            "tampered-signature" => fixture.Sign(payload, otherKey: true),
            "wrong-algorithm" => fixture.Sign(payload, algorithm: "RS512"),
            "duplicate-header-key" => fixture.SignJson(JsonSerializer.Serialize(payload), rawHeader: "{\"alg\":\"RS256\",\"alg\":\"RS256\",\"typ\":\"JWT\"}"),
            "oversized" => new string('x', 16385),
            "malformed" => "not.a.jwt",
            _ => fixture.Sign(payload),
        };
        Assert.Null(fixture.Verifier.Verify("Bearer " + token, Caller(), Quotation, Operation, Invoice, Now));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("intranet")]
    [InlineData("employee")]
    [InlineData("duplicate-sub")]
    [InlineData("duplicate-kind")]
    [InlineData("wrong-alias")]
    public void CapabilityCannotAuthorizeAnotherOrAmbiguousSender(string scenario)
    {
        using var fixture = new ProofFixture();
        var claims = new List<Claim> { new("sub", QuotationInvoiceCapabilityVerifier.Executor), new("identity_kind", "service") };
        switch (scenario)
        {
            case "intranet": claims[0] = new("sub", QuotationInvoiceCapabilityVerifier.Requester); break;
            case "employee": claims[0] = new("sub", "employee-42"); claims[1] = new("identity_kind", "employee"); break;
            case "duplicate-sub": claims.Add(claims[0]); break;
            case "duplicate-kind": claims.Add(claims[1]); break;
            case "wrong-alias": claims.Add(new("user_id", "employee-42")); break;
        }
        var caller = new ClaimsPrincipal(new ClaimsIdentity(claims, scenario == "anonymous" ? null : "unit-sender-shape"));
        Assert.Null(fixture.Verifier.Verify("Bearer " + fixture.Sign(fixture.Payload()), caller, Quotation, Operation, Invoice, Now));
    }

    [Fact]
    public void FreshCapabilityForSameOperation_HasNewNonceAndDoesNotRetainExpiredBearer()
    {
        using var fixture = new ProofFixture();
        var first = fixture.Payload();
        var next = fixture.Payload();
        next["jti"] = Guid.NewGuid().ToString("D");
        var a = fixture.Verifier.Verify("Bearer " + fixture.Sign(first), Caller(), Quotation, Operation, Invoice, Now);
        var b = fixture.Verifier.Verify("Bearer " + fixture.Sign(next), Caller(), Quotation, Operation, Invoice, Now);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.OperationId, b.OperationId);
        Assert.NotEqual(a.CapabilityId, b.CapabilityId);
        // No global one-use JTI fence: operation/invoice replay is owned by the durable financial admission.
    }

    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity([
        new("sub", QuotationInvoiceCapabilityVerifier.Executor), new("identity_kind", "service")], "unit-sender-shape"));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ProofFixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        internal QuotationInvoiceCapabilityVerifier Verifier { get; }
        internal ProofFixture()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
            }).Build();
            Verifier = new(configuration, new FixedClock());
        }
        internal Dictionary<string, object> Payload() => new()
        {
            ["iss"] = Issuer,
            ["aud"] = QuotationInvoiceCapabilityVerifier.Audience,
            ["sub"] = "employee-42",
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["nbf"] = Now.ToUnixTimeSeconds(),
            ["exp"] = Now.AddSeconds(60).ToUnixTimeSeconds(),
            ["azp"] = QuotationInvoiceCapabilityVerifier.Requester,
            ["executor"] = QuotationInvoiceCapabilityVerifier.Executor,
            ["scope"] = QuotationInvoiceCapabilityVerifier.Scope,
            ["quotation_id"] = Quotation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["operation_id"] = Operation.ToString("D"),
            ["invoice_id"] = Invoice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["quotation_version"] = Now.UtcDateTime.ToString("O"),
            ["financial_binding"] = new string('A', 64),
            ["financial_binding_version"] = "invoice-creation-financial-v1",
        };
        internal string Sign(Dictionary<string, object> payload, bool otherKey = false, string algorithm = "RS256")
        {
            if (!otherKey) return SignJson(JsonSerializer.Serialize(payload), algorithm);
            using var other = RSA.Create(2048);
            return SignJson(JsonSerializer.Serialize(payload), algorithm, other);
        }
        internal string SignJson(string json, string algorithm = "RS256", RSA? signer = null, string? rawHeader = null)
        {
            var header = Base64UrlEncoder.Encode(rawHeader ?? JsonSerializer.Serialize(new { alg = algorithm, typ = "JWT" }));
            var payload = Base64UrlEncoder.Encode(json);
            var input = header + "." + payload;
            var signature = (signer ?? rsa).SignData(Encoding.ASCII.GetBytes(input),
                algorithm == "RS512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return input + "." + Base64UrlEncoder.Encode(signature);
        }
        public void Dispose() => rsa.Dispose();
    }
}
