using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

/// <summary>Validates the invoice-bound extension of Auth's existing quotation completion capability.</summary>
/// <param name="configuration">Existing Auth issuer and public-key configuration.</param>
/// <param name="clock">Current time used independently of ordinary JWT authentication.</param>
public sealed class QuotationInvoiceCapabilityVerifier(IConfiguration configuration, TimeProvider clock)
{
    internal const string Audience = "legacy-quotation:invoice-complete";
    internal const string Scope = "legacy.quotation.invoice-complete";
    internal const string Requester = "service:legacy-intranet";
    internal const string Executor = "service:legacy-accounting";
    internal const string HeaderName = "X-Maliev-Quotation-Invoice-Capability";
    internal const string FinancialBindingVersion = "invoice-creation-financial-v1";

    internal VerifiedQuotationInvoiceOperation? Verify(string? header, ClaimsPrincipal caller, int quotationId, Guid operationId,
        int invoiceId, DateTimeOffset originalQuotationVersion)
    {
        if (quotationId <= 0 || invoiceId <= 0 || operationId == Guid.Empty || caller.Identity?.IsAuthenticated != true
            || One(caller, "sub") != Executor || One(caller, "identity_kind") != "service"
            || caller.FindAll("user_id").Concat(caller.FindAll(ClaimTypes.NameIdentifier)).Any(claim => claim.Value != Executor)
            || caller.FindAll("user_id").Count() > 1 || caller.FindAll(ClaimTypes.NameIdentifier).Count() > 1
            || header is null || header.Length is < 8 or > 16391
            || !header.StartsWith("Bearer ", StringComparison.Ordinal) || header[7..].Contains(' ')) return null;
        var issuer = configuration["Jwt:Issuer"];
        var encodedKey = configuration["Jwt:PublicKey"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(encodedKey)) return null;
        try
        {
            var compact = header[7..];
            using var payload = ReadUnambiguousPayload(compact);
            if (payload is null) return null;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey)));
            var now = clock.GetUtcNow();
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
            handler.ValidateToken(compact, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa)
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
                },
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (notBefore, expires, _, _) => notBefore is not null && expires is not null
                    && notBefore.Value <= now.UtcDateTime && expires.Value > now.UtcDateTime,
            }, out var validated);
            if (validated is not JwtSecurityToken token || token.Header.Alg != SecurityAlgorithms.RsaSha256
                || token.Audiences.Count() != 1 || token.Audiences.Single() != Audience || token.Issuer != issuer) return null;
            var body = payload.RootElement;
            static string? Text(JsonElement body, string name) => body.GetProperty(name).ValueKind == JsonValueKind.String
                ? body.GetProperty(name).GetString() : null;
            var subject = Text(body, "sub");
            var operation = Text(body, "operation_id");
            var jti = Text(body, "jti");
            if (string.IsNullOrWhiteSpace(subject) || subject != subject.Trim() || subject.Length > 256
                || subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
                || Text(body, "azp") != Requester || Text(body, "executor") != Executor || Text(body, "scope") != Scope
                || Text(body, "quotation_id") != quotationId.ToString(CultureInfo.InvariantCulture)
                || Text(body, "invoice_id") != invoiceId.ToString(CultureInfo.InvariantCulture)
                || Text(body, "quotation_version") != originalQuotationVersion.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                || operation != operationId.ToString("D")
                || !Guid.TryParseExact(jti, "D", out var nonce) || nonce == Guid.Empty || jti != nonce.ToString("D")) return null;
            var binding = Text(body, "financial_binding");
            if (Text(body, "financial_binding_version") != FinancialBindingVersion
                || binding is not { Length: 64 } || binding.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) return null;
            var issuedAt = body.GetProperty("iat").GetInt64();
            var notBeforeSeconds = body.GetProperty("nbf").GetInt64();
            var expiresSeconds = body.GetProperty("exp").GetInt64();
            var nowSeconds = now.ToUnixTimeSeconds();
            if (issuedAt > nowSeconds || notBeforeSeconds != issuedAt || expiresSeconds <= nowSeconds
                || issuedAt < nowSeconds - 120 || expiresSeconds <= issuedAt || expiresSeconds > issuedAt + 120) return null;
            return new(issuer, subject, quotationId, invoiceId, operationId, binding, originalQuotationVersion,
                nonce, DateTimeOffset.FromUnixTimeSeconds(expiresSeconds));
        }
        catch (Exception exception) when (exception is SecurityTokenException or CryptographicException
            or FormatException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    private static string? One(ClaimsPrincipal principal, string type)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static JsonDocument? ReadUnambiguousPayload(string compact)
    {
        var parts = compact.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0)) return null;
        using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]), new JsonDocumentOptions { MaxDepth = 2 });
        if (header.RootElement.ValueKind != JsonValueKind.Object) return null;
        var headerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in header.RootElement.EnumerateObject())
        {
            if (!headerNames.Add(property.Name) || property.Name is not ("alg" or "typ" or "kid")
                || property.Value.ValueKind != JsonValueKind.String) return null;
        }
        if (!headerNames.Contains("alg") || header.RootElement.GetProperty("alg").GetString() != SecurityAlgorithms.RsaSha256
            || !headerNames.Contains("typ") || header.RootElement.GetProperty("typ").GetString() != "JWT") return null;
        var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]), new JsonDocumentOptions { MaxDepth = 4 });
        var valid = false;
        try
        {
            if (payload.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in payload.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name)) return null;
                if (property.Name is "iat" or "nbf" or "exp")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out _)) return null;
                }
                else if (property.Name is not ("iss" or "aud" or "sub" or "jti" or "azp" or "executor" or "scope" or "quotation_id" or "operation_id"
                    or "invoice_id" or "financial_binding" or "quotation_version" or "financial_binding_version")) return null;
            }
            if (names.Count != 16) return null;
            valid = true;
            return payload;
        }
        finally
        {
            if (!valid) payload.Dispose();
        }
    }
}

/// <summary>Verified invoice/operation binding minted after Auth's Accounting financial ownership readback.</summary>
internal sealed record VerifiedQuotationInvoiceOperation(string Issuer, string EmployeeSubject, int QuotationId, int InvoiceId,
    Guid OperationId, string FinancialBinding, DateTimeOffset OriginalQuotationVersion, Guid CapabilityId, DateTimeOffset ExpiresAt);
