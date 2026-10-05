using System.Collections.Concurrent;
using System.Net;
using Legacy.Maliev.QuotationService.Api.Analytics;
using Legacy.Maliev.QuotationService.Domain;
using Legacy.Maliev.QuotationService.Tests.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Analytics;

/// <summary>Real hosted worker scheduling and durable delivery, with only external transport controlled.</summary>
public sealed class QuotationAnalyticsHostedWorkerTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(204, false)]
    [InlineData(400, true)]
    [InlineData(500, true)]
    public async Task Program_HostedWorker_DrainsMultipleBatchesAndDoesNotReplayCompletedRows(int status, bool failed)
    {
        await Seed(2);
        var transport = new Transport(status);
        var audit = new Audit();
        await using var app = App(transport, audit);
        using var bootstrap = app.CreateClient();
        Assert.Single(app.Services.GetServices<IHostedService>(), worker => worker is GoogleAnalyticsOutboxWorker);
        await Until(async () => (await Read()).All(row => row.SentUtc is not null || row.FailedUtc is not null));
        var rows = await Read();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal(failed, row.FailedUtc is not null);
            Assert.Equal(!failed, row.SentUtc is not null);
            Assert.Null(row.LeaseToken);
            Assert.Null(row.LeaseUntilUtc);
        });
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.Equal(2, transport.Calls);
        Assert.Equal(failed ? 2 : 0, audit.Events.Count(id => id == 5201));
        Assert.DoesNotContain(5101, audit.Events);
        Assert.Empty(audit.WorkerErrors);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task Program_StandbyOrDisabledDelivery_LeavesDueIntentUnclaimed(bool enabled, bool standby, bool registered)
    {
        await Seed(1);
        var transport = new Transport(204);
        var audit = new Audit();
        await using var app = App(transport, audit, enabled, standby);
        using var bootstrap = app.CreateClient();
        Assert.Equal(registered, app.Services.GetServices<IHostedService>().Any(worker => worker is GoogleAnalyticsOutboxWorker));
        await Assert.ThrowsAsync<TimeoutException>(() => transport.Entered.Task.WaitAsync(TimeSpan.FromMilliseconds(1500)));
        var row = Assert.Single(await Read());
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LeaseToken);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Null(row.SentUtc);
        Assert.Null(row.FailedUtc);
        Assert.Equal(0, transport.Calls);
        Assert.Empty(audit.Events);
        Assert.Empty(audit.WorkerErrors);
    }

    [Fact]
    public async Task Program_HostShutdown_CancelsProviderQuietlyAndRetainsUnacknowledgedLease()
    {
        await Seed(1);
        var transport = new Transport(-1);
        var audit = new Audit();
        await using (var app = App(transport, audit))
        {
            using var bootstrap = app.CreateClient();
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var claimed = Assert.Single(await Read());
            Assert.Equal(1, claimed.AttemptCount);
            Assert.NotNull(claimed.LeaseToken);
            Assert.NotNull(claimed.LeaseUntilUtc);
        }
        await transport.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = Assert.Single(await Read());
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.LeaseToken);
        Assert.NotNull(row.LeaseUntilUtc);
        Assert.Null(row.SentUtc);
        Assert.Null(row.FailedUtc);
        Assert.Null(row.LastError);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(audit.Events);
        Assert.Empty(audit.WorkerErrors);
    }

    private WebApplicationFactory<Program> App(Transport transport, Audit audit, bool enabled = true, bool standby = false)
        => fixture.App(new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", standby ? "true" : "false");
            builder.UseSetting("GoogleAnalyticsMeasurementProtocol:Enabled", enabled ? "true" : "false");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MALIEV_OBSERVABILITY_STANDBY"] = standby ? "true" : "false",
                ["GoogleAnalyticsMeasurementProtocol:Enabled"] = enabled ? "true" : "false",
                ["GoogleAnalyticsMeasurementProtocol:MeasurementId"] = "G-SYNTHETIC_WORKER",
                ["GoogleAnalyticsMeasurementProtocol:ApiSecret"] = "SYNTHETIC_WORKER_ONLY",
                ["GoogleAnalyticsMeasurementProtocol:BatchSize"] = "1",
                ["GoogleAnalyticsMeasurementProtocol:MaxAttempts"] = "1",
                ["GoogleAnalyticsMeasurementProtocol:PollSeconds"] = "1",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(audit));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
                services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(
                    http => http.PrimaryHandler = transport));
            });
        });

    private async Task Seed(int count)
    {
        await using var db = fixture.Context();
        await db.GoogleAnalyticsOutbox.ExecuteDeleteAsync();
        for (var index = 0; index < count; index++)
            db.GoogleAnalyticsOutbox.Add(new GoogleAnalyticsOutbox
            {
                QuotationId = 114 + index,
                EventKey = $"quotation-{114 + index}:close_convert_lead:v1",
                EventName = "close_convert_lead",
                ClientId = "123.456",
                SessionId = "789",
                Currency = "THB",
                Value = 104m,
                OccurredUtc = DateTime.SpecifyKind(Start.UtcDateTime, DateTimeKind.Unspecified),
                NextAttemptUtc = DateTime.SpecifyKind(Start.UtcDateTime, DateTimeKind.Unspecified),
            });
        await db.SaveChangesAsync();
    }

    private async Task<GoogleAnalyticsOutbox[]> Read()
    {
        await using var db = fixture.Context();
        return await db.GoogleAnalyticsOutbox.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Transport(int status) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            if (status == -1)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            }
            return new HttpResponseMessage((HttpStatusCode)status);
        }
    }

    private sealed class Audit : ILoggerProvider
    {
        public ConcurrentQueue<int> Events { get; } = new();
        public ConcurrentQueue<EventId> WorkerErrors { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Observer(this, categoryName);
        public void Dispose() { }
        private sealed class Observer(Audit audit, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id is 5101 or 5201) audit.Events.Enqueue(eventId.Id);
                if (category == typeof(GoogleAnalyticsOutboxWorker).FullName && logLevel == LogLevel.Error)
                    audit.WorkerErrors.Enqueue(eventId);
            }
        }
    }
}
