using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Application.Services;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Data;

public sealed class QuotationRepository(
    QuotationDbContext quotations,
    QuotationRequestDbContext requests,
    IQuotationCache cache,
    TimeProvider timeProvider) : IQuotationService
{
    public async Task<QuotationResponse> CreateQuotationAsync(UpsertQuotationRequest request, CancellationToken cancellationToken)
    {
        var now = Now(); var entity = Map(new Quotation(), request); entity.CreatedDate = now; entity.ModifiedDate = now;
        quotations.Add(entity); await quotations.SaveChangesAsync(cancellationToken); return ToResponse(entity);
    }

    public async Task<bool> DeleteQuotationAsync(int id, CancellationToken cancellationToken)
    {
        var deleted = await quotations.Quotations.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
        if (deleted) await cache.RemoveAsync(QuotationKey(id), cancellationToken); return deleted;
    }

    public async Task<QuotationResponse?> GetQuotationAsync(int id, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync<QuotationResponse>(QuotationKey(id), cancellationToken); if (cached is not null) return cached;
        var value = await Project(quotations.Quotations.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken);
        if (value is not null) await cache.SetAsync(QuotationKey(id), value, TimeSpan.FromMinutes(2), cancellationToken); return value;
    }

    public async Task<CustomerQuotationDetails?> GetCustomerQuotationAsync(int customerId, int id, CancellationToken cancellationToken)
    {
        var quotation = await Project(quotations.Quotations.AsNoTracking()
            .Where(x => x.Id == id && x.CustomerId == customerId))
            .SingleOrDefaultAsync(cancellationToken);
        if (quotation is null) return null;

        var orderItems = await GetOrderItemsAsync(id, cancellationToken);
        var orders = await GetOrderLinksAsync(id, cancellationToken);
        var files = await GetQuotationFilesAsync(id, cancellationToken);
        return new CustomerQuotationDetails(quotation, orderItems, orders, files);
    }

    public Task<QuotationResponse?> GetQuotationByInvoiceAsync(int invoiceId, CancellationToken cancellationToken) =>
        Project(quotations.Quotations.AsNoTracking().Where(x => x.InvoiceId == invoiceId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PaginatedResponse<QuotationResponse>?> GetQuotationsAsync(int? customerId, QuotationSortType? sort, string? search, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        IQueryable<Quotation> query = quotations.Quotations.AsNoTracking(); if (customerId is not null) query = query.Where(x => x.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(search)) { var value = search.Trim(); var numeric = int.TryParse(value, out var id); var pattern = $"%{value}%"; query = query.Where(x => (numeric && (x.Id == id || x.CustomerId == id)) || (x.Comment != null && EF.Functions.ILike(x.Comment, pattern))); }
        query = sort switch { QuotationSortType.QuotationId_Descending => query.OrderByDescending(x => x.Id), QuotationSortType.QuotationCreatedDate_Ascending => query.OrderBy(x => x.CreatedDate), QuotationSortType.QuotationCreatedDate_Descending => query.OrderByDescending(x => x.CreatedDate), QuotationSortType.QuotationModifiedDate_Ascending => query.OrderBy(x => x.ModifiedDate), QuotationSortType.QuotationModifiedDate_Descending => query.OrderByDescending(x => x.ModifiedDate), _ => query.OrderBy(x => x.Id) };
        return await PageAsync(Project(query), pageIndex, pageSize, cancellationToken);
    }

    public async Task<QuotationStatsResponse> GetStatsAsync(CancellationToken cancellationToken) =>
        await quotations.Quotations
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new QuotationStatsResponse(
                group.Count(x => x.Accepted == true),
                group.Count(x => x.Accepted == false),
                group.Count(x => x.Accepted == null)))
            .SingleOrDefaultAsync(cancellationToken)
        ?? new QuotationStatsResponse(0, 0, 0);

    public async Task<UpdateResult> UpdateQuotationAsync(int id, UpsertQuotationRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken)
    {
        var entity = await quotations.Quotations.FindAsync([id], cancellationToken); if (entity is null) return UpdateResult.NotFound;
        if (expectedModifiedDate is not null) quotations.Entry(entity).Property(x => x.ModifiedDate).OriginalValue = expectedModifiedDate.Value.UtcDateTime;
        Map(entity, request).ModifiedDate = Now();
        try { await quotations.SaveChangesAsync(cancellationToken); await cache.RemoveAsync(QuotationKey(id), cancellationToken); return UpdateResult.Updated; } catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; }
    }

    public async Task<decimal?> GetWithholdingTaxAsync(int id, CancellationToken cancellationToken)
    {
        var subtotal = await quotations.Quotations.AsNoTracking().Where(x => x.Id == id).Select(x => (decimal?)x.Subtotal).SingleOrDefaultAsync(cancellationToken);
        return subtotal is null ? null : QuotationCalculations.WithholdingAmount(subtotal.Value, timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task<QuotationDocumentSnapshot?> GetDocumentSnapshotAsync(int id, CancellationToken cancellationToken)
    {
        var quotation = await GetQuotationAsync(id, cancellationToken); if (quotation is null) return null;
        return new(quotation, await GetOrderItemsAsync(id, cancellationToken), await GetQuotationFilesAsync(id, cancellationToken));
    }

    public Task<int> DeclineExpiredAsync(DateTime utcNow, CancellationToken cancellationToken) => quotations.Quotations
        .Where(x => x.ExpirationDate < utcNow && x.Accepted == null)
        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Accepted, false).SetProperty(x => x.ModifiedDate, utcNow), cancellationToken);

    public async Task<QuotationOrderItemResponse> CreateOrderItemAsync(UpsertQuotationOrderItemRequest request, CancellationToken cancellationToken)
    {
        var now = Now(); var entity = new QuotationOrderItem { QuotationId = request.QuotationId, OrderId = request.OrderId, Description = request.Description, Quantity = request.Quantity, UnitPrice = request.UnitPrice, CreatedDate = now, ModifiedDate = now };
        quotations.Add(entity); await quotations.SaveChangesAsync(cancellationToken); return (await GetOrderItemAsync(entity.Id, cancellationToken))!;
    }
    public async Task<bool> DeleteOrderItemAsync(int id, CancellationToken cancellationToken) => await quotations.OrderItems.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
    public Task<QuotationOrderItemResponse?> GetOrderItemAsync(int id, CancellationToken cancellationToken) => ProjectItems(quotations.OrderItems.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken);
    public async Task<IReadOnlyList<QuotationOrderItemResponse>> GetOrderItemsAsync(int quotationId, CancellationToken cancellationToken) => await ProjectItems(quotations.OrderItems.AsNoTracking().Where(x => x.QuotationId == quotationId).OrderBy(x => x.Id)).ToListAsync(cancellationToken);
    public async Task<UpdateResult> UpdateOrderItemAsync(int id, UpsertQuotationOrderItemRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken)
    {
        var entity = await quotations.OrderItems.FindAsync([id], cancellationToken); if (entity is null) return UpdateResult.NotFound; if (expectedModifiedDate is not null) quotations.Entry(entity).Property(x => x.ModifiedDate).OriginalValue = expectedModifiedDate.Value.UtcDateTime;
        entity.QuotationId = request.QuotationId; entity.OrderId = request.OrderId; entity.Description = request.Description; entity.Quantity = request.Quantity; entity.UnitPrice = request.UnitPrice; entity.ModifiedDate = Now();
        try { await quotations.SaveChangesAsync(cancellationToken); return UpdateResult.Updated; } catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; }
    }

    public async Task<QuotationOrderLinkResponse?> CreateOrderLinkAsync(int quotationId, int orderId, CancellationToken cancellationToken)
    {
        if (!await quotations.Quotations.AnyAsync(x => x.Id == quotationId, cancellationToken)) return null; var now = Now(); var entity = new QuotationOrderLink { QuotationId = quotationId, OrderId = orderId, CreatedDate = now, ModifiedDate = now }; quotations.Add(entity); await quotations.SaveChangesAsync(cancellationToken); return ToResponse(entity);
    }
    public async Task<bool> DeleteOrderLinkAsync(int id, CancellationToken cancellationToken) => await quotations.OrderLinks.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
    public Task<QuotationOrderLinkResponse?> GetOrderLinkAsync(int id, CancellationToken cancellationToken) => ProjectLinks(quotations.OrderLinks.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken);
    public async Task<IReadOnlyList<QuotationOrderLinkResponse>> GetOrderLinksAsync(int quotationId, CancellationToken cancellationToken) => await ProjectLinks(quotations.OrderLinks.AsNoTracking().Where(x => x.QuotationId == quotationId).OrderBy(x => x.Id)).ToListAsync(cancellationToken);
    public async Task<bool> UpdateOrderLinkAsync(int id, UpsertQuotationOrderLinkRequest request, CancellationToken cancellationToken) { var entity = await quotations.OrderLinks.FindAsync([id], cancellationToken); if (entity is null) return false; entity.QuotationId = request.QuotationId; entity.OrderId = request.OrderId; entity.ModifiedDate = Now(); await quotations.SaveChangesAsync(cancellationToken); return true; }

    public async Task<QuotationFileResponse?> CreateQuotationFileAsync(int quotationId, string bucket, string objectName, CancellationToken cancellationToken)
    {
        if (!await quotations.Quotations.AnyAsync(x => x.Id == quotationId, cancellationToken)) return null; var now = Now(); var entity = new QuotationFile { QuotationId = quotationId, Bucket = bucket.Trim(), ObjectName = objectName.Trim(), CreatedDate = now, ModifiedDate = now }; quotations.Add(entity); await quotations.SaveChangesAsync(cancellationToken); return ToResponse(entity);
    }
    public async Task<bool> DeleteQuotationFileAsync(int id, CancellationToken cancellationToken) => await quotations.Files.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
    public Task<QuotationFileResponse?> GetQuotationFileAsync(int id, CancellationToken cancellationToken) => ProjectFiles(quotations.Files.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken);
    public async Task<IReadOnlyList<QuotationFileResponse>> GetQuotationFilesAsync(int quotationId, CancellationToken cancellationToken) => await ProjectFiles(quotations.Files.AsNoTracking().Where(x => x.QuotationId == quotationId).OrderBy(x => x.Id)).ToListAsync(cancellationToken);
    public async Task<bool> UpdateQuotationFileAsync(int id, UpsertQuotationFileRequest request, CancellationToken cancellationToken) { var entity = await quotations.Files.FindAsync([id], cancellationToken); if (entity is null) return false; entity.QuotationId = request.QuotationId ?? entity.QuotationId; entity.Bucket = request.Bucket.Trim(); entity.ObjectName = request.ObjectName.Trim(); entity.ModifiedDate = Now(); await quotations.SaveChangesAsync(cancellationToken); return true; }

    public async Task<QuotationRequestResponse> CreateRequestAsync(UpsertQuotationRequestRequest request, CancellationToken cancellationToken)
    {
        var now = Now(); var entity = Map(new QuotationRequest(), request); entity.CreatedDate = now; entity.ModifiedDate = now; requests.Add(entity); await requests.SaveChangesAsync(cancellationToken); return ToResponse(entity);
    }
    public async Task<IdempotentRequestCreateResult> CreateRequestIdempotentlyAsync(
        UpsertQuotationRequestRequest request,
        string keyHash,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(keyHash) || !IsSha256(fingerprint))
        {
            return new(null, IdempotencyBindingResult.Unavailable);
        }

        await using var transaction = await requests.Database.BeginTransactionAsync(cancellationToken);
        await requests.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({$"quotation-request\n{keyHash}"}, 0))",
            cancellationToken);

        var existingBinding = await requests.RequestCreateIdempotency
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.KeyHash == keyHash, cancellationToken);
        if (existingBinding is not null)
        {
            if (!string.Equals(existingBinding.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new(null, IdempotencyBindingResult.Conflict);
            }

            var existingResponse = await ProjectRequests(
                    requests.Requests.AsNoTracking().Where(value => value.Id == existingBinding.RequestId))
                .SingleOrDefaultAsync(cancellationToken);
            return existingResponse is null
                ? new(null, IdempotencyBindingResult.Unavailable)
                : new(existingResponse, IdempotencyBindingResult.Matched);
        }

        var now = Now();
        var entity = Map(new QuotationRequest(), request);
        entity.CreatedDate = now;
        entity.ModifiedDate = now;
        requests.Add(entity);
        await requests.SaveChangesAsync(cancellationToken);
        requests.RequestCreateIdempotency.Add(new RequestCreateIdempotency
        {
            KeyHash = keyHash,
            Fingerprint = fingerprint,
            RequestId = entity.Id,
        });
        await requests.SaveChangesAsync(cancellationToken);
        await requests.Entry(entity).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ToResponse(entity), IdempotencyBindingResult.Acquired);
    }
    public async Task<bool> DeleteRequestAsync(int id, CancellationToken cancellationToken) { var deleted = await requests.Requests.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1; if (deleted) await cache.RemoveAsync(RequestKey(id), cancellationToken); return deleted; }
    public async Task<QuotationRequestResponse?> GetRequestAsync(int id, CancellationToken cancellationToken) { var cached = await cache.GetAsync<QuotationRequestResponse>(RequestKey(id), cancellationToken); if (cached is not null) return cached; var value = await ProjectRequests(requests.Requests.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken); if (value is not null) await cache.SetAsync(RequestKey(id), value, TimeSpan.FromMinutes(2), cancellationToken); return value; }
    public async Task<PaginatedResponse<QuotationRequestResponse>?> GetRequestsAsync(RequestSortType? sort, string? search, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        IQueryable<QuotationRequest> query = requests.Requests.AsNoTracking(); if (!string.IsNullOrWhiteSpace(search)) { var value = search.Trim(); var numeric = int.TryParse(value, out var id); var pattern = $"%{value}%"; query = query.Where(x => (numeric && x.Id == id) || (!numeric && ((x.FirstName != null && EF.Functions.ILike(x.FirstName, pattern)) || (x.LastName != null && EF.Functions.ILike(x.LastName, pattern)) || (x.Message != null && EF.Functions.ILike(x.Message, pattern)) || (x.TelephoneNumber != null && EF.Functions.ILike(x.TelephoneNumber, pattern)) || (x.CompanyName != null && EF.Functions.ILike(x.CompanyName, pattern)) || (x.Email != null && EF.Functions.ILike(x.Email, pattern)) || (x.InternalComment != null && EF.Functions.ILike(x.InternalComment, pattern)) || (x.Country != null && EF.Functions.ILike(x.Country, pattern))))); }
        query = sort switch { RequestSortType.RequestId_Descending => query.OrderByDescending(x => x.Id), RequestSortType.RequestCreatedDate_Ascending => query.OrderBy(x => x.CreatedDate), RequestSortType.RequestCreatedDate_Descending => query.OrderByDescending(x => x.CreatedDate), RequestSortType.RequestModifiedDate_Ascending => query.OrderBy(x => x.ModifiedDate), RequestSortType.RequestModifiedDate_Descending => query.OrderByDescending(x => x.ModifiedDate), _ => query.OrderBy(x => x.Id) }; return await PageAsync(ProjectRequests(query), pageIndex, pageSize, cancellationToken);
    }
    public async Task<UpdateResult> UpdateRequestAsync(int id, UpsertQuotationRequestRequest request, DateTimeOffset? expectedModifiedDate, CancellationToken cancellationToken)
    {
        var entity = await requests.Requests.FindAsync([id], cancellationToken); if (entity is null) return UpdateResult.NotFound; if (expectedModifiedDate is not null) requests.Entry(entity).Property(x => x.ModifiedDate).OriginalValue = expectedModifiedDate.Value.UtcDateTime; Map(entity, request).ModifiedDate = Now(); try { await requests.SaveChangesAsync(cancellationToken); await cache.RemoveAsync(RequestKey(id), cancellationToken); return UpdateResult.Updated; } catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; }
    }

    public async Task<QuotationRequestFileResponse?> CreateRequestFileAsync(
        int requestId,
        string bucket,
        string objectName,
        CancellationToken cancellationToken)
    {
        var normalizedBucket = bucket.Trim();
        var normalizedObjectName = objectName.Trim();
        var lockIdentity = $"{requestId}\n{normalizedBucket.Length}:{normalizedBucket}\n{normalizedObjectName.Length}:{normalizedObjectName}";

        await using var transaction = await requests.Database.BeginTransactionAsync(cancellationToken);
        await requests.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))",
            cancellationToken);

        if (!await requests.Requests.AnyAsync(x => x.Id == requestId, cancellationToken))
        {
            return null;
        }

        var existing = await ProjectRequestFiles(requests.Files.AsNoTracking().Where(file =>
                    file.RequestId == requestId
                    && file.Bucket == normalizedBucket
                    && file.ObjectName == normalizedObjectName)
                .OrderBy(file => file.Id))
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var now = Now();
        var entity = new QuotationRequestFile
        {
            RequestId = requestId,
            Bucket = normalizedBucket,
            ObjectName = normalizedObjectName,
            CreatedDate = now,
            ModifiedDate = now,
        };
        requests.Add(entity);
        await requests.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(entity);
    }
    public async Task<bool> DeleteRequestFileAsync(int id, CancellationToken cancellationToken) => await requests.Files.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
    public Task<QuotationRequestFileResponse?> GetRequestFileAsync(int id, CancellationToken cancellationToken) => ProjectRequestFiles(requests.Files.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(cancellationToken);
    public async Task<IReadOnlyList<QuotationRequestFileResponse>> GetRequestFilesAsync(int requestId, CancellationToken cancellationToken) => await ProjectRequestFiles(requests.Files.AsNoTracking().Where(x => x.RequestId == requestId).OrderBy(x => x.Id)).ToListAsync(cancellationToken);
    public async Task<bool> UpdateRequestFileAsync(int id, UpsertQuotationRequestFileRequest request, CancellationToken cancellationToken) { var entity = await requests.Files.FindAsync([id], cancellationToken); if (entity is null) return false; entity.RequestId = request.RequestId; entity.Bucket = request.Bucket; entity.ObjectName = request.ObjectName; entity.ModifiedDate = Now(); await requests.SaveChangesAsync(cancellationToken); return true; }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
    private static string QuotationKey(int id) => $"quotation:{id}";
    private static string RequestKey(int id) => $"request:{id}";
    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static async Task<PaginatedResponse<T>?> PageAsync<T>(IQueryable<T> query, int pageIndex, int pageSize, CancellationToken cancellationToken) { pageIndex = Math.Max(pageIndex, 1); pageSize = Math.Clamp(pageSize, 1, 250); var total = await query.CountAsync(cancellationToken); if (total == 0) return null; var items = await query.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken); return new(items, pageIndex, (int)Math.Ceiling(total / (double)pageSize), total); }
    private static Quotation Map(Quotation x, UpsertQuotationRequest r) { x.CustomerId = r.CustomerId; x.EmployeeId = r.EmployeeId; x.InvoiceId = r.InvoiceId; x.Period = r.Period; x.ExpirationDate = r.ExpirationDate; x.Subtotal = r.Subtotal; x.Vat = r.Vat; x.Total = r.Total; x.WithholdingTax = r.WithholdingTax; x.CurrencyId = r.CurrencyId; x.Comment = r.Comment; x.Fob = r.Fob; x.ShippedVia = r.ShippedVia; x.Terms = r.Terms; x.Accepted = r.Accepted; return x; }
    private static QuotationRequest Map(QuotationRequest x, UpsertQuotationRequestRequest r) { x.FirstName = r.FirstName; x.LastName = r.LastName; x.Email = r.Email; x.TelephoneNumber = r.TelephoneNumber; x.Country = r.Country; x.CompanyName = r.CompanyName; x.TaxIdentification = r.TaxIdentification; x.Message = r.Message; x.InternalComment = r.InternalComment; x.Done = r.Done; return x; }
    private static QuotationResponse ToResponse(Quotation x) => new(x.Id, x.CustomerId, x.EmployeeId, x.InvoiceId, x.Period, x.ExpirationDate, x.Subtotal, x.Vat, x.Total, x.WithholdingTax, x.QuotedAmount, x.CurrencyId, x.Comment, x.Fob, x.ShippedVia, x.Terms, x.Accepted, x.CreatedDate, x.ModifiedDate);
    private static QuotationRequestResponse ToResponse(QuotationRequest x) => new(x.Id, x.FirstName, x.LastName, x.Email, x.TelephoneNumber, x.Country, x.CompanyName, x.TaxIdentification, x.Message, x.InternalComment, x.Done, x.CreatedDate, x.ModifiedDate);
    private static QuotationOrderLinkResponse ToResponse(QuotationOrderLink x) => new(x.Id, x.QuotationId, x.OrderId, x.CreatedDate, x.ModifiedDate);
    private static QuotationFileResponse ToResponse(QuotationFile x) => new(x.Id, x.QuotationId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate);
    private static QuotationRequestFileResponse ToResponse(QuotationRequestFile x) => new(x.Id, x.RequestId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate);
    private static IQueryable<QuotationResponse> Project(IQueryable<Quotation> q) => q.Select(x => new QuotationResponse(x.Id, x.CustomerId, x.EmployeeId, x.InvoiceId, x.Period, x.ExpirationDate, x.Subtotal, x.Vat, x.Total, x.WithholdingTax, x.QuotedAmount, x.CurrencyId, x.Comment, x.Fob, x.ShippedVia, x.Terms, x.Accepted, x.CreatedDate, x.ModifiedDate));
    private static IQueryable<QuotationOrderItemResponse> ProjectItems(IQueryable<QuotationOrderItem> q) => q.Select(x => new QuotationOrderItemResponse(x.Id, x.QuotationId, x.OrderId, x.Description, x.Quantity, x.UnitPrice, x.Subtotal, x.CreatedDate, x.ModifiedDate));
    private static IQueryable<QuotationOrderLinkResponse> ProjectLinks(IQueryable<QuotationOrderLink> q) => q.Select(x => new QuotationOrderLinkResponse(x.Id, x.QuotationId, x.OrderId, x.CreatedDate, x.ModifiedDate));
    private static IQueryable<QuotationFileResponse> ProjectFiles(IQueryable<QuotationFile> q) => q.Select(x => new QuotationFileResponse(x.Id, x.QuotationId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate));
    private static IQueryable<QuotationRequestResponse> ProjectRequests(IQueryable<QuotationRequest> q) => q.Select(x => new QuotationRequestResponse(x.Id, x.FirstName, x.LastName, x.Email, x.TelephoneNumber, x.Country, x.CompanyName, x.TaxIdentification, x.Message, x.InternalComment, x.Done, x.CreatedDate, x.ModifiedDate));
    private static IQueryable<QuotationRequestFileResponse> ProjectRequestFiles(IQueryable<QuotationRequestFile> q) => q.Select(x => new QuotationRequestFileResponse(x.Id, x.RequestId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate));
}
