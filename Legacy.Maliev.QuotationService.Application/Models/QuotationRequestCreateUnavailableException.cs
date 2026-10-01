namespace Legacy.Maliev.QuotationService.Application.Models;

/// <summary>A request-create operation could not be safely confirmed; this does not prove that no root was persisted.</summary>
public sealed class QuotationRequestCreateUnavailableException : Exception
{
    public QuotationRequestCreateUnavailableException() : base("Request creation could not be confirmed.") { }
}
