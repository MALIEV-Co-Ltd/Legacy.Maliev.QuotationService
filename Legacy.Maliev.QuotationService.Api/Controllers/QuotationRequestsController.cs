using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class QuotationRequestsController(IQuotationService service, IIdempotencyStore idempotency) : ControllerBase
{
    [HttpPost, RequirePermission(QuotationPermissions.RequestsCreate, RequireLiveCheck = true)] public async Task<IActionResult> CreateQuotationRequestAsync(UpsertQuotationRequestRequest item, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) { var value = await IdempotentCreates.GetOrCreateAsync(idempotency, "quotation-request", key, () => service.CreateRequestAsync(item, ct), ct); return CreatedAtRoute("GetQuotationRequest", new { requestId = value.Id }, value); }
    [HttpDelete("{requestId:int}"), RequirePermission(QuotationPermissions.RequestsDelete, RequireLiveCheck = true, IsCritical = true)] public async Task<IActionResult> DeleteQuotationRequestAsync(int requestId, CancellationToken ct) => await service.DeleteRequestAsync(requestId, ct) ? NoContent() : NotFound();
    [HttpGet, RequirePermission(QuotationPermissions.RequestsRead, RequireLiveCheck = true)] public async Task<ActionResult<PaginatedResponse<QuotationRequestResponse>>> GetPaginatedQuotationRequestAsync([FromQuery] RequestSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken ct) { var value = await service.GetRequestsAsync(sort, search, Math.Max(index ?? 1, 1), Math.Clamp(size ?? 50, 1, 250), ct); return value is null ? NotFound() : value; }
    [HttpGet("{requestId:int}", Name = "GetQuotationRequest"), RequirePermission(QuotationPermissions.RequestsRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationRequestResponse>> GetQuotationAsync(int requestId, CancellationToken ct) { var value = await service.GetRequestAsync(requestId, ct); return value is null ? NotFound() : value; }
    [HttpPut("{requestId:int}"), RequirePermission(QuotationPermissions.RequestsUpdate, RequireLiveCheck = true)] public async Task<IActionResult> UpdateQuotationRequestAsync(int requestId, UpsertQuotationRequestRequest item, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken ct) => (await service.UpdateRequestAsync(requestId, item, expected, ct)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Quotation request was modified by another request."), _ => NotFound() };
}
