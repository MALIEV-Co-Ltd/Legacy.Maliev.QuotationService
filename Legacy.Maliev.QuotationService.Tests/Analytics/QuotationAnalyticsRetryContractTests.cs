using System.Net;
using System.Net.Http.Headers;
using Legacy.Maliev.QuotationService.Api.Analytics;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.QuotationService.Tests.Analytics;

/// <summary>Exact source delay inputs and production processor transitions without external I/O.</summary>
public sealed class QuotationAnalyticsRetryContractTests
{
    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(1, 0.5, 3)]
    [InlineData(99, 1, 900)]
    [InlineData(-1, -1, 2)]
    [InlineData(2, 2, 8)]
    [InlineData(9, 0, 512)]
    [InlineData(9, 0.5, 768)]
    [InlineData(9, 1, 900)]
    public async Task Retry_ExactSourceInputsReachProductionStoreDespiteRetryAfter(int attempt, double jitter, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), GoogleAnalyticsDeliveryPolicy.CalculateRetryDelay(attempt, jitter));
        var store = new RecordingStore(attempt);
        using var client = new HttpClient(new ResponseHandler(429));
        var processor = Processor(client, store, maxAttempts: 100, jitter: jitter);

        Assert.Equal(1, await processor.DeliverBatchAsync(CancellationToken.None));

        Assert.Equal("retry", store.Outcome);
        Assert.Equal(Clock.Utc.UtcDateTime.AddSeconds(seconds), store.RecordedUtc);
        Assert.Equal("HTTP 429", store.Diagnostic);
        Assert.Equal(1, store.Writes);
        Assert.Equal(store.Row.Id, store.RecordedId);
        Assert.Equal(store.Row.LeaseToken, store.RecordedLease);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(600, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public void Classification_PreservesExactHistoricalStatusCases(int status, bool transient) =>
        Assert.Equal(transient, GoogleAnalyticsDeliveryPolicy.IsTransient((HttpStatusCode)status));

    [Theory]
    [InlineData(204, 1, 10, "sent", null)]
    [InlineData(408, 1, 10, "retry", "HTTP 408")]
    [InlineData(429, 1, 10, "retry", "HTTP 429")]
    [InlineData(500, 1, 10, "retry", "HTTP 500")]
    [InlineData(502, 1, 10, "retry", "HTTP 502")]
    [InlineData(600, 1, 10, "retry", "HTTP 600")]
    [InlineData(400, 1, 10, "failed", "HTTP 400")]
    [InlineData(401, 1, 10, "failed", "HTTP 401")]
    [InlineData(403, 1, 10, "failed", "HTTP 403")]
    [InlineData(429, 10, 10, "failed", "HTTP 429")]
    [InlineData(503, 11, 10, "failed", "HTTP 503")]
    [InlineData(-1, 1, 10, "retry", nameof(HttpRequestException))]
    [InlineData(-2, 1, 10, "retry", nameof(TaskCanceledException))]
    [InlineData(-1, 10, 10, "failed", nameof(HttpRequestException))]
    [InlineData(-2, 10, 10, "failed", nameof(TaskCanceledException))]
    public async Task Processor_UsesOneDurableTransitionAtExactAttemptBoundary(
        int status, int attempt, int maxAttempts, string outcome, string? diagnostic)
    {
        var store = new RecordingStore(attempt);
        using var client = new HttpClient(new ResponseHandler(status));
        var processor = Processor(client, store, maxAttempts, jitter: 0.5);

        Assert.Equal(1, await processor.DeliverBatchAsync(CancellationToken.None));

        Assert.Equal(outcome, store.Outcome);
        Assert.Equal(diagnostic, store.Diagnostic);
        Assert.Equal(Clock.Utc.UtcDateTime.AddSeconds(outcome == "retry" ? 3 : 0), store.RecordedUtc);
        Assert.Equal(1, store.Writes);
        Assert.Equal(store.Row.Id, store.RecordedId);
        Assert.Equal(store.Row.LeaseToken, store.RecordedLease);
    }

    [Fact]
    public async Task Disabled_delivery_does_not_claim_or_send()
    {
        var store = new RecordingStore(1);
        using var handler = new ResponseHandler(204);
        using var client = new HttpClient(handler);
        var processor = Processor(client, store, maxAttempts: 10, jitter: 0.5, enabled: false);

        Assert.Equal(0, await processor.DeliverBatchAsync(CancellationToken.None));

        Assert.Equal(0, store.Claims);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-4)]
    public async Task Caller_cancellation_propagates_without_a_durable_outcome(int status)
    {
        var store = new RecordingStore(1);
        using var cancellation = new CancellationTokenSource();
        using var handler = new ResponseHandler(status, cancellation);
        using var client = new HttpClient(handler);
        var processor = Processor(client, store, maxAttempts: 10, jitter: 0.5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.DeliverBatchAsync(cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, store.Claims);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, store.Writes);
        Assert.Null(store.Outcome);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(400)]
    [InlineData(429)]
    public async Task Completed_or_exhausted_delivery_does_not_sample_retry_jitter(int status)
    {
        var store = new RecordingStore(10);
        using var client = new HttpClient(new ResponseHandler(status));
        var processor = new GoogleAnalyticsDeliveryProcessor(client, store,
            Options.Create(new GoogleAnalyticsMeasurementProtocolOptions { Enabled = true, MaxAttempts = 10 }),
            new Clock(), NullLogger<GoogleAnalyticsDeliveryProcessor>.Instance,
            () => throw new InvalidOperationException("A terminal outcome must not sample retry jitter."));

        Assert.Equal(1, await processor.DeliverBatchAsync(CancellationToken.None));

        Assert.Equal(status == 204 ? "sent" : "failed", store.Outcome);
        Assert.Equal(1, store.Writes);
        Assert.Equal(store.Row.Id, store.RecordedId);
        Assert.Equal(store.Row.LeaseToken, store.RecordedLease);
    }

    private static GoogleAnalyticsDeliveryProcessor Processor(HttpClient client, RecordingStore store, int maxAttempts, double jitter, bool enabled = true) =>
        new(client, store, Options.Create(new GoogleAnalyticsMeasurementProtocolOptions
        {
            Enabled = enabled,
            MeasurementId = "test-measurement",
            ApiSecret = "test-only-secret",
            MaxAttempts = maxAttempts,
        }), new Clock(), NullLogger<GoogleAnalyticsDeliveryProcessor>.Instance, () => jitter);

    private sealed class Clock : TimeProvider
    {
        internal static readonly DateTimeOffset Utc = new(2026, 10, 1, 2, 3, 4, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class ResponseHandler(int status, CancellationTokenSource? caller = null) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (status == -1) throw new HttpRequestException("test-only transport failure");
            if (status == -2) throw new TaskCanceledException("test-only provider timeout");
            if (status is -3 or -4)
            {
                caller!.Cancel();
                if (status == -3) throw new TaskCanceledException("test-only caller cancellation", null, cancellationToken);
                throw new OperationCanceledException(cancellationToken);
            }
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1));
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingStore(int attempt) : IGoogleAnalyticsOutboxStore
    {
        internal GoogleAnalyticsOutbox Row { get; } = new()
        {
            Id = 7,
            QuotationId = 17,
            LeaseToken = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            AttemptCount = attempt,
            EventKey = "quotation-17:close_convert_lead:v1",
            EventName = "close_convert_lead",
            ClientId = "123.456",
            SessionId = "789",
            Currency = "THB",
            OccurredUtc = Clock.Utc.UtcDateTime,
        };
        internal string? Outcome { get; private set; }
        internal string? Diagnostic { get; private set; }
        internal DateTime RecordedUtc { get; private set; }
        internal long RecordedId { get; private set; }
        internal Guid RecordedLease { get; private set; }
        internal int Writes { get; private set; }
        internal int Claims { get; private set; }
        public Task<IReadOnlyList<GoogleAnalyticsOutbox>> ClaimAsync(DateTime nowUtc, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken)
        {
            Claims++;
            return Task.FromResult<IReadOnlyList<GoogleAnalyticsOutbox>>([Row]);
        }
        public Task MarkSentAsync(long id, Guid leaseToken, DateTime sentUtc, CancellationToken cancellationToken) =>
            Record("sent", id, leaseToken, sentUtc, null);
        public Task MarkRetryAsync(long id, Guid leaseToken, DateTime nextAttemptUtc, string error, CancellationToken cancellationToken) =>
            Record("retry", id, leaseToken, nextAttemptUtc, error);
        public Task MarkFailedAsync(long id, Guid leaseToken, DateTime failedUtc, string error, CancellationToken cancellationToken) =>
            Record("failed", id, leaseToken, failedUtc, error);
        private Task Record(string outcome, long id, Guid lease, DateTime utc, string? diagnostic)
        {
            Writes++;
            Outcome = outcome;
            RecordedId = id;
            RecordedLease = lease;
            RecordedUtc = utc;
            Diagnostic = diagnostic;
            return Task.CompletedTask;
        }
    }
}
