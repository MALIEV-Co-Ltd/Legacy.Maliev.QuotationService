using Microsoft.Extensions.Options;

namespace Legacy.Maliev.QuotationService.Api.Analytics;

public sealed class GoogleAnalyticsOutboxWorker(
    IServiceScopeFactory scopes, IOptions<GoogleAnalyticsMeasurementProtocolOptions> configuration,
    ILogger<GoogleAnalyticsOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                using var scope = scopes.CreateScope();
                processed = await scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>()
                    .DeliverBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "GA4 outbox delivery pass failed."); }
            if (processed == 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(configuration.Value.PollSeconds, 1, 300)), stoppingToken);
        }
    }
}
