using System.Globalization;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Data;

public sealed partial class QuotationRepository : IQuotationInvoiceCompletionStore
{
    public Task<QuotationDecisionPersistenceResult> ApplyAsync(QuotationInvoiceCompletionContext authority, CancellationToken cancellationToken)
    {
        if (authority.OperationId == Guid.Empty || authority.QuotationId <= 0 || authority.InvoiceId <= 0
            || !DateTime.TryParseExact(authority.OriginalQuotationVersion, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var version) || WireTime(version) != authority.OriginalQuotationVersion)
            throw new ArgumentException("Invalid operation admission.", nameof(authority));
        return ApplyDecisionCoreAsync(authority.QuotationId, true, QuotationAcceptanceOrigin.Employee,
            new DateTimeOffset(version), cancellationToken, authority.InvoiceId, null, authority);
    }

    public async Task<QuotationInvoiceCompletionProgress?> ReadAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var row = await quotations.InvoiceCompletionOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
        if (row is null) return null;
        var authority = JsonSerializer.Deserialize<QuotationInvoiceCompletionContext>(row.AuthorityJson)
            ?? throw new InvalidDataException("Incomplete invoice operation authority.");
        var orders = JsonSerializer.Deserialize<int[]>(row.OrderIdsJson)
            ?? throw new InvalidDataException("Incomplete invoice order membership.");
        var completed = JsonSerializer.Deserialize<int[]>(row.CompletedOrderIdsJson)
            ?? throw new InvalidDataException("Incomplete invoice order progress.");
        if (row.AuthorityJson != JsonSerializer.Serialize(authority)
            || authority.RequesterSubject != "service:legacy-intranet" || authority.ExecutorSubject != "service:legacy-accounting"
            || authority.FinancialBindingVersion != "invoice-creation-financial-v1"
            || authority.QuotationId <= 0 || authority.InvoiceId <= 0
            || string.IsNullOrWhiteSpace(authority.OriginIssuer) || string.IsNullOrWhiteSpace(authority.EmployeeSubject)
            || authority.EmployeeSubject.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
            || authority.FinancialBinding is not { Length: 64 }
            || authority.FinancialBinding.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || !DateTime.TryParseExact(authority.OriginalQuotationVersion, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var originalVersion)
            || WireTime(originalVersion) != authority.OriginalQuotationVersion
            || authority.OperationId != row.OperationId || authority.QuotationId != row.QuotationId
            || authority.InvoiceId != row.InvoiceId || orders.Any(x => x <= 0)
            || orders.Distinct().Count() != orders.Length || completed.Distinct().Count() != completed.Length
            || completed.Except(orders).Any()
            || row.State is not ("QuotationCommitted" or "OrdersPartial" or "OrdersConflict" or "Completed")
            || row.State == "QuotationCommitted" && completed.Length != 0
            || row.State == "Completed" && (completed.Length != orders.Length || row.ClaimId is not null))
            throw new InvalidDataException("Invalid invoice operation progress.");
        return new(new(1, operationId.ToString("D"), row.QuotationId, row.InvoiceId, authority.OriginIssuer,
            authority.EmployeeSubject, authority.RequesterSubject, authority.ExecutorSubject,
            authority.OriginalQuotationVersion, authority.FinancialBinding, authority.FinancialBindingVersion,
            row.State, WireTime(row.DecisionOrderVersion), completed.Length, orders.Length, WireTime(row.ModifiedDate)),
            orders, completed);
    }

    public async Task<bool> ClaimAsync(Guid operationId, Guid claimId, CancellationToken cancellationToken)
    {
        var now = Now();
        return await quotations.InvoiceCompletionOperations.Where(x => x.OperationId == operationId
                && x.State != "Completed" && (x.ClaimId == null || x.ClaimUntil <= now))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ClaimId, (Guid?)claimId)
                .SetProperty(x => x.ClaimUntil, (DateTime?)now.AddMinutes(2)), cancellationToken) == 1;
    }

    public async Task ReleaseAsync(Guid operationId, Guid claimId, CancellationToken cancellationToken)
    {
        await quotations.InvoiceCompletionOperations.Where(x => x.OperationId == operationId && x.ClaimId == claimId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ClaimId, (Guid?)null)
                .SetProperty(x => x.ClaimUntil, (DateTime?)null), cancellationToken);
    }

    public async Task<bool> CheckpointAsync(Guid operationId, Guid claimId, IReadOnlyList<int> completedOrders,
        string state, CancellationToken cancellationToken)
    {
        if (state is not ("OrdersPartial" or "OrdersConflict" or "Completed"))
            throw new ArgumentException("Invalid operation checkpoint.", nameof(state));
        var progress = await ReadAsync(operationId, cancellationToken);
        if (progress is null || completedOrders.Distinct().Count() != completedOrders.Count
            || completedOrders.Except(progress.OrderIds).Any() || progress.CompletedOrderIds.Except(completedOrders).Any()
            || state == "Completed" && completedOrders.Count != progress.OrderIds.Count) return false;
        var previousIds = JsonSerializer.Serialize(progress.CompletedOrderIds.OrderBy(x => x).ToArray());
        var ids = JsonSerializer.Serialize(completedOrders.OrderBy(x => x).ToArray());
        var now = Now();
        var release = state is "Completed" or "OrdersConflict";
        return await quotations.InvoiceCompletionOperations.Where(x => x.OperationId == operationId
                && x.ClaimId == claimId && x.ClaimUntil > now && x.State != "Completed"
                && x.CompletedOrderIdsJson == previousIds)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.CompletedOrderIdsJson, ids)
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.ClaimId, release ? null : (Guid?)claimId)
                .SetProperty(x => x.ClaimUntil, release ? null : (DateTime?)now.AddMinutes(2)), cancellationToken) == 1;
    }

    internal static bool Matches(QuotationInvoiceCompletionReceipt receipt, QuotationInvoiceCompletionContext authority)
        => receipt.OperationId == authority.OperationId.ToString("D") && receipt.QuotationId == authority.QuotationId
            && receipt.InvoiceId == authority.InvoiceId && receipt.OriginIssuer == authority.OriginIssuer
            && receipt.EmployeeSubject == authority.EmployeeSubject && receipt.RequesterSubject == authority.RequesterSubject
            && receipt.ExecutorSubject == authority.ExecutorSubject
            && receipt.OriginalQuotationVersion == authority.OriginalQuotationVersion
            && receipt.FinancialBinding == authority.FinancialBinding
            && receipt.FinancialBindingVersion == authority.FinancialBindingVersion;

    private static string WireTime(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
