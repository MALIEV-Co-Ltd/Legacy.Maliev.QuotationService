using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Real normal host/provider with external authority transport controlled; no synthetic grant.</summary>
public sealed class QualificationPrivateObservationTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private const string Protected = "SYNTHETIC_QUALIFICATION_PRIVATE_A591";

    [Theory]
    [InlineData(401, 403)]
    [InlineData(403, 403)]
    [InlineData(404, 503)]
    [InlineData(429, 503)]
    public async Task Nonserver_response_keeps_existing_outcome_and_quiet_dependency(int status, int expected)
    {
        var transport = new Transport("status", status);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(expected, await Check(app, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logs.Entries);
        AssertWire(transport);
    }

    [Fact]
    public async Task Terminal_server_response_has_one_sdk_event_and_fresh_denial_recovers_without_retry()
    {
        var transport = new Transport("status", 503);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(503, await Check(app, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", 503);
        Assert.Equal(1, transport.Calls);
        transport.Mode = "denial";
        Assert.Equal(403, await Check(app, CancellationToken.None));
        Assert.Equal(2, transport.Calls);
        Assert.Single(logs.Entries);
        AssertWire(transport);
    }

    [Theory]
    [InlineData("headers-network", "HttpRequest")]
    [InlineData("headers-io", "QualificationHeadersTransport")]
    public async Task Header_transport_failure_retains_503_with_one_correct_owner(string mode, string operation)
    {
        var transport = new Transport(mode);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(503, await Check(app, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), operation, null);
    }

    [Theory]
    [InlineData("body-io")]
    [InlineData("body-cancel-shape")]
    public async Task Body_failure_has_one_consumer_event_with_actual_200_without_native_timeout_guess(string mode)
    {
        var transport = new Transport(mode);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(503, await Check(app, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), "QualificationBodyTransport", 200);
    }

    [Theory]
    [InlineData("headers-wait", "QualificationHeadersDeadline", null)]
    [InlineData("body-wait", "QualificationBodyDeadline", 200)]
    public async Task Actual_linked_ten_second_deadline_is_owned_in_each_phase(string mode, string operation, int? status)
    {
        var transport = new Transport(mode);
        var logs = new Audit();
        // Hold native timeout open only in this fixture so the unchanged real linked deadline is decisive.
        await using var app = App(transport, logs, Timeout.InfiniteTimeSpan);
        using var bootstrap = app.CreateClient();
        await Prime(app);
        Assert.Equal(503, await Check(app, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), operation, status);
    }

    [Theory]
    [InlineData("headers-wait")]
    [InlineData("body-wait")]
    public async Task Original_caller_cancellation_propagates_in_each_phase_without_event(string mode)
    {
        var transport = new Transport(mode);
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        await Prime(app);
        using var caller = new CancellationTokenSource();
        var pending = Check(app, caller.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.Calls);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Native_client_deadline_is_sdk_owned_statusless_without_consumer_duplicate()
    {
        var transport = new Transport("headers-wait");
        var logs = new Audit();
        await using var app = App(transport, logs, TimeSpan.FromSeconds(1));
        using var bootstrap = app.CreateClient();
        await Prime(app);
        Assert.Equal(503, await Check(app, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", null);
    }

    [Fact]
    public async Task Malformed_wire_response_keeps_fail_closed_503_without_transport_event()
    {
        var logs = new Audit();
        await using var app = App(new Transport("malformed"), logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(503, await Check(app, CancellationToken.None));
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Consumer_sink_failure_cannot_replace_body_failure_503()
    {
        var logs = new Audit { Throw = true };
        await using var app = App(new Transport("body-io"), logs);
        using var bootstrap = app.CreateClient();
        Assert.Equal(503, await Check(app, CancellationToken.None));
        AssertSafe(Assert.Single(logs.Entries), "QualificationBodyTransport", 200);
    }

    [Fact]
    public async Task IOException_after_caller_cancellation_retains_existing_503_without_claiming_universal_quietness()
    {
        var transport = new Transport("body-io-after-cancel");
        var logs = new Audit();
        await using var app = App(transport, logs);
        using var bootstrap = app.CreateClient();
        await Prime(app);
        using var caller = new CancellationTokenSource();
        var pending = Check(app, caller.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        Assert.Equal(503, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertSafe(Assert.Single(logs.Entries), "QualificationBodyTransport", 200);
    }

    [Fact]
    public async Task Sdk_native_record_then_linked_deadline_before_consumer_catch_has_one_event()
    {
        var transport = new Transport("headers-wait");
        using var originalCaller = new CancellationTokenSource();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(originalCaller.Token);
        var callbacks = 0;
        var canCancel = false;
        var initiallyUncancelled = false;
        var expiredBeforeRelease = false;
        var logs = new Audit
        {
            AfterSdkRecord = () =>
            {
                Interlocked.Increment(ref callbacks);
                // Actual SDK native record has been captured. Hold its synchronous sink callback
                // until the unchanged real linked ten-second token expires, before consumer catch.
                var linked = operation.Token;
                canCancel = linked.CanBeCanceled;
                initiallyUncancelled = !linked.IsCancellationRequested;
                // Arm the real ten-second linked timer only after SDK ownership is captured.
                // This barrier makes ordering independent of runner scheduling of equal timers.
                operation.CancelAfter(TimeSpan.FromSeconds(10));
                if (canCancel) expiredBeforeRelease = linked.WaitHandle.WaitOne(TimeSpan.FromSeconds(15)) && linked.IsCancellationRequested;
            }
        };
        await using var app = App(transport, logs, TimeSpan.FromSeconds(10));
        using var bootstrap = app.CreateClient();
        await Prime(app);
        var pending = app.Services.GetRequiredService<QualificationAuthorityClient>()
            .CheckWithinDeadlineAsync(Protected, "employee-private", QuotationPermissions.RequestsRead, 57, originalCaller.Token, operation.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Both configured deadlines are ten seconds. The sink arms the linked timer only after
        // actual native ownership, then blocks unwind until its real cancellation occurs.
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(503, result);
        Assert.Equal(1, callbacks);
        // Assert outside the sink: the SDK intentionally contains sink exceptions.
        Assert.True(canCancel);
        Assert.True(initiallyUncancelled);
        Assert.True(expiredBeforeRelease);
        Assert.Equal(1, transport.Calls);
        AssertSafe(Assert.Single(logs.Entries), "HttpRequest", null);
    }

    [Fact]
    public void Privacy_oracle_allows_status_digits_in_utc_and_trace_metadata()
    {
        var record = OracleRecord(null);
        Assert.Contains("504", record.Json, StringComparison.Ordinal);
        AssertSafe(record, "QualificationHeadersTransport", null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(200)]
    public void Privacy_oracle_rejects_fabricated_serialized_status(int? expected)
    {
        var record = OracleRecord(expected);
        var payload = JsonNode.Parse(record.Json)!.AsObject();
        payload["StatusCode"] = 504;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertSafe(record with { Json = payload.ToJsonString() }, "QualificationHeadersTransport", expected));
    }

    [Fact]
    public void Privacy_oracle_still_rejects_actual_protected_text()
    {
        var record = OracleRecord(null);
        var payload = JsonNode.Parse(record.Json)!.AsObject();
        payload["message"] = Protected;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertSafe(record with { Json = payload.ToJsonString() }, "QualificationHeadersTransport", null));
    }

    private static Record OracleRecord(int? status)
    {
        var fields = new Dictionary<string, object?>
        {
            ["EventName"] = "DependencyRequestFailure",
            ["Dependency"] = QualificationAuthorityClient.ClientName,
            ["Operation"] = "QualificationHeadersTransport",
        };
        if (status is not null) fields.Add("StatusCode", status.Value);
        var logs = new Audit();
        logs.CreateLogger("QualificationPrivacyOracleRegression").Log(LogLevel.Error, new EventId(5101),
            fields, null, static (_, _) => "Application diagnostic");
        var record = Assert.Single(logs.Entries);
        var payload = JsonNode.Parse(record.Json)!.AsObject();
        payload["occurredAtUtc"] = "2026-10-06T08:44:41.0504431+00:00";
        payload["traceId"] = "00000000000000000000000000000504";
        return record with { Json = payload.ToJsonString() };
    }

    private WebApplicationFactory<Program> App(Transport transport, Audit logs, TimeSpan? nativeTimeout = null) =>
        fixture.App(new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services => services.AddHttpClient(QualificationAuthorityClient.ClientName, client =>
            {
                if (nativeTimeout is { } timeout) client.Timeout = timeout;
            }).ConfigurePrimaryHttpMessageHandler(() => transport));
        });

    private static Task<int> Check(WebApplicationFactory<Program> app, CancellationToken token) =>
        app.Services.GetRequiredService<QualificationAuthorityClient>().CheckAsync(Protected, "employee-private", QuotationPermissions.RequestsRead, 57, token);

    private static async Task Prime(WebApplicationFactory<Program> app)
    {
        Assert.NotNull(await app.Services.GetRequiredService<ILegacyServiceAccessTokenProvider>().GetAccessTokenAsync(CancellationToken.None));
    }

    private static void AssertWire(Transport transport)
    {
        Assert.Equal(transport.Calls, transport.WireChecks);
    }

    private static void AssertSafe(Record record, string operation, int? status)
    {
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Null(record.Exception);
        Assert.Equal(status is null ? new[] { "Dependency", "EventName", "Operation" }
            : new[] { "Dependency", "EventName", "Operation", "StatusCode" }, record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("DependencyRequestFailure", record.Fields["EventName"]);
        Assert.Equal(QualificationAuthorityClient.ClientName, record.Fields["Dependency"]);
        Assert.Equal(operation, record.Fields["Operation"]);
        if (status is not null) Assert.Equal(status.Value, record.Fields["StatusCode"]);
        using var json = JsonDocument.Parse(record.Json);
        Assert.Equal(5101, json.RootElement.GetProperty("eventId").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("exceptionType").ValueKind);
        foreach (var pair in record.Fields)
            Assert.Equal(JsonSerializer.Serialize(pair.Value), json.RootElement.GetProperty(pair.Key).GetRawText());
        var serializedStatuses = json.RootElement.EnumerateObject()
            .Where(property => property.Name.Equals("StatusCode", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(status is null ? 0 : 1, serializedStatuses.Length);
        if (status is not null)
        {
            var serializedStatus = Assert.Single(serializedStatuses);
            Assert.Equal("StatusCode", serializedStatus.Name);
            Assert.Equal(status.Value, serializedStatus.Value.GetInt32());
        }
        foreach (var forbidden in new[] { Protected, "employee-private", "introspection", "Bearer", "TimeoutException" })
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
            Assert.Equal("/auth/v1/introspection/quotation-qualification", request.RequestUri?.AbsolutePath);
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(4, payload.RootElement.EnumerateObject().Count());
            Assert.Equal(Protected, payload.RootElement.GetProperty("employeeAccessToken").GetString());
            Assert.Equal(QuotationPermissions.RequestsRead, payload.RootElement.GetProperty("permission").GetString());
            Assert.Equal(QualificationAuthorityAttribute.Purpose, payload.RootElement.GetProperty("purpose").GetString());
            Assert.Equal(57, payload.RootElement.GetProperty("requestId").GetInt32());
            Interlocked.Increment(ref WireChecks);
            if (Mode == "headers-wait")
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (Mode == "headers-network") throw new HttpRequestException(Protected);
            if (Mode == "headers-io") throw new IOException(Protected);
            HttpContent content = Mode.StartsWith("body-", StringComparison.Ordinal) ? new BodyContent(Mode, Entered)
                : Mode == "denial" ? new StringContent(JsonSerializer.Serialize(new { allowed = false, subject = (string?)null, permission = QuotationPermissions.RequestsRead, purpose = QualificationAuthorityAttribute.Purpose, requestId = 57 }))
                : new StringContent("{");
            return new(Mode == "denial" ? HttpStatusCode.OK : (HttpStatusCode)status) { Content = content };
        }
    }

    private sealed class BodyContent(string mode, TaskCompletionSource entered) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BodyStream(mode, entered));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BodyStream(string mode, TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            if (mode == "body-wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (mode == "body-io-after-cancel")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { throw new IOException(Protected); }
            }
            if (mode == "body-cancel-shape") throw new TaskCanceledException(Protected, new TimeoutException(Protected));
            throw new IOException(Protected);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record Record(LogLevel Level, Exception? Exception, Dictionary<string, object?> Fields, string Json);

    private sealed class Audit : ILoggerProvider
    {
        public bool Throw;
        public Action? AfterSdkRecord;
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
                if (!Equals(safe.GetValueOrDefault("Dependency"), QualificationAuthorityClient.ClientName)) return;
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
                new PrivateFailureConsoleFormatter().Write(in entry, null, writer);
                owner.Entries.Enqueue(new(level, exception, safe, writer.ToString()));
                if (category == "Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateDependencyFailureHandler") owner.AfterSdkRecord?.Invoke();
                if (owner.Throw) throw new InvalidOperationException(Protected);
            }
        }
    }
}
