namespace Legacy.Maliev.QuotationService.Domain;

/// <summary>Immutable admission and frozen order membership with conditional, leased progress.</summary>
public sealed class QuotationInvoiceCompletionOperation
{
    public Guid OperationId { get; set; }
    public int QuotationId { get; set; }
    public int InvoiceId { get; set; }
    public string AuthorityJson { get; set; } = string.Empty;
    public string OrderIdsJson { get; set; } = "[]";
    public string CompletedOrderIdsJson { get; set; } = "[]";
    public string State { get; set; } = "QuotationCommitted";
    public DateTime DecisionOrderVersion { get; set; }
    public DateTime ModifiedDate { get; set; }
    public Guid? ClaimId { get; set; }
    public DateTime? ClaimUntil { get; set; }
}
