namespace Legacy.Maliev.QuotationService.Application.Models;

public sealed record QuotationResponse(int Id, int? CustomerId, int? EmployeeId, int? InvoiceId, int Period, DateTime ExpirationDate, decimal Subtotal, decimal Vat, decimal Total, decimal? WithholdingTax, decimal? QuotedAmount, int CurrencyId, string? Comment, string? Fob, string? ShippedVia, string? Terms, bool? Accepted, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationRequest(int? CustomerId, int? EmployeeId, int? InvoiceId, int Period, DateTime ExpirationDate, decimal Subtotal, decimal Vat, decimal Total, decimal? WithholdingTax, int CurrencyId, string? Comment, string? Fob, string? ShippedVia, string? Terms, bool? Accepted, int? SourceRequestId = null, Guid? SourceJourneyId = null);
public sealed record QuotationStatsResponse(int Accepted, int Declined, int Open);
public sealed record QuotationOutcomeReadback(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<QuotationOutcomeReadbackDay> Days);
public sealed record QuotationOutcomeReadbackDay(
    DateTime DayUtc,
    int PersistedQuotationCount,
    int AcceptedQuotationCount,
    IReadOnlyList<AcceptedQuotedAmountByCurrency> AcceptedQuotedAmountsByCurrency);
public sealed record AcceptedQuotedAmountByCurrency(
    int CurrencyId,
    decimal QuotedAmount,
    int AcceptedQuotationCount);
public sealed record QuotationDocumentSnapshot(QuotationResponse Quotation, IReadOnlyList<QuotationOrderItemResponse> OrderItems, IReadOnlyList<QuotationFileResponse> Files);
public sealed record CustomerQuotationDetails(
    QuotationResponse Quotation,
    IReadOnlyList<QuotationOrderItemResponse> OrderItems,
    IReadOnlyList<QuotationOrderLinkResponse> Orders,
    IReadOnlyList<QuotationFileResponse> Files);
public sealed record QuotationOrderItemResponse(int Id, int QuotationId, int? OrderId, string? Description, int? Quantity, decimal? UnitPrice, decimal? Subtotal, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationOrderItemRequest(int QuotationId, int? OrderId, string? Description, int? Quantity, decimal? UnitPrice);
public sealed record QuotationOrderLinkResponse(int Id, int QuotationId, int OrderId, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationOrderLinkRequest(int QuotationId, int OrderId);
public sealed record QuotationFileResponse(int Id, int QuotationId, string Bucket, string ObjectName, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationFileRequest(int? QuotationId, string Bucket, string ObjectName);
public sealed record QuotationRequestResponse(int Id, string? FirstName, string? LastName, string? Email, string? TelephoneNumber, string? Country, string? CompanyName, string? TaxIdentification, string? Message, string? InternalComment, bool? Done, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationRequestRequest(string? FirstName, string? LastName, string? Email, string? TelephoneNumber, string? Country, string? CompanyName, string? TaxIdentification, string? Message, string? InternalComment, bool? Done);
public sealed record QuotationRequestFileResponse(int Id, int? RequestId, string? Bucket, string? ObjectName, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationRequestFileRequest(int? RequestId, string? Bucket, string? ObjectName);

public sealed record PaginatedResponse<T>(IReadOnlyList<T> Items, int PageIndex, int TotalPages, int TotalRecords)
{
    public bool HasNextPage => PageIndex < TotalPages;
    public bool HasPreviousPage => PageIndex > 1;
}

public enum QuotationSortType { QuotationId_Ascending, QuotationId_Descending, QuotationCreatedDate_Ascending, QuotationCreatedDate_Descending, QuotationModifiedDate_Ascending, QuotationModifiedDate_Descending }
public enum RequestSortType { RequestId_Ascending, RequestId_Descending, RequestCreatedDate_Ascending, RequestCreatedDate_Descending, RequestModifiedDate_Ascending, RequestModifiedDate_Descending }
public enum UpdateResult { Updated, NotFound, Conflict }
public sealed record QuotationDecisionRequest(bool Accepted, bool EmployeeInitiated = false);
public sealed record QuotationDecisionResponse(QuotationDecisionStatus Status, int CompletedOrders, int TotalOrders, DateTime? ModifiedDate);
public enum QuotationDecisionStatus { Completed, NotFound, Conflict, DependencyConflict, DependencyUnavailable }
public sealed record QuotationDecisionPersistenceResult(
    QuotationDecisionPersistenceStatus Status,
    QuotationResponse? Quotation);
public enum QuotationDecisionPersistenceStatus { Completed, NotFound, Conflict }
public enum QuotationAcceptanceOrigin { Customer, Employee }
public enum OrderDecisionResult { Completed, Conflict, NotFound, Unavailable }
