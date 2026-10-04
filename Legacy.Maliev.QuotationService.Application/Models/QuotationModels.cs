using System.ComponentModel.DataAnnotations;

namespace Legacy.Maliev.QuotationService.Application.Models;

public sealed record QuotationResponse(int Id, int? CustomerId, int? EmployeeId, int? InvoiceId, int Period, DateTime ExpirationDate, decimal Subtotal, decimal Vat, decimal Total, decimal? WithholdingTax, decimal? QuotedAmount, int CurrencyId, string? Comment, string? Fob, string? ShippedVia, string? Terms, bool? Accepted, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertQuotationRequest(int? CustomerId, int? EmployeeId, int? InvoiceId, int Period, DateTime ExpirationDate, decimal Subtotal, decimal Vat, decimal Total, decimal? WithholdingTax, int CurrencyId, string? Comment, string? Fob, string? ShippedVia, string? Terms, bool? Accepted, int? SourceRequestId = null, Guid? SourceJourneyId = null);
public sealed record QuotationStatsResponse(int Accepted, int Declined, int Open);
public sealed record QuotationOutcomeReadback(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<QuotationOutcomeReadbackDay> Days)
{
    public string TechnicalConversionAvailability { get; } = "unavailable";
    public string QualifiedCustomerAvailability { get; } = "unavailable";
    public string RevenueAvailability { get; } = "unavailable";
}
public sealed record QuotationOutcomeReadbackDay(
    DateTime DayUtc,
    int PersistedQuotationCount,
    int AcceptedQuotationCount,
    int SourceAttributedPersistedQuotationCount,
    int SourceAttributedAcceptedQuotationCount,
    int UnattributedPersistedQuotationCount,
    int UnattributedAcceptedQuotationCount,
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
public sealed record QuotationRequestResponse(
    int Id,
    string? FirstName,
    string? LastName,
    string? Email,
    string? TelephoneNumber,
    string? Country,
    string? CompanyName,
    string? TaxIdentification,
    string? Message,
    string? InternalComment,
    bool? Done,
    DateTime? CreatedDate,
    DateTime? ModifiedDate,
    Guid? JourneyId = null,
    string? TransactionId = null);
public sealed record UpsertQuotationRequestRequest(string? FirstName, string? LastName, string? Email, string? TelephoneNumber, string? Country, string? CompanyName, string? TaxIdentification, string? Message, string? InternalComment, bool? Done, Guid? JourneyId = null);
public sealed record QualificationStateUpdateRequest(
    [Required, MaxLength(32)] string State,
    [MaxLength(512)] string? Reason,
    [MaxLength(32)] string? Completeness,
    [Range(0, int.MaxValue)] int DuplicateCount,
    [MaxLength(64)] string? UnmatchedClassification,
    [Required, MaxLength(128)] string IdempotencyKey,
    [Range(0, int.MaxValue)] int ExpectedVersion);
public sealed record QualificationReceipt(
    int RequestId,
    Guid? JourneyId,
    string TransactionId,
    string State,
    DateTime? StateChangedUtc,
    int Version,
    IReadOnlyList<QualificationReceiptEvent> Events);
public sealed record QualificationReceiptEvent(
    long Id,
    string PreviousState,
    string State,
    int Version,
    DateTime ChangedUtc,
    string ChangedBy,
    string? Completeness,
    int DuplicateCount,
    string? UnmatchedClassification,
    string? Reason);
public sealed record QualificationUpdateResult(QualificationUpdateStatus Status, QualificationReceipt? Receipt);
public enum QualificationUpdateStatus { Completed, NotFound, VersionConflict, IdempotencyConflict }
public sealed record QualificationOutcomeReadback(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<QualificationOutcomeReadbackRequest> Requests);
public sealed record QualificationOutcomeReadbackRequest(
    int RequestId,
    DateTime CreatedUtc,
    string? TransactionId,
    Guid? JourneyId,
    string State);
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
/// <summary>A quotation decision with optional consent-gated first-acceptance analytics context.</summary>
/// <param name="Accepted">Whether the quotation is accepted.</param>
/// <param name="EmployeeInitiated">Whether this is an authorized employee-origin decision; service identity alone does not set this flag.</param>
/// <param name="InvoiceId">Optional invoice intent; positive identifiers link the persisted invoice atomically with the decision.</param>
public sealed record QuotationDecisionRequest(bool Accepted, bool EmployeeInitiated = false, int? InvoiceId = null)
{
    /// <summary>Optional consent-gated client identifier, supplied together with SessionId and limited to 128 raw characters.</summary>
    public string? ClientId { get; init; }
    /// <summary>Optional consent-gated session identifier, supplied together with ClientId and limited to 128 raw characters.</summary>
    public string? SessionId { get; init; }
    /// <summary>Optional opaque user identifier; nonblank values are limited to 128 raw characters and must not contain an at sign.</summary>
    public string? UserId { get; init; }
    /// <summary>Three-letter analytics currency required when the identifier pair is supplied, normalized to uppercase.</summary>
    public string? Currency { get; init; }

    public bool TryGetAnalyticsContext(out QuotationAnalyticsContext? context)
    {
        context = null;
        var hasClient = !string.IsNullOrWhiteSpace(ClientId);
        var hasSession = !string.IsNullOrWhiteSpace(SessionId);
        if (hasClient != hasSession) return false;
        if (!hasClient) return true;
        if (ClientId!.Length > 128 || SessionId!.Length > 128
            || !string.IsNullOrWhiteSpace(UserId) && (UserId.Length > 128 || UserId.Contains('@'))
            || Currency is not { Length: 3 } currency || !currency.All(char.IsLetter)) return false;

        context = new(ClientId.Trim(), SessionId.Trim(), currency.ToUpperInvariant(),
            string.IsNullOrWhiteSpace(UserId) ? null : UserId.Trim());
        return true;
    }
}
public sealed record QuotationAnalyticsContext(string ClientId, string SessionId, string Currency, string? UserId);
public sealed record QuotationDecisionResponse(QuotationDecisionStatus Status, int CompletedOrders, int TotalOrders, DateTime? ModifiedDate);
public enum QuotationDecisionStatus { Completed, NotFound, Conflict, DependencyConflict, DependencyUnavailable }
public sealed record QuotationDecisionPersistenceResult(
    QuotationDecisionPersistenceStatus Status,
    QuotationResponse? Quotation,
    DateTime? DecisionOrderVersion = null);
public enum QuotationDecisionPersistenceStatus { Completed, NotFound, Conflict }
public enum QuotationAcceptanceOrigin { Customer, Employee }
public enum OrderDecisionResult { Completed, Conflict, NotFound, Unavailable }
