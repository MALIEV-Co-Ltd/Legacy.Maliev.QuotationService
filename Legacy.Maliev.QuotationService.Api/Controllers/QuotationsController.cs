using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class QuotationsController(IQuotationService service, IIdempotencyStore idempotency) : ControllerBase
{
    [HttpPost, RequirePermission(QuotationPermissions.QuotationsCreate, RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> CreateQuotationAsync(UpsertQuotationRequest item, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken cancellationToken)
    { var value = await IdempotentCreates.GetOrCreateAsync(idempotency, "quotation", key, () => service.CreateQuotationAsync(item, cancellationToken), cancellationToken); return CreatedAtRoute("GetQuotation", new { quotationId = value.Id }, value); }

    [HttpDelete("{quotationId:int}"), RequirePermission(QuotationPermissions.QuotationsDelete, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> DeleteQuotationAsync(int quotationId, CancellationToken cancellationToken) => await service.DeleteQuotationAsync(quotationId, cancellationToken) ? NoContent() : NotFound();

    [HttpGet, HttpGet("customers/{customerId:int}"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<PaginatedResponse<QuotationResponse>>> GetPaginatedQuotationAsync(int? customerId, [FromQuery] QuotationSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken cancellationToken)
    { var value = await service.GetQuotationsAsync(customerId, sort, search, Math.Max(index ?? 1, 1), Math.Clamp(size ?? 50, 1, 250), cancellationToken); return value is null ? NotFound() : value; }

    [HttpGet("{quotationId:int}", Name = "GetQuotation"), RequirePermission(QuotationPermissions.QuotationsRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationResponse>> GetQuotationAsync(int quotationId, CancellationToken cancellationToken) { var value = await service.GetQuotationAsync(quotationId, cancellationToken); return value is null ? NotFound() : value; }

    [HttpGet("invoices/{invoiceId:int}", Name = "GetQuotationFromInvoiceId"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationResponse>> GetQuotationFromInvoiceIdAsync(int invoiceId, CancellationToken cancellationToken) { var value = await service.GetQuotationByInvoiceAsync(invoiceId, cancellationToken); return value is null ? NotFound() : value; }

    [HttpGet("stats"), RequirePermission(QuotationPermissions.QuotationsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QuotationStatsResponse>> GetQuotationStatsAsync(CancellationToken cancellationToken) => await service.GetStatsAsync(cancellationToken);

    [HttpPut("{quotationId:int}"), RequirePermission(QuotationPermissions.QuotationsUpdate, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true, IsCritical = true)]
    public async Task<IActionResult> UpdateQuotationAsync(int quotationId, UpsertQuotationRequest item, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken cancellationToken) => (await service.UpdateQuotationAsync(quotationId, item, expected, cancellationToken)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Quotation was modified by another request."), _ => NotFound() };

    [HttpGet("{quotationId:int}/withholdingtax", Name = "GetQuotationWithholdingTax"), RequirePermission(QuotationPermissions.QuotationsRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)]
    public async Task<ActionResult<decimal>> GetQuotationWithholdingTaxAsync(int quotationId, CancellationToken cancellationToken) { var value = await service.GetWithholdingTaxAsync(quotationId, cancellationToken); return value is null ? NotFound() : value.Value; }
}
