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
    IQuotationDecisionWorkflow decisions) : ControllerBase
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

    [HttpGet("outcomes/readback"), Authorize(Roles = "Employee"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationOutcomeReadback>> GetOutcomeReadbackAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Employee"))
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

    [HttpPut("{quotationId:int}/decision"), RequirePermission(QuotationPermissions.QuotationsUpdate, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> DecideQuotationAsync(
        int quotationId,
        QuotationDecisionRequest request,
        [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected,
        CancellationToken cancellationToken)
    {
        if (request.EmployeeInitiated && !IsTrustedEmployeeDecisionCaller())
        {
            return Forbid();
        }

        var result = await decisions.DecideAsync(quotationId, request, expected, cancellationToken);
        return result.Status switch
        {
            QuotationDecisionStatus.Completed => Ok(result),
            QuotationDecisionStatus.NotFound => NotFound(),
            QuotationDecisionStatus.Conflict => Conflict("Quotation was modified by another request."),
            QuotationDecisionStatus.DependencyConflict => Conflict(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        };
    }

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
        if (User.IsInRole("Employee"))
        {
            return identityKinds.Length == 0
                || identityKinds is ["employee"];
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
