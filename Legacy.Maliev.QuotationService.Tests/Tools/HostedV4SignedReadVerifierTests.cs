using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Cloud.Storage.V1;
using InvoiceCompletionProducerAcceptance.Companion;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

/// <summary>Real offline Google UrlSigner signatures; not a live File/backend acceptance claim.</summary>
public sealed class HostedV4SignedReadVerifierTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", false)]
    [InlineData("[::1]", true)]
    public void ActualSdkSignatureWithDispositionAndOptionalGenerationIsAccepted(string host, bool generation)
    {
        using var fixture = new SignedFixture(host, generation);
        Assert.True(fixture.Verifier.Authorize("GET", fixture.Origin.Authority, fixture.Target, fixture.SignedAt.AddSeconds(1)));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("path")]
    [InlineData("generation")]
    [InlineData("disposition")]
    [InlineData("date")]
    [InlineData("expires")]
    [InlineData("duplicate")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    public void ActualSdkSignatureCannotAuthorizeMutatedRequest(string mutation)
    {
        using var fixture = new SignedFixture("127.0.0.1", true);
        string target = fixture.Target;
        target = mutation switch
        {
            "signature" => ChangeSignature(target),
            "path" => target.Replace("invoice.pdf", "other.pdf", StringComparison.Ordinal),
            "generation" => target.Replace("generation=17", "generation=18", StringComparison.Ordinal),
            "disposition" => target.Replace("attachment", "inline", StringComparison.Ordinal),
            "date" => ChangeParameter(target, "X-Goog-Date", fixture.SignedAt.AddSeconds(1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)),
            "expires" => ChangeParameter(target, "X-Goog-Expires", "121"),
            "duplicate" => target + "&X-Goog-Expires=120",
            "algorithm" => target.Replace("GOOG4-RSA-SHA256", "GOOG4-HMAC-SHA256", StringComparison.Ordinal),
            "unsigned" => target[..target.IndexOf('?')],
            _ => throw new InvalidDataException(),
        };
        Assert.NotEqual(fixture.Target, target);
        Assert.False(fixture.Verifier.Authorize("GET", fixture.Origin.Authority, target, fixture.SignedAt.AddSeconds(1)));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("method")]
    [InlineData("notYetValid")]
    [InlineData("expired")]
    [InlineData("lease")]
    public void ActualSignatureStillRequiresOriginMethodAndCurrentFiniteLease(string mutation)
    {
        using var fixture = new SignedFixture("127.0.0.1", true, mutation == "lease" ? 1 : 1800);
        string host = mutation switch { "host" => "localhost:45001", "port" => "127.0.0.1:45002", _ => fixture.Origin.Authority };
        string method = mutation == "method" ? "POST" : "GET";
        DateTimeOffset now = mutation switch
        {
            "notYetValid" => fixture.SignedAt.AddSeconds(-1),
            "expired" => fixture.SignedAt.AddSeconds(120),
            "lease" => fixture.SignedAt.AddSeconds(1),
            _ => fixture.SignedAt.AddSeconds(1),
        };
        Assert.False(fixture.Verifier.Authorize(method, host, fixture.Target, now));
    }

    [Fact]
    public void ValidSdkSignatureRejectsWrongPublicKey()
    {
        using var fixture = new SignedFixture("127.0.0.1", true);
        using var other = RSA.Create(2048);
        using var verifier = new HostedV4SignedReadVerifier(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()),
            fixture.Origin.Authority, fixture.SignedAt.AddMinutes(30));
        Assert.False(verifier.Authorize("GET", fixture.Origin.Authority, fixture.Target, fixture.SignedAt.AddSeconds(1)));
    }

    [Fact]
    public void DisposedVerifierCannotAuthorizeAValidSdkSignature()
    {
        using var fixture = new SignedFixture("127.0.0.1", true);
        fixture.Verifier.Dispose();
        Assert.False(fixture.Verifier.Authorize("GET", fixture.Origin.Authority, fixture.Target, fixture.SignedAt.AddSeconds(1)));
    }

    [Fact]
    public void PublicKeyWithTrailingCarrierBytesIsRejected()
    {
        using var key = RSA.Create(2048);
        byte[] bytes = [.. key.ExportSubjectPublicKeyInfo(), 0];
        Assert.Throws<ArgumentException>(() => new HostedV4SignedReadVerifier(Convert.ToBase64String(bytes),
            "127.0.0.1:45001", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private static string ChangeSignature(string target)
    {
        int index = target.IndexOf("X-Goog-Signature=", StringComparison.Ordinal) + "X-Goog-Signature=".Length;
        return target[..index] + (target[index] == 'a' ? 'b' : 'a') + target[(index + 1)..];
    }

    private static string ChangeParameter(string target, string name, string value)
    {
        int index = target.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1;
        int end = target.IndexOf('&', index);
        if (end < 0) end = target.Length;
        return target[..index] + value + target[end..];
    }

    private sealed class SignedFixture : IDisposable
    {
        private readonly RSA key = RSA.Create(2048);
        public Uri Origin { get; }
        public string Target { get; }
        public DateTimeOffset SignedAt { get; }
        public HostedV4SignedReadVerifier Verifier { get; }

        public SignedFixture(string host, bool generation, int leaseSeconds = 1800)
        {
            try
            {
                Origin = new Uri("http://" + host + ":45001");
                var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(
                    "hosted-file-signing@example.invalid")
                { Key = key, HttpClientFactory = new RejectingFactory() });
                var query = new Dictionary<string, IEnumerable<string>>
                {
                    ["response-content-disposition"] = ["attachment; filename=invoice.pdf"],
                };
                if (generation) query["generation"] = ["17"];
                var request = UrlSigner.RequestTemplate.FromBucket("maliev.com").WithObjectName("invoices/invoice.pdf")
                    .WithHttpMethod(HttpMethod.Get).WithQueryParameters(query);
                var options = UrlSigner.Options.FromDuration(TimeSpan.FromSeconds(120)).WithSigningVersion(SigningVersion.V4)
                    .WithScheme(Origin.Scheme).WithHost(Origin.Host).WithPort(Origin.Port);
                var uri = new Uri(UrlSigner.FromCredential(credential).Sign(request, options));
                Target = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
                string date = WebUtility.UrlDecode(uri.Query[1..].Split('&').Single(value => value.StartsWith("X-Goog-Date=", StringComparison.Ordinal))["X-Goog-Date=".Length..]);
                SignedAt = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                Verifier = new HostedV4SignedReadVerifier(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                    Origin.Authority, SignedAt.AddSeconds(leaseSeconds));
            }
            catch { key.Dispose(); throw; }
        }

        public void Dispose() { Verifier.Dispose(); key.Dispose(); }
    }

    private sealed class RejectingFactory : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new RejectingHandler();
        private sealed class RejectingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("Native verifier controls prohibit signer network access."));
        }
    }
}
