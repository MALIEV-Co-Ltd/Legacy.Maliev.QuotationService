using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Actual registered IAM client; negative authority replies and external transport only.</summary>
public sealed class QuotationIamTerminalObservationTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private const string Protected = "SYNTHETIC_IAM_PRIVATE_A591";

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task Nonserver_header_status_retains_false_and_quiet_selected_observer(int status)
    {
        var transport = new Transport("status", status);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.False(await Check(app, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(599)]
    public async Task Terminal_server_headers_have_one_safe_selected_event_without_retry(int status)
    {
        var transport = new Transport("status", status);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.False(await Check(app, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", status);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
        transport.Mode = "denial";
        Assert.False(await Check(app, CancellationToken.None));
        Assert.Equal(2, transport.Calls);
        Assert.Equal(2, transport.WireChecks);
        Assert.Single(logs.Entries);
    }

    [Fact]
    public async Task Header_network_failure_remains_false_with_one_statusless_selected_event()
    {
        var transport = new Transport("network");
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.False(await Check(app, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", null);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
    }

    [Fact]
    public async Task Original_caller_cancellation_after_external_entry_propagates_without_selected_event()
    {
        var transport = new Transport("wait");
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.NotNull(await app.Services.GetRequiredService<ILegacyServiceAccessTokenProvider>()
            .GetAccessTokenAsync(CancellationToken.None));
        using var caller = new CancellationTokenSource();
        var pending = Check(app, caller.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Successful_negative_authority_response_retains_false_without_selected_failure_event()
    {
        var transport = new Transport("denial");
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.False(await Check(app, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Selected_sink_failure_cannot_replace_actual_iam_false_outcome()
    {
        var transport = new Transport("status", 503);
        var logs = new Audit { Throw = true };
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.False(await Check(app, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", 503);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, transport.WireChecks);
    }

    private WebApplicationFactory<Program> App(Transport transport, Audit logs) =>
        fixture.App(new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.AddHttpClient("IAMService")
                .ConfigurePrimaryHttpMessageHandler(() => transport));
        });

    private async Task<bool> Check(WebApplicationFactory<Program> app, CancellationToken token)
    {
        using var scope = app.Services.CreateScope();
        Assert.IsType<IamServiceClient>(scope.ServiceProvider.GetRequiredService<IIamServiceClient>());
        var row = await fixture.SeedAsync();
        string before;
        await using (var db = fixture.Context())
            before = JsonSerializer.Serialize(await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id));
        using var client = fixture.Client(app);
        try
        {
            using var response = await client.PutAsJsonAsync($"/quotations/{row.Id}", new
            {
                row.CustomerId, row.EmployeeId, row.InvoiceId, row.CurrencyId, row.Period,
                row.ExpirationDate, row.Subtotal, row.Vat, row.Total, row.WithholdingTax,
                Comment = "synthetic IAM denied update", row.Accepted
            }, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        finally
        {
            // Also read back after original caller cancellation; database work uses no cancelled token.
            await using var db = fixture.Context();
            var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
            Assert.Equal(before, JsonSerializer.Serialize(stored));
            Assert.Empty(await db.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToListAsync());
        }
    }

    private static void AssertSafe(Record record, string operation, int? status)
    {
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Null(record.Exception);
        Assert.Equal(status is null ? new[] { "Dependency", "EventName", "Operation" }
            : new[] { "Dependency", "EventName", "Operation", "StatusCode" }, record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("DependencyRequestFailure", record.Fields["EventName"]);
        Assert.Equal("IAMService", record.Fields["Dependency"]);
        Assert.Equal(operation, record.Fields["Operation"]);
        if (status is not null) Assert.Equal(status.Value, record.Fields["StatusCode"]);
        using var json = JsonDocument.Parse(record.Json);
        Assert.Equal(5101, json.RootElement.GetProperty("eventId").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
        foreach (var pair in record.Fields)
            Assert.Equal(JsonSerializer.Serialize(pair.Value), json.RootElement.GetProperty(pair.Key).GetRawText());
        foreach (var forbidden in new[] { Protected, "service:legacy-accounting", "legacy.quotations.update", "check-permission", "Bearer", "TimeoutException" })
            Assert.DoesNotContain(forbidden, record.Json, StringComparison.Ordinal);
    }

    private sealed class Transport(string mode, int status = 200) : HttpMessageHandler
    {
        public string Mode = mode;
        public int Calls;
        public int WireChecks;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri?.AbsolutePath);
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key"))));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(4, payload.RootElement.EnumerateObject().Count());
            Assert.Equal("service:legacy-accounting", payload.RootElement.GetProperty("principalId").GetString());
            Assert.Equal("legacy.quotations.update", payload.RootElement.GetProperty("permissionId").GetString());
            Assert.Equal("global", payload.RootElement.GetProperty("resourcePath").GetString());
            Assert.True(payload.RootElement.GetProperty("bypassCache").GetBoolean());
            Interlocked.Increment(ref WireChecks);
            Entered.TrySetResult();
            if (Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Mode == "network") throw new HttpRequestException(Protected);
            return new((HttpStatusCode)(Mode == "denial" ? 200 : status))
            { Content = new StringContent("{\"allowed\":false}", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Record(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields, string Json);

    private sealed class Audit : ILoggerProvider
    {
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
                if (!Equals(safe.GetValueOrDefault("Dependency"), "IAMService")) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
                new PrivateFailureConsoleFormatter().Write(in entry, null, writer);
                owner.Entries.Enqueue(new(level, exception, safe, writer.ToString()));
                if (owner.Throw) throw new InvalidOperationException(Protected);
            }
        }
    }
}
