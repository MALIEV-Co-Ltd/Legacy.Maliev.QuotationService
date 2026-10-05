using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Logging;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Selected normal Order client observer with real workload provider and PostgreSQL/Redis host.</summary>
public sealed class QuotationOrderPrivateObservationTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private const string Protected = "SYNTHETIC_ORDER_PRIVATE_A591";

    [Theory]
    [InlineData(201, OrderDecisionResult.Completed)]
    [InlineData(409, OrderDecisionResult.Conflict)]
    [InlineData(404, OrderDecisionResult.NotFound)]
    [InlineData(400, OrderDecisionResult.Unavailable)]
    public async Task Nonserver_response_retains_route_workload_idempotency_and_quiet_outcome(int status, OrderDecisionResult expected)
    {
        var transport = new Transport(status);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        var client = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(expected, await client.TransitionAsync(57, true, Protected, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logs.Entries);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("/orderstatuses/histories/57/accepted", request.RequestUri?.AbsolutePath);
            Assert.Equal(Protected, Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
        });
        Assert.All(transport.Contents, content => Assert.Equal(0, content.Reads));
    }

    [Fact]
    public async Task Terminal_server_failure_emits_one_safe_event_after_all_existing_attempts_and_next_call_recovers()
    {
        var transport = new Transport(503);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        var client = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(OrderDecisionResult.Unavailable, await client.TransitionAsync(57, false, Protected, CancellationToken.None));
        var failure = Assert.Single(logs.Entries);
        Assert.True(transport.Calls >= 1);
        Assert.Equal(transport.Calls, failure.AttemptsAtLog);
        AssertSafe(failure, 503);
        Assert.All(transport.Requests, request =>
        {
            Assert.Equal("/orderstatuses/histories/57/declined", request.RequestUri?.AbsolutePath);
            Assert.Equal(Protected, Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        });
        transport.Status = 201;
        var previousCalls = transport.Calls;
        Assert.Equal(OrderDecisionResult.Completed, await client.TransitionAsync(57, false, Protected, CancellationToken.None));
        Assert.Equal(previousCalls + 1, transport.Calls);
        Assert.Single(logs.Entries);
    }

    [Fact]
    public async Task Transport_failure_retains_unavailable_result_and_one_safe_statusless_event()
    {
        var transport = new Transport(0);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(OrderDecisionResult.Unavailable, await app.Services.GetRequiredService<IOrderDecisionClient>()
            .TransitionAsync(57, true, Protected, CancellationToken.None));
        var failure = Assert.Single(logs.Entries);
        Assert.Equal(transport.Calls, failure.AttemptsAtLog);
        AssertSafe(failure, null);
    }

    [Fact]
    public async Task Native_client_deadline_retains_unavailable_result_and_exactly_one_statusless_event()
    {
        var transport = new Transport(-1);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs, nativeDeadline: true);
        using var bootstrap = app.CreateClient();
        Assert.NotNull(await app.Services.GetRequiredService<LegacyServiceAccessTokenProvider>()
            .GetAccessTokenAsync(CancellationToken.None));
        var result = await app.Services.GetRequiredService<IOrderDecisionClient>()
            .TransitionAsync(57, true, Protected, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OrderDecisionResult.Unavailable, result);
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), null);
    }

    [Fact]
    public async Task Original_caller_cancellation_propagates_without_dependency_event()
    {
        var transport = new Transport(-1);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        using var caller = new CancellationTokenSource();
        var pending = app.Services.GetRequiredService<IOrderDecisionClient>().TransitionAsync(57, true, Protected, caller.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Cancel_pending_requests_does_not_invent_native_timeout_event()
    {
        var transport = new Transport(-1);
        var logs = new Audit(() => transport.Calls);
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        using var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IOrderDecisionClient));
        var pending = new OrderDecisionClient(http).TransitionAsync(57, true, Protected, CancellationToken.None);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        http.CancelPendingRequests();
        Assert.Equal(OrderDecisionResult.Unavailable, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Observer_sink_failure_cannot_replace_unavailable_outcome()
    {
        var transport = new Transport(503);
        var logs = new Audit(() => transport.Calls) { Throw = true };
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(OrderDecisionResult.Unavailable, await app.Services.GetRequiredService<IOrderDecisionClient>()
            .TransitionAsync(57, true, Protected, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), 503);
    }

    private WebApplicationFactory<Program> App(Transport transport, Audit logs, bool nativeDeadline = false) =>
        fixture.App(new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.AddHttpClient<IOrderDecisionClient, OrderDecisionClient>(client =>
            {
                if (nativeDeadline) client.Timeout = TimeSpan.FromSeconds(1);
            }).ConfigurePrimaryHttpMessageHandler(() => transport));
        });

    private static void AssertSafe(Record record, int? status)
    {
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Null(record.Exception);
        Assert.Equal(status is null ? new[] { "Dependency", "EventName", "Operation" }
            : new[] { "Dependency", "EventName", "Operation", "StatusCode" }, record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("DependencyRequestFailure", record.Fields["EventName"]);
        Assert.Equal("Order", record.Fields["Dependency"]);
        Assert.Equal("HttpRequest", record.Fields["Operation"]);
        if (status is not null) Assert.Equal(status.Value, record.Fields["StatusCode"]);
        using var json = JsonDocument.Parse(record.Json);
        Assert.Equal(5101, json.RootElement.GetProperty("eventId").GetInt32());
        Assert.Equal("ERROR", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
        foreach (var pair in record.Fields)
            Assert.Equal(JsonSerializer.Serialize(pair.Value), json.RootElement.GetProperty(pair.Key).GetRawText());
        foreach (var forbidden in new[] { Protected, "orderstatuses", "quotation95-order.invalid", "Bearer" })
            Assert.DoesNotContain(forbidden, record.Json, StringComparison.Ordinal);
    }

    private sealed class Transport(int status) : HttpMessageHandler
    {
        public int Status = status;
        public int Calls;
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
        public ConcurrentQueue<UnreadContent> Contents { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Requests.Enqueue(request);
            Entered.TrySetResult();
            if (Status == -1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Status == 0) throw new HttpRequestException(Protected);
            var content = new UnreadContent();
            Contents.Enqueue(content);
            return new((HttpStatusCode)Status) { Content = content };
        }
    }

    private sealed class UnreadContent : HttpContent
    {
        public int Reads;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Increment(ref Reads);
            throw new InvalidOperationException(Protected);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed record Record(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields, string Json, int AttemptsAtLog);

    private sealed class Audit(Func<int> attempts) : ILoggerProvider
    {
        private int Attempts => attempts();
        public bool Throw;
        public ConcurrentQueue<Record> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }
        private sealed class Recorder(Audit owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 5101 || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
                var safe = fields.Where(pair => pair.Key != "{OriginalFormat}").ToDictionary(pair => pair.Key, pair => pair.Value);
                if (!Equals(safe.GetValueOrDefault("Dependency"), "Order")) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
                new PrivateFailureConsoleFormatter().Write(in entry, null, writer);
                owner.Entries.Enqueue(new(level, exception, safe, writer.ToString(), owner.Attempts));
                if (owner.Throw) throw new InvalidOperationException(Protected);
            }
        }
    }
}
