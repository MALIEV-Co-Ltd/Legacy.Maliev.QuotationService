using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Analytics;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Legacy.Maliev.QuotationService.Tests.Controllers;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Analytics;

/// <summary>Source terminal/fallback ownership through production DI/client handlers and real PostgreSQL.</summary>
public sealed class QuotationTerminalLoggingPipelineTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Protected = "SYNTHETIC_GA_PRIVATE_27E4";

    [Theory]
    [InlineData(400, 1, 1)]
    [InlineData(401, 1, 1)]
    [InlineData(403, 1, 1)]
    [InlineData(500, 10, 1)]
    [InlineData(500, 1, 0)]
    [InlineData(429, 1, 0)]
    [InlineData(204, 1, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(-1, 10, 1)]
    [InlineData(-2, 1, 0)]
    [InlineData(-2, 10, 1)]
    [InlineData(-4, 1, 0)]
    [InlineData(-4, 10, 1)]
    public async Task Program_CallerOwnedFailure_EmitsExactSafeTerminalFieldsOnlyAfterPersistence(int status, int attempt, int records)
    {
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(status);
        var audit = Audit();
        await using var app = App(transport, audit, clock, nativeDeadline: status == -4);
        using var bootstrap = app.CreateClient();
        await Seed(clock, attempt - 1);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.All(transport.Ownership, value => Assert.True(value));
        var row = await Read();
        Assert.Equal(attempt, row.AttemptCount);
        Assert.Null(row.LeaseToken);
        Assert.Null(row.LeaseUntilUtc);
        Assert.Equal(records == 1, row.FailedUtc is not null);
        Assert.Equal(status == 204, row.SentUtc is not null);
        Assert.Empty(audit.Entries.Where(entry => entry.Event.Id == 5101));
        var terminal = audit.Entries.Where(entry => entry.Event.Id == 5201).ToArray();
        Assert.Equal(records, terminal.Length);
        if (records == 1) AssertTerminal(Assert.Single(terminal), status, attempt);
        else if (status != 204) Assert.True(row.NextAttemptUtc > Storage(clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(-1, 503)]
    [InlineData(-2, 504)]
    [InlineData(-4, 504)]
    public async Task Program_ErrorDisabledCaller_PreservesOneDependencyFallbackWithoutDuplicateTerminal(int status, int expectedStatus)
    {
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(status);
        var audit = Audit();
        await using var app = App(transport, audit, clock, callerLogging: false, maxAttempts: 1, nativeDeadline: status == -4);
        using var bootstrap = app.CreateClient();
        await Seed(clock);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.All(transport.Ownership, value => Assert.False(value));
        Assert.NotNull((await Read()).FailedUtc);
        Assert.Empty(audit.Entries.Where(entry => entry.Event.Id == 5201));
        AssertDependency(Assert.Single(audit.Entries), expectedStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Program_CallerCancellation_IsQuietForBothLoggingOwnersAndRetainsLease(bool callerLogging)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(-3, cancellation);
        var audit = Audit();
        await using var app = App(transport, audit, clock, callerLogging: callerLogging);
        using var bootstrap = app.CreateClient();
        await Seed(clock);
        using var scope = app.Services.CreateScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(cancellation.Token));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(callerLogging, Assert.Single(transport.Ownership));
        Assert.Empty(audit.Entries);
        var row = await Read();
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.LeaseToken);
        Assert.NotNull(row.LeaseUntilUtc);
        Assert.Null(row.FailedUtc);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task Program_RepeatedDurableRetries_ProduceOneErrorOnlyAtExhaustion()
    {
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(500);
        var audit = Audit();
        await using var app = App(transport, audit, clock, maxAttempts: 3);
        using var bootstrap = app.CreateClient();
        await Seed(clock);
        using var scope = app.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(1, await processor.DeliverBatchAsync(CancellationToken.None));
            var row = await Read();
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(attempt == 3, row.FailedUtc is not null);
            Assert.Equal(attempt == 3 ? 1 : 0, audit.Entries.Count);
            clock.Advance(TimeSpan.FromMinutes(20));
        }
        Assert.Equal(3, transport.Calls);
        AssertTerminal(Assert.Single(audit.Entries), 500, 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Program_FailedPersistence_DoesNotEmit5201OrReplayDelivery(bool callerLogging)
    {
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(500);
        var audit = Audit();
        var fault = new FailedPersistence();
        await using var app = App(transport, audit, clock, callerLogging: callerLogging, maxAttempts: 1, fault: fault);
        using var bootstrap = app.CreateClient();
        await Seed(clock);
        using var scope = app.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, fault.FailedUpdates);
        Assert.Empty(audit.Entries.Where(entry => entry.Event.Id == 5201));
        Assert.Equal(callerLogging ? 0 : 1, audit.Entries.Count);
        var row = await Read();
        Assert.Equal(1, row.AttemptCount);
        Assert.Null(row.FailedUtc);
        Assert.Null(row.LastError);
        Assert.NotNull(row.LeaseToken);
    }

    [Fact]
    public async Task Program_FallbackLoggerFailure_CannotReplaceProviderOutcomeOrPreventPersistence()
    {
        var clock = new FakeTimeProvider(Start);
        var transport = new Transport(500);
        var audit = Audit();
        audit.ThrowDependency = true;
        await using var app = App(transport, audit, clock, callerLogging: false, maxAttempts: 1);
        using var bootstrap = app.CreateClient();
        await Seed(clock);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.NotNull((await Read()).FailedUtc);
        AssertDependency(Assert.Single(audit.Entries), 500);
    }

    private StructuredAudit Audit() => new(() =>
    {
        using var db = fixture.Context();
        return db.GoogleAnalyticsOutbox.AsNoTracking().Single().FailedUtc is not null;
    });

    private WebApplicationFactory<Program> App(Transport transport, StructuredAudit audit, FakeTimeProvider clock,
        bool callerLogging = true, int maxAttempts = 10, bool nativeDeadline = false, FailedPersistence? fault = null) =>
        fixture.App(new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.UseSetting("GoogleAnalyticsMeasurementProtocol:Enabled", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MALIEV_OBSERVABILITY_STANDBY"] = "true",
                ["GoogleAnalyticsMeasurementProtocol:Enabled"] = "true",
                ["GoogleAnalyticsMeasurementProtocol:MeasurementId"] = "G-" + Protected,
                ["GoogleAnalyticsMeasurementProtocol:ApiSecret"] = Protected,
                ["GoogleAnalyticsMeasurementProtocol:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(audit);
                logging.AddFilter((category, level) => callerLogging
                    || category != typeof(GoogleAnalyticsDeliveryProcessor).FullName || level != LogLevel.Error);
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                if (fault is not null) services.AddDbContext<QuotationDbContext>(options => options.AddInterceptors(fault));
                if (nativeDeadline) services.AddHttpClient<GoogleAnalyticsDeliveryProcessor>(client => client.Timeout = TimeSpan.FromMilliseconds(150));
                services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(
                    http => http.PrimaryHandler = transport));
            });
        });

    private async Task Seed(FakeTimeProvider clock, int completedAttempts = 0)
    {
        await using var db = fixture.Context();
        await db.GoogleAnalyticsOutbox.ExecuteDeleteAsync();
        db.GoogleAnalyticsOutbox.Add(new GoogleAnalyticsOutbox
        {
            QuotationId = 27,
            EventKey = "quotation-27:close_convert_lead:v1",
            EventName = "close_convert_lead",
            ClientId = "123.456",
            SessionId = "789",
            UserId = "opaque-27",
            Currency = "THB",
            Value = 104m,
            OccurredUtc = Storage(clock.GetUtcNow()),
            NextAttemptUtc = Storage(clock.GetUtcNow()),
            AttemptCount = completedAttempts,
        });
        await db.SaveChangesAsync();
    }

    private async Task<GoogleAnalyticsOutbox> Read()
    {
        await using var db = fixture.Context();
        return await db.GoogleAnalyticsOutbox.AsNoTracking().SingleAsync();
    }

    private static DateTime Storage(DateTimeOffset time) => DateTime.SpecifyKind(time.UtcDateTime, DateTimeKind.Unspecified);

    private static void AssertTerminal(Record record, int status, int attempts)
    {
        Assert.Equal(5201, record.Event.Id);
        Assert.Equal("GoogleAnalyticsDeliveryFailed", record.Event.Name);
        Assert.True(record.PersistedBeforeLog);
        AssertSafe(record);
        Assert.Equal(new[] { "AttemptCount", "Dependency", "EventName", "ExceptionType", "Operation", "StatusCode" }, record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("GoogleAnalyticsDeliveryFailed", record.Fields["EventName"]);
        Assert.Equal("GoogleAnalytics", record.Fields["Dependency"]);
        Assert.Equal("GA4Delivery", record.Fields["Operation"]);
        Assert.Equal(attempts, record.Fields["AttemptCount"]);
        Assert.Equal(status == -1 ? 503 : status is -2 or -4 ? 504 : status, record.Fields["StatusCode"]);
        Assert.Equal(status == -1 ? typeof(HttpRequestException).FullName
            : status is -2 or -4 ? typeof(TaskCanceledException).FullName : null, record.Fields["ExceptionType"]);
    }

    private static void AssertDependency(Record record, int status)
    {
        Assert.Equal(5101, record.Event.Id);
        Assert.Equal("DependencyRequestFailure", record.Event.Name);
        AssertSafe(record);
        Assert.Equal(new[] { "Dependency", "EventName", "Operation", "StatusCode" }, record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("DependencyRequestFailure", record.Fields["EventName"]);
        Assert.Equal("GoogleAnalytics", record.Fields["Dependency"]);
        Assert.Equal("GA4Delivery", record.Fields["Operation"]);
        Assert.Equal(status, record.Fields["StatusCode"]);
    }

    private static void AssertSafe(Record record)
    {
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Null(record.Exception);
        using var json = JsonDocument.Parse(record.Json);
        Assert.Equal("ERROR", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Exception").ValueKind);
        var state = json.RootElement.GetProperty("State");
        Assert.Equal(record.Fields.Keys.Order(StringComparer.Ordinal), state.EnumerateObject()
            .Where(property => property.Name != "{OriginalFormat}").Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var pair in record.Fields) Assert.Equal(JsonSerializer.Serialize(pair.Value), state.GetProperty(pair.Key).GetRawText());
        foreach (var forbidden in new[] { Protected, "quotation-27", "123.456", "opaque-27", "measurement_id", "api_secret", "google-analytics.com" })
            Assert.DoesNotContain(forbidden, record.Json, StringComparison.Ordinal);
    }

    private sealed class Transport(int status, CancellationTokenSource? caller = null) : HttpMessageHandler
    {
        public int Calls;
        public ConcurrentQueue<bool> Ownership { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("Maliev.DependencyFailureOwnedByCaller"), out var owned));
            Ownership.Enqueue(owned);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("www.google-analytics.com", request.RequestUri?.Host);
            if (status == -1) throw new HttpRequestException(Protected);
            if (status == -2) throw new TaskCanceledException(Protected);
            if (status == -3) caller!.Cancel();
            if (status is -3 or -4) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new((HttpStatusCode)status) { Content = new StringContent(Protected) };
        }
    }

    private sealed class FailedPersistence : DbCommandInterceptor
    {
        public int FailedUpdates;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"FailedUtc\" =", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref FailedUpdates);
                throw new InvalidOperationException(Protected);
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record Record(EventId Event, LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields,
        string Json, bool? PersistedBeforeLog);

    private sealed class StructuredAudit(Func<bool> persisted) : ILoggerProvider
    {
        public ConcurrentQueue<Record> Entries { get; } = new();
        public bool ThrowDependency;
        public bool Persisted => persisted();
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }
        private sealed class Recorder(StructuredAudit owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> format)
            {
                if (eventId.Id is not (5101 or 5201)) return;
                var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).Where(pair => pair.Key != "{OriginalFormat}")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, format);
                new MalievCloudJsonConsoleFormatter(new FormatterOptions()).Write(in entry, null, writer);
                owner.Entries.Enqueue(new(eventId, level, exception, fields, writer.ToString(), eventId.Id == 5201 ? owner.Persisted : null));
                if (eventId.Id == 5101 && owner.ThrowDependency) throw new InvalidOperationException(Protected);
            }
        }
    }

    private sealed class FormatterOptions : IOptionsMonitor<JsonConsoleFormatterOptions>
    {
        public JsonConsoleFormatterOptions CurrentValue { get; } = new() { UseUtcTimestamp = true };
        public JsonConsoleFormatterOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<JsonConsoleFormatterOptions, string?> listener) => null;
    }
}
