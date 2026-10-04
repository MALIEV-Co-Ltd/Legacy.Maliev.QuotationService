using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Api.Analytics;

public interface IGoogleAnalyticsOutboxStore
{
    Task<IReadOnlyList<GoogleAnalyticsOutbox>> ClaimAsync(DateTime nowUtc, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken);
    Task MarkSentAsync(long id, Guid leaseToken, DateTime sentUtc, CancellationToken cancellationToken);
    Task MarkRetryAsync(long id, Guid leaseToken, DateTime nextAttemptUtc, string error, CancellationToken cancellationToken);
    Task MarkFailedAsync(long id, Guid leaseToken, DateTime failedUtc, string error, CancellationToken cancellationToken);
}

/// <summary>Conditional database claims; a reclaimed token fences every stale acknowledgement.</summary>
public sealed class GoogleAnalyticsOutboxStore(QuotationDbContext context) : IGoogleAnalyticsOutboxStore
{
    public async Task<IReadOnlyList<GoogleAnalyticsOutbox>> ClaimAsync(
        DateTime nowUtc, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken)
    {
        var now = StorageUtc(nowUtc);
        var candidates = await context.GoogleAnalyticsOutbox.AsNoTracking()
            .Where(row => row.SentUtc == null && row.FailedUtc == null && row.NextAttemptUtc <= now
                && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
            .OrderBy(row => row.NextAttemptUtc).ThenBy(row => row.Id)
            .Select(row => row.Id).Take(Math.Clamp(batchSize, 1, 100)).ToArrayAsync(cancellationToken);
        var token = Guid.NewGuid();
        var until = now.Add(leaseDuration);
        foreach (var id in candidates)
        {
            await context.GoogleAnalyticsOutbox
                .Where(row => row.Id == id && row.SentUtc == null && row.FailedUtc == null
                    && row.NextAttemptUtc <= now && (row.LeaseUntilUtc == null || row.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LeaseToken, (Guid?)token)
                    .SetProperty(row => row.LeaseUntilUtc, (DateTime?)until)
                    .SetProperty(row => row.AttemptCount, row => row.AttemptCount + 1), cancellationToken);
        }
        return await context.GoogleAnalyticsOutbox.AsNoTracking().Where(row => row.LeaseToken == token)
            .OrderBy(row => row.Id).ToListAsync(cancellationToken);
    }

    public async Task MarkSentAsync(long id, Guid leaseToken, DateTime sentUtc, CancellationToken cancellationToken)
    {
        var sent = StorageUtc(sentUtc);
        await context.GoogleAnalyticsOutbox
            .Where(row => row.Id == id && row.LeaseToken == leaseToken && row.SentUtc == null && row.FailedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SentUtc, (DateTime?)sent)
                .SetProperty(row => row.LeaseToken, (Guid?)null).SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(row => row.LastError, (string?)null), cancellationToken);
    }

    public async Task MarkRetryAsync(long id, Guid leaseToken, DateTime nextAttemptUtc, string error, CancellationToken cancellationToken)
    {
        var next = StorageUtc(nextAttemptUtc);
        var diagnostic = Bound(error);
        await context.GoogleAnalyticsOutbox
            .Where(row => row.Id == id && row.LeaseToken == leaseToken && row.SentUtc == null && row.FailedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NextAttemptUtc, next)
                .SetProperty(row => row.LeaseToken, (Guid?)null).SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(row => row.LastError, diagnostic), cancellationToken);
    }

    public async Task MarkFailedAsync(long id, Guid leaseToken, DateTime failedUtc, string error, CancellationToken cancellationToken)
    {
        var failed = StorageUtc(failedUtc);
        var diagnostic = Bound(error);
        await context.GoogleAnalyticsOutbox
            .Where(row => row.Id == id && row.LeaseToken == leaseToken && row.SentUtc == null && row.FailedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FailedUtc, (DateTime?)failed)
                .SetProperty(row => row.LeaseToken, (Guid?)null).SetProperty(row => row.LeaseUntilUtc, (DateTime?)null)
                .SetProperty(row => row.LastError, diagnostic), cancellationToken);
    }

    private static DateTime StorageUtc(DateTime value) => DateTime.SpecifyKind(
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Unspecified);
    private static string? Bound(string? value) => string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, 1024)];
}
