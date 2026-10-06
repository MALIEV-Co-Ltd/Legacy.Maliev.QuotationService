using System.Globalization;

namespace Legacy.Maliev.QuotationService.Application.Models;

/// <summary>Immutable operation authority supplied only after independent capability verification.</summary>
public sealed record QuotationInvoiceCompletionContext(Guid OperationId, int QuotationId, int InvoiceId,
    string OriginIssuer, string EmployeeSubject, string RequesterSubject, string ExecutorSubject,
    string OriginalQuotationVersion, string FinancialBinding, string FinancialBindingVersion);

/// <summary>Quotation-owned recovery evidence; Completed includes every frozen linked order.</summary>
public sealed record QuotationInvoiceCompletionReceipt(int ContractVersion, string OperationId, int QuotationId,
    int InvoiceId, string OriginIssuer, string EmployeeSubject, string RequesterSubject, string ExecutorSubject,
    string OriginalQuotationVersion, string FinancialBinding, string FinancialBindingVersion, string State,
    string DecisionOrderVersion, int CompletedOrders, int TotalOrders, string ModifiedDate);

/// <summary>Internal persisted progress without bearer credentials or mutable downstream inference.</summary>
public sealed record QuotationInvoiceCompletionProgress(QuotationInvoiceCompletionReceipt Receipt,
    IReadOnlyList<int> OrderIds, IReadOnlyList<int> CompletedOrderIds)
{
    /// <summary>Returns the original, immutable order-transition version.</summary>
    public DateTime OrderVersion => DateTime.ParseExact(Receipt.DecisionOrderVersion, "O",
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
