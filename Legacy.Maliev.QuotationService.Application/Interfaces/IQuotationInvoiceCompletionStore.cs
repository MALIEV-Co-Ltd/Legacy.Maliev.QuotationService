using Legacy.Maliev.QuotationService.Application.Models;

namespace Legacy.Maliev.QuotationService.Application.Interfaces;

/// <summary>Owns the atomic quotation link and durable operation progress.</summary>
public interface IQuotationInvoiceCompletionStore
{
    Task<QuotationDecisionPersistenceResult> ApplyAsync(QuotationInvoiceCompletionContext authority, CancellationToken cancellationToken);
    Task<QuotationInvoiceCompletionProgress?> ReadAsync(Guid operationId, CancellationToken cancellationToken);
    Task<bool> ClaimAsync(Guid operationId, Guid claimId, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid operationId, Guid claimId, CancellationToken cancellationToken);
    Task<bool> CheckpointAsync(Guid operationId, Guid claimId, IReadOnlyList<int> completedOrders, string state, CancellationToken cancellationToken);
}
