using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;

namespace Legacy.Maliev.QuotationService.Application.Services;

/// <summary>Owns the retry-safe quotation decision and linked-order transition process.</summary>
public sealed class QuotationDecisionWorkflow(
    IQuotationService quotations,
    IOrderDecisionClient orders,
    IQuotationInvoiceCompletionStore? invoiceOperations = null) : IQuotationDecisionWorkflow
{
    /// <inheritdoc />
    public async Task<QuotationDecisionResponse> DecideAsync(
        int quotationId,
        QuotationDecisionRequest request,
        DateTimeOffset? expectedModifiedDate,
        CancellationToken cancellationToken)
    {
        QuotationAnalyticsContext? analyticsContext = null;
        if (request.Accepted && !request.TryGetAnalyticsContext(out analyticsContext))
            throw new ArgumentException("Invalid analytics context.", nameof(request));
        var origin = request.Accepted
            ? request.EmployeeInitiated ? QuotationAcceptanceOrigin.Employee : QuotationAcceptanceOrigin.Customer
            : (QuotationAcceptanceOrigin?)null;
        var persistence = analyticsContext is null
            ? await quotations.ApplyDecisionAsync(quotationId, request.Accepted, origin,
                expectedModifiedDate, cancellationToken, request.InvoiceId)
            : await quotations.ApplyDecisionAsync(quotationId, request.Accepted, origin,
                expectedModifiedDate, cancellationToken, request.InvoiceId, analyticsContext);
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
                CreateIdempotencyKey(quotationId, link.OrderId, request.Accepted,
                    (request.Accepted ? persistence.DecisionOrderVersion : null) ?? quotation.ModifiedDate ?? quotation.CreatedDate),
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

    /// <inheritdoc />
    public async Task<QuotationDecisionResponse> CompleteInvoiceAsync(QuotationInvoiceCompletionContext authority,
        CancellationToken cancellationToken)
    {
        var store = invoiceOperations ?? throw new InvalidOperationException("Missing invoice operation store.");
        var persistence = await store.ApplyAsync(authority, cancellationToken);
        if (persistence.Status == QuotationDecisionPersistenceStatus.NotFound) return Result(QuotationDecisionStatus.NotFound);
        if (persistence.Status != QuotationDecisionPersistenceStatus.Completed) return Result(QuotationDecisionStatus.Conflict);
        var progress = await store.ReadAsync(authority.OperationId, cancellationToken)
            ?? throw new InvalidDataException("Missing committed invoice operation.");
        if (progress.Receipt.State == "Completed") return BoundResult(QuotationDecisionStatus.Completed, progress);
        var claim = Guid.NewGuid();
        if (!await store.ClaimAsync(authority.OperationId, claim, cancellationToken))
            return BoundResult(QuotationDecisionStatus.DependencyUnavailable, progress);
        try
        {
            // Reload after conditional acquisition: a previous owner may have checkpointed before releasing.
            progress = await store.ReadAsync(authority.OperationId, cancellationToken)
                ?? throw new InvalidDataException("Missing claimed invoice operation.");
            var completed = progress.CompletedOrderIds.ToList();
            foreach (var orderId in progress.OrderIds.Except(completed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transition = await orders.TransitionAsync(orderId, true,
                    CreateIdempotencyKey(authority.QuotationId, orderId, true, progress.OrderVersion), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (transition != OrderDecisionResult.Completed)
                {
                    var conflict = transition != OrderDecisionResult.Unavailable;
                    await store.CheckpointAsync(authority.OperationId, claim, completed,
                        conflict ? "OrdersConflict" : "OrdersPartial", cancellationToken);
                    return new(conflict ? QuotationDecisionStatus.DependencyConflict : QuotationDecisionStatus.DependencyUnavailable,
                        completed.Count, progress.OrderIds.Count, persistence.Quotation?.ModifiedDate);
                }
                completed.Add(orderId);
                if (!await store.CheckpointAsync(authority.OperationId, claim, completed,
                        completed.Count == progress.OrderIds.Count ? "Completed" : "OrdersPartial", cancellationToken))
                    return new(QuotationDecisionStatus.DependencyUnavailable, completed.Count,
                        progress.OrderIds.Count, persistence.Quotation?.ModifiedDate);
            }
            if (progress.OrderIds.Count == 0
                && !await store.CheckpointAsync(authority.OperationId, claim, completed, "Completed", cancellationToken))
                return BoundResult(QuotationDecisionStatus.DependencyUnavailable, progress);
            return new(QuotationDecisionStatus.Completed, completed.Count, progress.OrderIds.Count, persistence.Quotation?.ModifiedDate);
        }
        finally
        {
            // Caller cancellation cannot strand an indefinite owner; a failed release retains the finite persisted lease.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.ReleaseAsync(authority.OperationId, claim, cleanup.Token); }
            catch (Exception) { /* Recovery remains fenced by the original two-minute lease. */ }
        }
    }

    private static QuotationDecisionResponse BoundResult(QuotationDecisionStatus status, QuotationInvoiceCompletionProgress progress)
        => new(status, progress.Receipt.CompletedOrders, progress.Receipt.TotalOrders,
            DateTime.SpecifyKind(DateTime.ParseExact(progress.Receipt.ModifiedDate, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind), DateTimeKind.Unspecified));

    private static QuotationDecisionResponse Result(QuotationDecisionStatus status) => new(status, 0, 0, null);

    private static string CreateIdempotencyKey(int quotationId, int orderId, bool accepted, DateTime? version)
    {
        var normalizedVersion = version is null
            ? DateTime.UnixEpoch
            : DateTime.SpecifyKind(version.Value, DateTimeKind.Utc);
        return $"quotation-{quotationId}-{(accepted ? "accepted" : "declined")}-{normalizedVersion.Ticks:x}-order-{orderId}";
    }
}
