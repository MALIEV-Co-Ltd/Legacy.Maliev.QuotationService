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
        var persistence = await quotations.ApplyDecisionAsync(
            quotationId,
            request.Accepted,
            request.Accepted
                ? request.EmployeeInitiated
                    ? QuotationAcceptanceOrigin.Employee
                    : QuotationAcceptanceOrigin.Customer
                : null,
            expectedModifiedDate,
            cancellationToken);
        if (persistence.Status == QuotationDecisionPersistenceStatus.NotFound)
        {
            return Result(QuotationDecisionStatus.NotFound);
        }
        if (persistence.Status == QuotationDecisionPersistenceStatus.Conflict)
        {
            return Result(QuotationDecisionStatus.Conflict);
        }

        var quotation = persistence.Quotation;
        if (quotation is null || quotation.Accepted != request.Accepted)
        {
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

    private static string CreateIdempotencyKey(int quotationId, int orderId, bool accepted, DateTime? version)
    {
        var normalizedVersion = version is null
            ? DateTime.UnixEpoch
            : DateTime.SpecifyKind(version.Value, DateTimeKind.Utc);
        return $"quotation-{quotationId}-{(accepted ? "accepted" : "declined")}-{normalizedVersion.Ticks:x}-order-{orderId}";
    }
}
