using System.Globalization;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.IAM;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

/// <summary>Admits only one invoice-bound Accounting operation with a fresh originating employee live check.</summary>
/// <param name="verifier">Independent narrow capability validator.</param>
/// <param name="iam">Existing workload-authenticated IAM transport.</param>
/// <param name="clock">Current time for the post-await expiry fence.</param>
/// <param name="configuration">Staged recipient activation.</param>
/// <param name="services">Resolves the staged financial reader only after authority is required.</param>
public sealed class QuotationInvoiceCompletionAuthority(QuotationInvoiceCapabilityVerifier verifier,
    IIamServiceClient iam, TimeProvider clock, IConfiguration configuration, IServiceProvider services)
{
    internal async Task<(int Status, QuotationInvoiceCompletionContext? Authority)> AuthorizeAsync(HttpContext context, int quotationId, int invoiceId,
        DateTimeOffset? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!configuration.GetValue<bool>("QuotationInvoiceCompletion:Enabled") || expected is null) return (403, null);
        var headers = context.Request.Headers;
        if (!headers.TryGetValue(QuotationInvoiceCapabilityVerifier.HeaderName, out var proof) || proof.Count != 1
            || !headers.TryGetValue("Idempotency-Key", out var operationHeader) || operationHeader.Count != 1
            || !headers.TryGetValue("X-Expected-Modified-Date", out var versionHeader) || versionHeader.Count != 1
            || !Guid.TryParseExact(operationHeader[0], "D", out var operationId) || operationId == Guid.Empty
            || operationHeader[0] != operationId.ToString("D")) return (403, null);
        var authority = verifier.Verify(proof[0], context.User, quotationId, operationId, invoiceId, expected.Value);
        if (authority is null) return (403, null);
        // Independent direct live check for the employee. Service policy success is not employee authority,
        // and this direct SDK call has no permission-handler claim/local-snapshot/fail-open fallback.
        bool allowed;
        try
        {
            allowed = await iam.CheckPermissionLiveAsync(authority.EmployeeSubject, QuotationPermissions.QuotationsUpdate,
                $"/quotations/{quotationId}", cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (503, null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!allowed || authority.ExpiresAt <= clock.GetUtcNow()) return (403, null);
        int financialStatus;
        try
        {
            var financial = services.GetRequiredService<QuotationInvoiceFinancialAuthorityClient>();
            financialStatus = await financial.ValidateAsync(authority, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (503, null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (financialStatus != 200) return (financialStatus, null);
        if (authority.ExpiresAt <= clock.GetUtcNow()) return (403, null);
        return (200, new(authority.OperationId, authority.QuotationId, authority.InvoiceId,
            authority.Issuer, authority.EmployeeSubject, QuotationInvoiceCapabilityVerifier.Requester,
            QuotationInvoiceCapabilityVerifier.Executor,
            authority.OriginalQuotationVersion.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            authority.FinancialBinding, QuotationInvoiceCapabilityVerifier.FinancialBindingVersion));
    }
}
