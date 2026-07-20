using Legacy.Maliev.QuotationService.Application.Models;

namespace Legacy.Maliev.QuotationService.Application.Interfaces;

public interface IQuotationService
{
    Task<QuotationResponse> CreateQuotationAsync(UpsertQuotationRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteQuotationAsync(int id, CancellationToken cancellationToken);
    Task<QuotationResponse?> GetQuotationAsync(int id, CancellationToken cancellationToken);
    Task<CustomerQuotationDetails?> GetCustomerQuotationAsync(int customerId, int id, CancellationToken cancellationToken);
    Task<QuotationResponse?> GetQuotationByInvoiceAsync(int invoiceId, CancellationToken cancellationToken);
    Task<PaginatedResponse<QuotationResponse>?> GetQuotationsAsync(int? customerId, QuotationSortType? sort, string? search, int pageIndex, int pageSize, CancellationToken cancellationToken);
    Task<QuotationStatsResponse> GetStatsAsync(CancellationToken cancellationToken);
    Task<UpdateResult> UpdateQuotationAsync(int id, UpsertQuotationRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken);
    Task<decimal?> GetWithholdingTaxAsync(int id, CancellationToken cancellationToken);
    Task<QuotationDocumentSnapshot?> GetDocumentSnapshotAsync(int id, CancellationToken cancellationToken);
    Task<int> DeclineExpiredAsync(DateTime utcNow, CancellationToken cancellationToken);

    Task<QuotationOrderItemResponse> CreateOrderItemAsync(UpsertQuotationOrderItemRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteOrderItemAsync(int id, CancellationToken cancellationToken);
    Task<QuotationOrderItemResponse?> GetOrderItemAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationOrderItemResponse>> GetOrderItemsAsync(int quotationId, CancellationToken cancellationToken);
    Task<UpdateResult> UpdateOrderItemAsync(int id, UpsertQuotationOrderItemRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken);

    Task<QuotationOrderLinkResponse?> CreateOrderLinkAsync(int quotationId, int orderId, CancellationToken cancellationToken);
    Task<bool> DeleteOrderLinkAsync(int id, CancellationToken cancellationToken);
    Task<QuotationOrderLinkResponse?> GetOrderLinkAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationOrderLinkResponse>> GetOrderLinksAsync(int quotationId, CancellationToken cancellationToken);
    Task<bool> UpdateOrderLinkAsync(int id, UpsertQuotationOrderLinkRequest request, CancellationToken cancellationToken);

    Task<QuotationFileResponse?> CreateQuotationFileAsync(int quotationId, string bucket, string objectName, CancellationToken cancellationToken);
    Task<bool> DeleteQuotationFileAsync(int id, CancellationToken cancellationToken);
    Task<QuotationFileResponse?> GetQuotationFileAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationFileResponse>> GetQuotationFilesAsync(int quotationId, CancellationToken cancellationToken);
    Task<bool> UpdateQuotationFileAsync(int id, UpsertQuotationFileRequest request, CancellationToken cancellationToken);

    Task<QuotationRequestResponse> CreateRequestAsync(UpsertQuotationRequestRequest request, CancellationToken cancellationToken);
    Task<IdempotentRequestCreateResult> CreateRequestIdempotentlyAsync(
        UpsertQuotationRequestRequest request,
        string keyHash,
        string fingerprint,
        CancellationToken cancellationToken);
    Task<bool> DeleteRequestAsync(int id, CancellationToken cancellationToken);
    Task<QuotationRequestResponse?> GetRequestAsync(int id, CancellationToken cancellationToken);
    Task<PaginatedResponse<QuotationRequestResponse>?> GetRequestsAsync(RequestSortType? sort, string? search, int pageIndex, int pageSize, CancellationToken cancellationToken);
    Task<UpdateResult> UpdateRequestAsync(int id, UpsertQuotationRequestRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken);

    Task<QuotationRequestFileResponse?> CreateRequestFileAsync(int requestId, string bucket, string objectName, CancellationToken cancellationToken);
    Task<bool> DeleteRequestFileAsync(int id, CancellationToken cancellationToken);
    Task<QuotationRequestFileResponse?> GetRequestFileAsync(int id, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationRequestFileResponse>> GetRequestFilesAsync(int requestId, CancellationToken cancellationToken);
    Task<bool> UpdateRequestFileAsync(int id, UpsertQuotationRequestFileRequest request, CancellationToken cancellationToken);
}

public interface IQuotationCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken) where T : class;
    Task RemoveAsync(string key, CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    Task<IdempotencyBindingResult> BindAsync(
        string scope,
        string key,
        string fingerprint,
        CancellationToken cancellationToken) =>
        Task.FromResult(IdempotencyBindingResult.Unavailable);
    Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class;
    Task SetAsync<T>(string scope, string key, T response, CancellationToken cancellationToken) where T : class;
}

public enum IdempotencyBindingResult
{
    Unavailable,
    Acquired,
    Matched,
    Conflict,
}

public sealed record IdempotentRequestCreateResult(
    QuotationRequestResponse? Response,
    IdempotencyBindingResult Binding);

public interface IQuotationDecisionWorkflow
{
    Task<QuotationDecisionResponse> DecideAsync(int quotationId, QuotationDecisionRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken);
}

public interface IOrderDecisionClient
{
    Task<OrderDecisionResult> TransitionAsync(int orderId, bool accepted, string idempotencyKey, CancellationToken cancellationToken);
}
