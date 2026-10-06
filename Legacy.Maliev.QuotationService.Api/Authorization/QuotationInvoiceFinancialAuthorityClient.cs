using System.Net;
using System.Text.Json;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

/// <summary>Reads the owning Accounting operation immediately before the local employee decision.</summary>
/// <param name="httpClient">Fixed-origin, workload-authenticated, redirect-disabled Accounting transport.</param>
public sealed class QuotationInvoiceFinancialAuthorityClient(HttpClient httpClient)
{
    internal const string ReadPermission = "legacy.accounting.invoice-financial-ownership.read";

    internal async Task<int> ValidateAsync(VerifiedQuotationInvoiceOperation authority, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/internal/invoice-creation/operations/{authority.OperationId:D}/financial-ownership");
            // No cache, retry, callback URL, request body, or caller-owned employee credential.
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return 403;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return 409;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 4096) return 503;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4097];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 4097 - (int)body.Length), deadline.Token);
                if (read == 0) break;
                body.Write(buffer, 0, read);
                if (body.Length > 4096) return 503;
            }
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 503;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != 9 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 9
                || fields.Any(field => field.Name is not ("ContractVersion" or "OperationId" or "QuotationId" or "InvoiceId"
                    or "OriginIssuer" or "EmployeeSubject" or "RequesterSubject" or "OriginalQuotationVersion" or "FinancialBinding"))) return 503;
            static bool Integer(JsonElement root, string name, out int value)
            {
                value = 0;
                return root.GetProperty(name).ValueKind == JsonValueKind.Number && root.GetProperty(name).TryGetInt32(out value);
            }
            static string? Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String
                ? root.GetProperty(name).GetString() : null;
            if (!Integer(root, "ContractVersion", out var version) || version != 1
                || !Integer(root, "QuotationId", out var quotationId) || quotationId <= 0
                || !Integer(root, "InvoiceId", out var invoiceId) || invoiceId <= 0) return 503;
            if (fields.Any(field => field.Name is not ("ContractVersion" or "QuotationId" or "InvoiceId")
                && field.Value.ValueKind != JsonValueKind.String)) return 503;
            // All strings are canonical exact comparisons to the already signed operation.
            return quotationId == authority.QuotationId && invoiceId == authority.InvoiceId
                && Text(root, "OperationId") == authority.OperationId.ToString("D")
                && Text(root, "OriginIssuer") == authority.Issuer
                && Text(root, "EmployeeSubject") == authority.EmployeeSubject
                && Text(root, "RequesterSubject") == QuotationInvoiceCapabilityVerifier.Requester
                && Text(root, "OriginalQuotationVersion") == authority.OriginalQuotationVersion.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                && Text(root, "FinancialBinding") == authority.FinancialBinding ? 200 : 409;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return 503;
        }
    }

    internal static Uri ResolveOrigin(string? configured, IHostEnvironment environment)
    {
        if (configured is null) return new Uri("https+http://legacy-maliev-accounting-service");
        if (string.IsNullOrWhiteSpace(configured) || configured != configured.Trim()
            || !Uri.TryCreate(configured, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Financial authority requires an approved Accounting origin.");
        if (uri.Scheme == "https" || uri.Scheme == "https+http" && uri.Host == "legacy-maliev-accounting-service" && uri.Port == -1) return uri;
        if (uri.Scheme == "http" && environment.IsDevelopment() && uri.IsLoopback) return uri;
        throw new InvalidOperationException("Financial authority requires an approved Accounting origin.");
    }
}
