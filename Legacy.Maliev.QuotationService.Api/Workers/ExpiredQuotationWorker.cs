using Legacy.Maliev.QuotationService.Application.Interfaces;

namespace Legacy.Maliev.QuotationService.Api.Workers;

public sealed class ExpiredQuotationWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ExpiredQuotationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(4), timeProvider);
        do
        {
            try { await using var scope = scopeFactory.CreateAsyncScope(); var changed = await scope.ServiceProvider.GetRequiredService<IQuotationService>().DeclineExpiredAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken); if (changed > 0) logger.LogInformation("Declined {QuotationCount} expired quotations", changed); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Failed to decline expired quotations"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
