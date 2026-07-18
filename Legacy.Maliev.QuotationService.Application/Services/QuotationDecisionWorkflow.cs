using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;

namespace Legacy.Maliev.QuotationService.Application.Services;

/// <summary>Owns the retry-safe quotation decision and linked-order transition process.</summary>
public sealed class QuotationDecisionWorkflow(
    IQuotationService quotations,
    IOrderDecisionClient orders) : IQuotationDecisionWorkflow
{
    /// <inheritdoc />
    public async Task<QuotationDecisionResponse> DecideAsync(
        int quotationId,
        QuotationDecisionRequest request,
        DateTimeOffset? expectedModifiedDate,
        CancellationToken cancellationToken)
    {
        var quotation = await quotations.GetQuotationAsync(quotationId, cancellationToken);
        if (quotation is null) return Result(QuotationDecisionStatus.NotFound);

        if (quotation.Accepted != request.Accepted)
        {
            var update = await quotations.UpdateQuotationAsync(
                quotationId,
                ToUpdateRequest(quotation, request.Accepted),
                expectedModifiedDate ?? ToOffset(quotation.ModifiedDate),
                cancellationToken);
            if (update == UpdateResult.NotFound) return Result(QuotationDecisionStatus.NotFound);
            if (update == UpdateResult.Conflict) return Result(QuotationDecisionStatus.Conflict);

            quotation = await quotations.GetQuotationAsync(quotationId, cancellationToken);
            if (quotation is null || quotation.Accepted != request.Accepted)
                return Result(QuotationDecisionStatus.DependencyUnavailable);
        }

        var links = await quotations.GetOrderLinksAsync(quotationId, cancellationToken);
        var completed = 0;
        foreach (var link in links)
        {
            var transition = await orders.TransitionAsync(
                link.OrderId,
                request.Accepted,
                CreateIdempotencyKey(quotationId, link.OrderId, request.Accepted, quotation.ModifiedDate ?? quotation.CreatedDate),
                cancellationToken);
            if (transition == OrderDecisionResult.Completed)
            {
                completed++;
                continue;
            }

            return new QuotationDecisionResponse(
                transition == OrderDecisionResult.Unavailable
                    ? QuotationDecisionStatus.DependencyUnavailable
                    : QuotationDecisionStatus.DependencyConflict,
                completed,
                links.Count,
                quotation.ModifiedDate);
        }

        return new QuotationDecisionResponse(
            QuotationDecisionStatus.Completed,
            completed,
            links.Count,
            quotation.ModifiedDate);
    }

    private static QuotationDecisionResponse Result(QuotationDecisionStatus status) => new(status, 0, 0, null);

    private static DateTimeOffset? ToOffset(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string CreateIdempotencyKey(int quotationId, int orderId, bool accepted, DateTime? version)
    {
        var normalizedVersion = version is null
            ? DateTime.UnixEpoch
            : DateTime.SpecifyKind(version.Value, DateTimeKind.Utc);
        return $"quotation-{quotationId}-{(accepted ? "accepted" : "declined")}-{normalizedVersion.Ticks:x}-order-{orderId}";
    }

    private static UpsertQuotationRequest ToUpdateRequest(QuotationResponse quotation, bool accepted) => new(
        quotation.CustomerId,
        quotation.EmployeeId,
        quotation.InvoiceId,
        quotation.Period,
        quotation.ExpirationDate,
        quotation.Subtotal,
        quotation.Vat,
        quotation.Total,
        quotation.WithholdingTax,
        quotation.CurrencyId,
        quotation.Comment,
        quotation.Fob,
        quotation.ShippedVia,
        quotation.Terms,
        accepted);
}
