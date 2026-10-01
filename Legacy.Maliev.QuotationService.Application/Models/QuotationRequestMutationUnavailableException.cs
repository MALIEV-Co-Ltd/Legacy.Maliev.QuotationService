namespace Legacy.Maliev.QuotationService.Application.Models;

/// <summary>A receiptless request mutation could not be confirmed; it may already have committed.</summary>
public sealed class QuotationRequestMutationUnavailableException(Exception cause)
    : Exception("Request mutation could not be confirmed.", cause);
