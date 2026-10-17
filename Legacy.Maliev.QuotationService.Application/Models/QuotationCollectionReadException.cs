namespace Legacy.Maliev.QuotationService.Application.Models;

/// <summary>A collection read cannot preserve the source paging contract for the supplied size.</summary>
public sealed class QuotationCollectionReadException : Exception
{
    /// <summary>Initializes a collection-read failure without retaining request values or provider details.</summary>
    public QuotationCollectionReadException() : base("Collection paging could not be processed.") { }
}
