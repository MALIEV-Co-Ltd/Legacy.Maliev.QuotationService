using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class QuotationsController(
    IQuotationService service,
    IIdempotencyStore idempotency,
    IAuthorizationService authorization,
    IQuotationDecisionWorkflow decisions,
    QuotationInvoiceCompletionAuthority? invoiceAuthority = null,
    IQuotationInvoiceCompletionStore? invoiceOperations = null) : ControllerBase
{
    [HttpPost, RequirePermission(QuotationPermissions.QuotationsCreate, RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> CreateQuotationAsync(UpsertQuotationRequest item, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken cancellationToken)
    { var value = await IdempotentCreates.GetOrCreateAsync(idempotency, "quotation", key, () => service.CreateQuotationAsync(item, cancellationToken), cancellationToken); return CreatedAtRoute("GetQuotation", new { quotationId = value.Id }, value); }

    [HttpDelete("{quotationId:int}"), RequirePermission(QuotationPermissions.QuotationsDelete, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> DeleteQuotationAsync(int quotationId, CancellationToken cancellationToken) => await service.DeleteQuotationAsync(quotationId, cancellationToken) ? NoContent() : NotFound();

    [HttpGet, HttpGet("customers/{customerId:int}"), RequirePermission(QuotationPermissions.CustomerQuotationsRead)]
    public async Task<ActionResult<PaginatedResponse<QuotationResponse>>> GetPaginatedQuotationAsync(int? customerId, [FromQuery] QuotationSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken cancellationToken)
    {
        if (customerId is null && !await HasAdministrativeReadAsync()) return Forbid();
        var value = await service.GetQuotationsAsync(customerId, sort, search, Math.Max(index ?? 1, 1), Math.Clamp(size ?? 50, 1, customerId is null ? 250 : 100), cancellationToken);
        return value is null ? NotFound() : value;
    }

    [HttpGet("{quotationId:int}", Name = "GetQuotation"), RequirePermission(QuotationPermissions.CustomerQuotationsRead, ResourcePathTemplate = "/quotations/{quotationId}")]
    public async Task<ActionResult<object>> GetQuotationAsync(int quotationId, [FromQuery] int? customerId, CancellationToken cancellationToken)
    {
        if (customerId is not null)
        {
            var customerValue = await service.GetCustomerQuotationAsync(customerId.Value, quotationId, cancellationToken);
            return customerValue is null ? NotFound() : customerValue;
        }

        if (!await HasAdministrativeReadAsync()) return Forbid();
        var value = await service.GetQuotationAsync(quotationId, cancellationToken);
        return value is null ? NotFound() : value;
    }

    [HttpGet("invoices/{invoiceId:int}", Name = "GetQuotationFromInvoiceId"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationResponse>> GetQuotationFromInvoiceIdAsync(int invoiceId, CancellationToken cancellationToken) { var value = await service.GetQuotationByInvoiceAsync(invoiceId, cancellationToken); return value is null ? NotFound() : value; }

    [HttpGet("stats"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationStatsResponse>> GetQuotationStatsAsync(CancellationToken cancellationToken) => await service.GetStatsAsync(cancellationToken);

    [HttpGet("outcomes/readback"), Authorize(Policy = QuotationEmployeeActorPolicy.Name), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationOutcomeReadback>> GetOutcomeReadbackAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        if (!QuotationEmployeeActorPolicy.IsEmployee(User))
        {
            return Forbid();
        }

        if (fromUtc.Kind != DateTimeKind.Utc
            || toUtc.Kind != DateTimeKind.Utc
            || fromUtc >= toUtc
            || toUtc - fromUtc > TimeSpan.FromDays(31))
        {
            return BadRequest();
        }

        return await service.GetOutcomeReadbackAsync(fromUtc, toUtc, cancellationToken);
    }

    [HttpPut("{quotationId:int}"), RequirePermission(QuotationPermissions.QuotationsUpdate, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> UpdateQuotationAsync(int quotationId, UpsertQuotationRequest item, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken cancellationToken) => (await service.UpdateQuotationAsync(quotationId, item, expected, cancellationToken)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Quotation was modified by another request."), _ => NotFound() };

    /// <summary>Records a quotation decision with its invoice and optional first-acceptance analytics context.</summary>
    /// <param name="quotationId">Quotation identifier whose decision is being recorded.</param>
    /// <param name="request">Decision and optional consent-gated context supplied by the authorized first writer.</param>
    /// <param name="expected">Optional quotation version precondition.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("{quotationId:int}/decision"), RequirePermission(QuotationPermissions.QuotationsUpdate, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> DecideQuotationAsync(
        int quotationId,
        QuotationDecisionRequest request,
        [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected,
        CancellationToken cancellationToken)
    {
        if (request.InvoiceId < 0
            || request.InvoiceId > 0 && !request.Accepted
            || request.InvoiceId == 0 && (!request.Accepted || !request.EmployeeInitiated))
        {
            return BadRequest();
        }

        QuotationInvoiceCompletionContext? completion = null;
        if (request.EmployeeInitiated && !IsTrustedEmployeeDecisionCaller())
        {
            if (!request.Accepted || request.InvoiceId is not > 0 || invoiceAuthority is null)
                return Forbid();
            var authority = await invoiceAuthority.AuthorizeAsync(HttpContext, quotationId, request.InvoiceId.Value, expected, cancellationToken);
            if (authority.Status != 200) return authority.Status switch
            {
                409 => Conflict(),
                503 => StatusCode(503),
                _ => Forbid(),
            };
            completion = authority.Authority;
            if (completion is null) return Forbid();
        }

        if (request.Accepted && !ValidAnalyticsContext(request))
        {
            return BadRequest();
        }

        if (completion is not null && request.TryGetAnalyticsContext(out var analytics) && analytics is not null)
            return BadRequest();
        QuotationDecisionResponse result;
        try
        {
            result = completion is null
                ? await decisions.DecideAsync(quotationId, request, expected, cancellationToken)
                : await decisions.CompleteInvoiceAsync(completion, cancellationToken);
        }
        catch (Exception exception) when (completion is not null
            && (exception is IOException or InvalidDataException or FormatException or System.Data.Common.DbException or System.Text.Json.JsonException
                or Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Storage acknowledgement may be unknown. Only owning operation readback can resolve it.
            return StatusCode(503);
        }
        return result.Status switch
        {
            QuotationDecisionStatus.Completed => Ok(result),
            QuotationDecisionStatus.NotFound => NotFound(),
            QuotationDecisionStatus.Conflict => Conflict("Quotation was modified by another request."),
            QuotationDecisionStatus.DependencyConflict => Conflict(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        };
    }

    /// <summary>Reads the owning operation receipt under freshly verified invoice-bound employee authority.</summary>
    [HttpGet("{quotationId:int}/invoice-completion/operations/{operationId:guid}"), RequirePermission(QuotationPermissions.QuotationsUpdate,
        ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> GetInvoiceCompletionOperationAsync(int quotationId, Guid operationId,
        [FromQuery] int invoiceId, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (invoiceId <= 0 || operationId == Guid.Empty) return BadRequest();
        if (invoiceAuthority is null || invoiceOperations is null) return Forbid();
        var result = await invoiceAuthority.AuthorizeAsync(HttpContext, quotationId, invoiceId, expected, cancellationToken);
        if (result.Status != 200) return result.Status switch
        {
            409 => Conflict(),
            503 => StatusCode(503),
            _ => Forbid(),
        };
        var authority = result.Authority;
        if (authority is null || authority.OperationId != operationId) return Forbid();
        try
        {
            var progress = await invoiceOperations.ReadAsync(operationId, cancellationToken);
            if (progress is null) return NotFound();
            var receipt = progress.Receipt;
            var retained = new QuotationInvoiceCompletionContext(Guid.ParseExact(receipt.OperationId, "D"),
                receipt.QuotationId, receipt.InvoiceId, receipt.OriginIssuer, receipt.EmployeeSubject,
                receipt.RequesterSubject, receipt.ExecutorSubject, receipt.OriginalQuotationVersion,
                receipt.FinancialBinding, receipt.FinancialBindingVersion);
            return retained == authority ? Ok(receipt) : Conflict();
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException
            or System.Data.Common.DbException or IOException or FormatException
            or Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(503);
        }
    }

    private static bool ValidAnalyticsContext(QuotationDecisionRequest request)
        => request.TryGetAnalyticsContext(out _);

    [HttpGet("{quotationId:int}/withholdingtax", Name = "GetQuotationWithholdingTax"), RequirePermission(QuotationPermissions.QuotationsRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)]
    public async Task<ActionResult<decimal>> GetQuotationWithholdingTaxAsync(int quotationId, CancellationToken cancellationToken) { var value = await service.GetWithholdingTaxAsync(quotationId, cancellationToken); return value is null ? NotFound() : value.Value; }

    private async Task<bool> HasAdministrativeReadAsync()
    {
        if (User.Claims.Any(claim =>
                (claim.Type == "permissions" || claim.Type == "permission") &&
                string.Equals(claim.Value, QuotationPermissions.QuotationsRead, StringComparison.Ordinal)))
        {
            return true;
        }

        return (await authorization.AuthorizeAsync(
            User,
            null,
            $"Permission:{QuotationPermissions.QuotationsRead}")).Succeeded;
    }

    private bool IsTrustedEmployeeDecisionCaller()
    {
        var identityKinds = User.FindAll("identity_kind").Select(claim => claim.Value).ToArray();
        if (QuotationEmployeeActorPolicy.IsEmployee(User))
        {
            return true;
        }

        if (identityKinds is not ["service"])
        {
            return false;
        }

        var subjects = User.FindAll("sub").Select(claim => claim.Value).ToArray();
        var permissions = User.FindAll("permissions").Select(claim => claim.Value).ToArray();
        return subjects is ["service:legacy-intranet"]
            && !User.HasClaim(claim => claim.Type == "permission")
            && permissions.Contains(QuotationPermissions.QuotationsUpdate, StringComparer.Ordinal)
            && permissions.All(permission => !permission.Contains('*', StringComparison.Ordinal));
    }
}
