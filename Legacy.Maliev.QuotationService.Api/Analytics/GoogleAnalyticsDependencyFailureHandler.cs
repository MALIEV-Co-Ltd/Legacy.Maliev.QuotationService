namespace Legacy.Maliev.QuotationService.Api.Analytics;

/// <summary>GA-only dependency fallback when the processor cannot own failure logging.</summary>
internal sealed class GoogleAnalyticsDependencyFailureHandler(ILogger<GoogleAnalyticsDependencyFailureHandler> logger)
    : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<bool> CallerOwnsFailure = new("Maliev.DependencyFailureOwnedByCaller");
    internal static readonly HttpRequestOptionsKey<CancellationToken> CallerCancellation = new("Quotation.GA4CallerCancellation");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 500 and <= 599) Record(request, (int)response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) when (CallerCanceled(request, cancellationToken)) { throw; }
        catch (HttpRequestException)
        {
            Record(request, 503);
            throw;
        }
        catch (OperationCanceledException)
        {
            Record(request, 504);
            throw;
        }
    }

    private static bool CallerCanceled(HttpRequestMessage request, CancellationToken handlerToken) =>
        request.Options.TryGetValue(CallerCancellation, out var callerToken)
            ? callerToken.IsCancellationRequested : handlerToken.IsCancellationRequested;

    private void Record(HttpRequestMessage request, int status)
    {
        if (request.Options.TryGetValue(CallerOwnsFailure, out var owned) && owned) return;
        try
        {
            logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode}",
                "DependencyRequestFailure", "GoogleAnalytics", "GA4Delivery", status);
        }
        catch (Exception)
        {
            // Observation failure must not replace the provider outcome or disclose logging-provider details.
        }
    }
}
