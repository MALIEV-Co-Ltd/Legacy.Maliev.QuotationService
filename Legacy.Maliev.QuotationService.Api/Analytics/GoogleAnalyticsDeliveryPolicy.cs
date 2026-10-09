using System.Net;

namespace Legacy.Maliev.QuotationService.Api.Analytics;

/// <summary>Preserves the source provider classification and bounded exponential delay.</summary>
internal static class GoogleAnalyticsDeliveryPolicy
{
    internal static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    internal static TimeSpan CalculateRetryDelay(int attemptCount, double jitterFraction)
    {
        var exponent = Math.Clamp(attemptCount, 1, 9);
        var jitter = Math.Clamp(jitterFraction, 0, 1);
        return TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, exponent) * (1 + jitter)));
    }
}
