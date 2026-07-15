using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("quotationrequests/files"), Authorize]
public sealed class QuotationRequestFilesController(IQuotationService service) : ControllerBase
{
    [HttpPost("/quotationrequests/{requestId:int}/files"), RequirePermission(QuotationPermissions.FilesWrite, RequireLiveCheck = true)] public async Task<IActionResult> CreateQuotationRequestFileEntryAsync(int requestId, [FromQuery] string bucket, [FromQuery] string objectName, CancellationToken ct) { if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectName)) return BadRequest(); var value = await service.CreateRequestFileAsync(requestId, bucket, objectName, ct); return value is null ? NotFound() : CreatedAtRoute("GetQuotationRequestFile", new { requestFileId = value.Id }, value); }
    [HttpDelete("{requestFileId:int}"), RequirePermission(QuotationPermissions.FilesDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteQuotationRequestFileAsync(int requestFileId, CancellationToken ct) => await service.DeleteRequestFileAsync(requestFileId, ct) ? NoContent() : NotFound();
    [HttpGet("{requestFileId:int}", Name = "GetQuotationRequestFile"), RequirePermission(QuotationPermissions.FilesRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationRequestFileResponse>> GetQuotationRequestFileAsync(int requestFileId, CancellationToken ct) { var value = await service.GetRequestFileAsync(requestFileId, ct); return value is null ? NotFound() : value; }
    [HttpGet("/quotationrequests/{requestId:int}/files"), RequirePermission(QuotationPermissions.FilesRead, RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<QuotationRequestFileResponse>>> GetQuotationRequestFilesAsync(int requestId, CancellationToken ct) { var value = await service.GetRequestFilesAsync(requestId, ct); return value.Count == 0 ? NotFound() : Ok(value); }
    [HttpPut("{requestFileId:int}"), RequirePermission(QuotationPermissions.FilesWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateQuotationRequestFileAsync(int requestFileId, UpsertQuotationRequestFileRequest item, CancellationToken ct) => await service.UpdateRequestFileAsync(requestFileId, item, ct) ? NoContent() : NotFound();
}
