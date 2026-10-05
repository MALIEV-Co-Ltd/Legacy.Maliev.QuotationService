namespace Legacy.Maliev.QuotationService.Application.Models;

/// <summary>Request attachment metadata creation could not be confirmed safely.</summary>
public sealed class QuotationRequestFileCreateUnavailableException : Exception
{
    public QuotationRequestFileCreateUnavailableException() : base("Request attachment creation could not be confirmed.") { }
}
