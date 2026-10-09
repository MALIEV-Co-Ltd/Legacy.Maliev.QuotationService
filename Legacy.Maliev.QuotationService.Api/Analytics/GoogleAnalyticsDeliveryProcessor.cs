using System.Text;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.QuotationService.Api.Analytics;

public sealed class GoogleAnalyticsDeliveryProcessor(
    HttpClient httpClient, IGoogleAnalyticsOutboxStore store,
    IOptions<GoogleAnalyticsMeasurementProtocolOptions> configuration,
    TimeProvider clock, ILogger<GoogleAnalyticsDeliveryProcessor> logger, Func<double>? jitter = null)
{
    private readonly GoogleAnalyticsMeasurementProtocolOptions options = configuration.Value;

    public async Task<int> DeliverBatchAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return 0;
        var rows = await store.ClaimAsync(Now(), TimeSpan.FromSeconds(options.LeaseSeconds), options.BatchSize, cancellationToken);
        foreach (var row in rows) await DeliverAsync(row, cancellationToken);
        return rows.Count;
    }

    private async Task DeliverAsync(GoogleAnalyticsOutbox row, CancellationToken cancellationToken)
    {
        var token = row.LeaseToken!.Value;
        try
        {
            var endpoint = $"https://www.google-analytics.com/mp/collect?measurement_id={Uri.EscapeDataString(options.MeasurementId)}&api_secret={Uri.EscapeDataString(options.ApiSecret)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(GoogleAnalyticsPayloadFactory.Build(row), Encoding.UTF8, "application/json"),
            };
            request.Options.Set(GoogleAnalyticsDependencyFailureHandler.CallerOwnsFailure, logger.IsEnabled(LogLevel.Error));
            request.Options.Set(GoogleAnalyticsDependencyFailureHandler.CallerCancellation, cancellationToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await store.MarkSentAsync(row.Id, token, Now(), cancellationToken);
                return;
            }
            var transient = GoogleAnalyticsDeliveryPolicy.IsTransient(response.StatusCode);
            await FailAsync(row, token, transient, $"HTTP {(int)response.StatusCode}", (int)response.StatusCode, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            await FailAsync(row, token, true, error.GetType().Name,
                error is TaskCanceledException ? 504 : 503, error.GetType().FullName, cancellationToken);
        }
    }

    private async Task FailAsync(GoogleAnalyticsOutbox row, Guid token, bool transient,
        string diagnostic, int status, string? exceptionType, CancellationToken cancellationToken)
    {
        var now = Now();
        if (transient && row.AttemptCount < options.MaxAttempts)
        {
            var delay = GoogleAnalyticsDeliveryPolicy.CalculateRetryDelay(row.AttemptCount, (jitter ?? Random.Shared.NextDouble)());
            await store.MarkRetryAsync(row.Id, token, now.Add(delay), diagnostic, cancellationToken);
            return;
        }
        await store.MarkFailedAsync(row.Id, token, now, diagnostic, cancellationToken);
        logger.LogError(new EventId(5201, "GoogleAnalyticsDeliveryFailed"),
            "{EventName} Operation={Operation} Dependency={Dependency} StatusCode={StatusCode} ExceptionType={ExceptionType} AttemptCount={AttemptCount}",
            "GoogleAnalyticsDeliveryFailed", "GA4Delivery", "GoogleAnalytics", status, exceptionType, row.AttemptCount);
    }

    private DateTime Now() => DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
