using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("quotations/[controller]"), Authorize]
public sealed class OrdersController(IQuotationService service, IIdempotencyStore idempotency) : ControllerBase
{
    [HttpPost("/quotations/{quotationId:int}/orders/{orderId:int}"), RequirePermission(QuotationPermissions.OrdersWrite, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)] public async Task<IActionResult> CreateQuotationOrderLinkAsync(int quotationId, int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) { var value = await IdempotentCreates.GetOrCreateNullableAsync(idempotency, "quotation-order-link", key, () => service.CreateOrderLinkAsync(quotationId, orderId, ct), ct); return value is null ? NotFound() : CreatedAtRoute("GetQuotationOrder", new { id = value.Id }, value); }
    [HttpDelete("{id:int}"), RequirePermission(QuotationPermissions.OrdersDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteQuotationOrderLinkAsync(int id, CancellationToken ct) => await service.DeleteOrderLinkAsync(id, ct) ? NoContent() : NotFound();
    [HttpGet("/quotations/{quotationId:int}/orders"), RequirePermission(QuotationPermissions.OrdersRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<QuotationOrderLinkResponse>>> GetAllOrdersFromQuotationAsync(int quotationId, CancellationToken ct) { var value = await service.GetOrderLinksAsync(quotationId, ct); return value.Count == 0 ? NotFound() : Ok(value); }
    [HttpGet("{id:int}", Name = "GetQuotationOrder"), RequirePermission(QuotationPermissions.OrdersRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationOrderLinkResponse>> GetQuotationOrderLinkAsync(int id, CancellationToken ct) { var value = await service.GetOrderLinkAsync(id, ct); return value is null ? NotFound() : value; }
    [HttpPut, RequirePermission(QuotationPermissions.OrdersWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateQuotationOrderLinkAsync(int id, UpsertQuotationOrderLinkRequest item, CancellationToken ct) => await service.UpdateOrderLinkAsync(id, item, ct) ? NoContent() : NotFound();
}
