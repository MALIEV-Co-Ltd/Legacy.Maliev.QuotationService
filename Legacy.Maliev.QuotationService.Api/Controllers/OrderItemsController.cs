using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("quotations/[controller]"), Authorize]
public sealed class OrderItemsController(IQuotationService service, IIdempotencyStore idempotency) : ControllerBase
{
    [HttpPost, RequirePermission(QuotationPermissions.LinesWrite, RequireLiveCheck = true)] public async Task<IActionResult> CreateOrderItemAsync(UpsertQuotationOrderItemRequest item, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) { var value = await IdempotentCreates.GetOrCreateAsync(idempotency, "quotation-order-item", key, () => service.CreateOrderItemAsync(item, ct), ct); return CreatedAtRoute("GetOrderItem", new { orderItemId = value.Id }, value); }
    [HttpDelete("{orderItemId:int}"), RequirePermission(QuotationPermissions.LinesDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteOrderItemAsync(int orderItemId, CancellationToken ct) => await service.DeleteOrderItemAsync(orderItemId, ct) ? NoContent() : NotFound();
    [HttpGet("{orderItemId:int}", Name = "GetOrderItem"), RequirePermission(QuotationPermissions.LinesRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationOrderItemResponse>> GetOrderItemAsync(int orderItemId, CancellationToken ct) { var value = await service.GetOrderItemAsync(orderItemId, ct); return value is null ? NotFound() : value; }
    [HttpGet("/quotations/{quotationId:int}/orderitems"), RequirePermission(QuotationPermissions.LinesRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<QuotationOrderItemResponse>>> GetOrderItemsAsync(int quotationId, CancellationToken ct) { var value = await service.GetOrderItemsAsync(quotationId, ct); return value.Count == 0 ? NotFound() : Ok(value); }
    [HttpPut("{orderItemId:int}"), RequirePermission(QuotationPermissions.LinesWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateOrderItemAsync(int orderItemId, UpsertQuotationOrderItemRequest item, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken ct) => (await service.UpdateOrderItemAsync(orderItemId, item, expected, ct)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Order item was modified by another request."), _ => NotFound() };
}
