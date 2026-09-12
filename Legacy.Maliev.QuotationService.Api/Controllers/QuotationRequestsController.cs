using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.QuotationService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class QuotationRequestsController(IQuotationService service) : ControllerBase
{
    private const int MaximumIdempotencyKeyLength = 128;
    private static readonly IReadOnlySet<string> QualificationStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "unreviewed", "qualified", "not_qualified", "duplicate", "stale", "incomplete",
    };

    [HttpPost, RequirePermission(QuotationPermissions.RequestsCreate, RequireLiveCheck = true)]
    public async Task<IActionResult> CreateQuotationRequestAsync(
        UpsertQuotationRequestRequest item,
        [FromHeader(Name = "Idempotency-Key")] string? key,
        CancellationToken ct)
    {
        if (key?.Length > MaximumIdempotencyKeyLength)
        {
            return ProblemWithCode(
                StatusCodes.Status400BadRequest,
                "Idempotency key is too long.",
                "idempotency_key_too_long");
        }
        if (key is not null && string.IsNullOrWhiteSpace(key))
        {
            return ProblemWithCode(
                StatusCodes.Status400BadRequest,
                "Idempotency key is invalid.",
                "idempotency_key_invalid");
        }

        if (key is null)
        {
            return Created(await service.CreateRequestAsync(item, ct));
        }

        var result = await service.CreateRequestIdempotentlyAsync(
            item,
            Hash(key),
            Fingerprint(item),
            ct);
        if (result.Binding == IdempotencyBindingResult.Conflict)
        {
            return ProblemWithCode(
                StatusCodes.Status409Conflict,
                "Idempotency key was already used with different request data.",
                "idempotency_key_conflict");
        }
        if (result.Binding == IdempotencyBindingResult.Unavailable || result.Response is null)
        {
            return ProblemWithCode(
                StatusCodes.Status503ServiceUnavailable,
                "Idempotency protection is temporarily unavailable.",
                "idempotency_store_unavailable");
        }

        return Created(result.Response);
    }
    [HttpDelete("{requestId:int}"), RequirePermission(QuotationPermissions.RequestsDelete, RequireLiveCheck = true, IsCritical = true)] public async Task<IActionResult> DeleteQuotationRequestAsync(int requestId, CancellationToken ct) => await service.DeleteRequestAsync(requestId, ct) ? NoContent() : NotFound();
    [HttpGet, RequirePermission(QuotationPermissions.RequestsRead, RequireLiveCheck = true)] public async Task<ActionResult<PaginatedResponse<QuotationRequestResponse>>> GetPaginatedQuotationRequestAsync([FromQuery] RequestSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken ct) { var value = await service.GetRequestsAsync(sort, search, Math.Max(index ?? 1, 1), Math.Clamp(size ?? 50, 1, 250), ct); return value is null ? NotFound() : value; }
    [HttpGet("{requestId:int}", Name = "GetQuotationRequest"), RequirePermission(QuotationPermissions.RequestsRead, RequireLiveCheck = true)] public async Task<ActionResult<QuotationRequestResponse>> GetQuotationAsync(int requestId, CancellationToken ct) { var value = await service.GetRequestAsync(requestId, ct); return value is null ? NotFound() : value; }
    [HttpPut("{requestId:int}"), RequirePermission(QuotationPermissions.RequestsUpdate, RequireLiveCheck = true)] public async Task<IActionResult> UpdateQuotationRequestAsync(int requestId, UpsertQuotationRequestRequest item, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken ct) => (await service.UpdateRequestAsync(requestId, item, expected, ct)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Quotation request was modified by another request."), _ => NotFound() };

    [HttpGet("{requestId:int}/qualification-receipt"), RequirePermission(QuotationPermissions.RequestsRead, RequireLiveCheck = true)]
    public async Task<ActionResult<QualificationReceipt>> GetQualificationReceiptAsync(int requestId, CancellationToken ct)
    {
        var receipt = await service.GetRequestQualificationAsync(requestId, ct);
        return receipt is null ? NotFound() : receipt;
    }

    [HttpPut("{requestId:int}/qualification"), RequirePermission(QuotationPermissions.RequestsUpdate, RequireLiveCheck = true)]
    public async Task<ActionResult<QualificationReceipt>> UpdateQualificationStateAsync(
        int requestId,
        QualificationStateUpdateRequest item,
        CancellationToken ct)
    {
        var state = item.State?.Trim().ToLowerInvariant();
        var reason = item.Reason?.Trim();
        var completeness = item.Completeness?.Trim().ToLowerInvariant();
        var unmatched = item.UnmatchedClassification?.Trim().ToLowerInvariant();
        var idempotencyKey = item.IdempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(state)
            || string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            return BadRequest("State and a valid idempotencyKey are required.");
        }
        if (!QualificationStates.Contains(state))
        {
            return BadRequest("Qualification state is invalid.");
        }
        if (state != "qualified" && string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest("Reason is required unless the state is qualified.");
        }
        if (item.DuplicateCount < 0 || item.ExpectedVersion < 0)
        {
            return BadRequest("Duplicate count and expected version cannot be negative.");
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Forbid();
        }

        var normalized = item with
        {
            State = state,
            Reason = reason,
            Completeness = completeness,
            UnmatchedClassification = unmatched,
            IdempotencyKey = idempotencyKey,
        };
        var result = await service.UpdateRequestQualificationAsync(requestId, normalized, actor, ct);
        return result.Status switch
        {
            QualificationUpdateStatus.Completed when result.Receipt is not null => Ok(result.Receipt),
            QualificationUpdateStatus.NotFound => NotFound(),
            QualificationUpdateStatus.IdempotencyConflict => Conflict("Idempotency key was already used for a different transition."),
            _ => Conflict("Qualification state changed. Reload the receipt and retry with its current version."),
        };
    }

    private IActionResult Created(QuotationRequestResponse response) =>
        CreatedAtRoute("GetQuotationRequest", new { requestId = response.Id }, response);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Fingerprint(UpsertQuotationRequestRequest request)
    {
        var canonical = new StringBuilder();
        Append(canonical, request.FirstName);
        Append(canonical, request.LastName);
        Append(canonical, request.Email);
        Append(canonical, request.TelephoneNumber);
        Append(canonical, request.Country);
        Append(canonical, request.CompanyName);
        Append(canonical, request.TaxIdentification);
        Append(canonical, request.Message);
        Append(canonical, request.InternalComment);
        canonical.Append(request.Done switch { true => "T", false => "F", null => "N" });
        // Preserve existing null-journey fingerprints across the additive rollout.
        if (request.JourneyId.HasValue) Append(canonical, request.JourneyId.Value.ToString("D"));
        return Hash(canonical.ToString());
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("N;");
            return;
        }

        canonical.Append('V').Append(value.Length).Append(':').Append(value).Append(';');
    }

    private ObjectResult ProblemWithCode(int statusCode, string title, string code)
    {
        var result = Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
