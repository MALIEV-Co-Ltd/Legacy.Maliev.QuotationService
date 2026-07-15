using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("quotations/files"), Authorize]
public sealed class QuotationFilesController(IQuotationService service) : ControllerBase
{
    [HttpPost("/quotations/{quotationId:int}/files"), RequirePermission(QuotationPermissions.FilesWrite, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)] public async Task<IActionResult> CreateQuotationFileEntryAsync(int quotationId, [FromQuery] string bucket, [FromQuery] string objectName, CancellationToken ct) { if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectName)) return BadRequest(); var value = await service.CreateQuotationFileAsync(quotationId, bucket, objectName, ct); return value is null ? NotFound() : CreatedAtRoute("GetQuotationFile", new { quotationFileId = value.Id }, value); }
    [HttpDelete("{quotationFileId:int}"), RequirePermission(QuotationPermissions.FilesDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteQuotationFileAsync(int quotationFileId, CancellationToken ct) => await service.DeleteQuotationFileAsync(quotationFileId, ct) ? NoContent() : NotFound();
    [HttpGet("{quotationFileId:int}", Name = "GetQuotationFile"), RequirePermission(QuotationPermissions.FilesRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationFileResponse>> GetQuotationFileAsync(int quotationFileId, CancellationToken ct) { var value = await service.GetQuotationFileAsync(quotationFileId, ct); return value is null ? NotFound() : value; }
    [HttpGet("/quotations/{quotationId:int}/files"), RequirePermission(QuotationPermissions.FilesRead, ResourcePathTemplate = "/quotations/{quotationId}", RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<QuotationFileResponse>>> GetQuotationFilesAsync(int quotationId, CancellationToken ct) { var value = await service.GetQuotationFilesAsync(quotationId, ct); return value.Count == 0 ? NotFound() : Ok(value); }
    [HttpPut("{quotationFileId:int}"), RequirePermission(QuotationPermissions.FilesWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateQuotationFileAsync(int quotationFileId, UpsertQuotationFileRequest item, CancellationToken ct) { if (string.IsNullOrWhiteSpace(item.Bucket) || string.IsNullOrWhiteSpace(item.ObjectName)) return BadRequest(); return await service.UpdateQuotationFileAsync(quotationFileId, item, ct) ? NoContent() : NotFound(); }
}
