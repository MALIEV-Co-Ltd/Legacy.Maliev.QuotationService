using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Asp.Versioning;
using Legacy.Maliev.QuotationService.Api.Diagnostics;
using Legacy.Maliev.QuotationService.Tests.Controllers;
using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Diagnostics;

/// <summary>Actual Program request pipeline and selected formatter with real PostgreSQL/Redis.</summary>
public sealed class QuotationPrivateObservationHttpTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    private const string Marker = "SYNTHETIC_PRIVATE_OBSERVATION_A591";

    [Fact]
    public async Task Caller_owned_startup_guard_preserves_test_host_exception_and_quiet_success()
    {
        var failure = new InvalidOperationException(Marker);
        var originalExit = Environment.ExitCode;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QuotationStartupBoundary.RunAsync(() => Task.FromException(failure))));
        Assert.Equal(0, await QuotationStartupBoundary.RunAsync(() => Task.CompletedTask));
        Assert.Equal(originalExit, Environment.ExitCode);
    }

    [Fact]
    public void Selected_formatter_discards_rendered_text_state_and_scopes_but_keeps_safe_metadata()
    {
        var output = new PrivateOutput();
        using var app = App(output);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("QuotationPrivateProbe");
        using (logger.BeginScope(new Dictionary<string, object?> { ["CustomerEmail"] = Marker, ["EventName"] = "PrivateProbe" }))
            logger.LogError(new EventId(6106, "PrivateProbe"), new InvalidOperationException(Marker),
                "{EventName} {CustomerEmail} {Payload} {StatusCode}", "PrivateProbe", Marker, Marker, 503);
        var record = Assert.Single(output.Records, row => row.GetProperty("eventId").GetInt32() == 6106);
        Assert.Equal("ERROR", record.GetProperty("severity").GetString());
        Assert.Equal("System.InvalidOperationException", record.GetProperty("exceptionType").GetString());
        Assert.Equal("PrivateProbe", record.GetProperty("EventName").GetString());
        Assert.Equal(503, record.GetProperty("StatusCode").GetInt32());
        Assert.DoesNotContain(Marker, record.GetRawText(), StringComparison.Ordinal);
        Assert.False(record.TryGetProperty("CustomerEmail", out _));
        Assert.False(record.TryGetProperty("Payload", out _));
        Assert.False(record.TryGetProperty("State", out _));
        Assert.False(record.TryGetProperty("Scopes", out _));
        logger.LogInformation(new EventId(6107), "{Payload}", Marker);
        Assert.DoesNotContain(output.Records, row => row.GetProperty("eventId").GetInt32() == 6107);
    }

    [Theory]
    [InlineData("POST", "127.0.0.1", "valid")]
    [InlineData("GET", "192.0.2.10", "valid")]
    [InlineData("GET", "127.0.0.1", "invalid")]
    [InlineData("GET", "127.0.0.1", "missing")]
    public async Task Invalid_diagnostic_request_is_denied_without_failure_or_business_call(string method, string remote, string nonce)
    {
        var boundary = new QuotationNormalIamBoundary();
        var output = new PrivateOutput();
        using var app = App(output, boundary: boundary);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        var context = await Probe(app, method, remote, nonce == "valid" ? Guid.NewGuid().ToString("N") : nonce == "missing" ? null : Marker);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("X-Maliev-Diagnostic-Id"));
        Assert.Empty(Observation(output));
        Assert.Equal(0, boundary.LoginCalls);
        Assert.Equal(0, boundary.IamCalls);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Direct_loopback_probe_uses_real_failure_boundaries_and_rate_limits_per_host(string remote)
    {
        var boundary = new QuotationNormalIamBoundary();
        var output = new PrivateOutput();
        var clock = new FakeTimeProvider();
        using var app = App(output, clock, boundary: boundary);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        var nonce = Guid.NewGuid().ToString("N");
        var first = await Probe(app, "GET", remote, nonce);
        Assert.Equal(500, first.Response.StatusCode);
        Assert.Equal(nonce, first.Response.Headers["X-Maliev-Diagnostic-Id"].ToString());
        Assert.Equal("no-store", first.Response.Headers.CacheControl.ToString());
        Assert.Equal("noindex", first.Response.Headers["X-Robots-Tag"].ToString());
        var failures = Observation(output);
        Assert.Equal(3, failures.Length);
        Assert.Equal(new[] { "CRITICAL", "ERROR", "WARNING" }, failures.Select(row => row.GetProperty("severity").GetString()).Order(StringComparer.Ordinal));
        Assert.All(failures, row =>
        {
            Assert.True(row.GetProperty("Synthetic").GetBoolean());
            Assert.Equal(nonce, row.GetProperty("DiagnosticId").GetString());
            Assert.DoesNotContain("/internal/diagnostics", row.GetRawText(), StringComparison.Ordinal);
        });
        Assert.Equal("Maliev.Aspire.ServiceDefaults.Diagnostics.ProductionObservabilityDiagnosticException",
            Assert.Single(failures, row => row.GetProperty("severity").GetString() == "ERROR").GetProperty("exceptionType").GetString());
        Assert.Equal(429, (await Probe(app, "GET", remote, Guid.NewGuid().ToString("N"))).Response.StatusCode);
        Assert.Equal(3, Observation(output).Length);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(500, (await Probe(app, "GET", remote, Guid.NewGuid().ToString("N"))).Response.StatusCode);
        Assert.Equal(6, Observation(output).Length);
        Assert.Equal(0, boundary.LoginCalls);
        Assert.Equal(0, boundary.IamCalls);
    }

    [Fact]
    public async Task Forwarded_loopback_header_cannot_admit_an_external_probe()
    {
        var output = new PrivateOutput();
        using var app = App(output);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        var context = await Probe(app, "GET", "192.0.2.10", Guid.NewGuid().ToString("N"), forwarded: "127.0.0.1");
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Empty(Observation(output));
    }

    [Fact]
    public async Task Registered_health_identity_is_host_local_and_success_is_quiet_without_cookie()
    {
        var output = new PrivateOutput();
        using var first = App(output);
        using var second = App(new());
        using var client = first.CreateClient();
        using var other = second.CreateClient();
        SelectFormatter(first, output);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("th");
        using var response = await client.GetAsync("/quotation/liveness");
        using var again = await client.GetAsync("/quotation/liveness");
        using var separate = await other.GetAsync("/quotation/liveness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var instance = Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance"));
        Assert.True(Guid.TryParseExact(instance, "N", out _));
        Assert.Equal(instance, Assert.Single(again.Headers.GetValues("X-Maliev-Health-Instance")));
        Assert.NotEqual(instance, Assert.Single(separate.Headers.GetValues("X-Maliev-Health-Instance")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(Observation(output));
        using var ordinary = await client.GetAsync("/quotation-observability-tests/response?status=204");
        Assert.Equal(HttpStatusCode.NoContent, ordinary.StatusCode);
        Assert.False(ordinary.Headers.Contains("X-Maliev-Health-Instance"));
    }

    [Fact]
    public async Task Readiness_failure_is_throttled_and_quiet_recovery_resets_fingerprint()
    {
        var output = new PrivateOutput();
        var health = new HealthState();
        var clock = new FakeTimeProvider();
        using var app = App(output, clock, health);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        health.Failed = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.GetAsync("/quotation/readiness");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        Assert.Single(Events(output, "HealthProbeFailure"));
        clock.Advance(TimeSpan.FromMinutes(5));
        using (var response = await client.GetAsync("/quotation/readiness"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, Events(output, "HealthProbeFailure").Length);
        health.Failed = false;
        using (var response = await client.GetAsync("/quotation/readiness")) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Events(output, "HealthProbeFailure").Length);
        health.Failed = true;
        using (var response = await client.GetAsync("/quotation/readiness")) Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, Events(output, "HealthProbeFailure").Length);
        Assert.All(Events(output, "HealthProbeFailure"), row => Assert.Equal("Readiness", row.GetProperty("Operation").GetString()));
        Assert.All(output.Records, row => Assert.DoesNotContain(Marker, row.GetRawText(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(204, 0)]
    [InlineData(400, 0)]
    [InlineData(503, 1)]
    public async Task Completed_response_observer_records_only_server_failure(int status, int records)
    {
        var output = new PrivateOutput();
        using var app = App(output);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        using var response = await client.GetAsync($"/quotation-observability-tests/response?status={status}&private={Marker}");
        Assert.Equal(status, (int)response.StatusCode);
        var failures = Events(output, "HandledOperationFailure");
        Assert.Equal(records, failures.Length);
        Assert.All(failures, row =>
        {
            Assert.Equal("HttpResponse", row.GetProperty("Operation").GetString());
            Assert.Equal(status, row.GetProperty("StatusCode").GetInt32());
            Assert.DoesNotContain(Marker, row.GetRawText(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Thrown_failure_retains_existing_exception_owner_without_completed_response_duplicate()
    {
        var output = new PrivateOutput();
        using var app = App(output);
        using var client = app.CreateClient();
        SelectFormatter(app, output);
        using var response = await client.GetAsync("/quotation-observability-tests/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(Events(output, "HandledOperationFailure"));
        Assert.Contains(output.Records, row => row.GetProperty("exceptionType").GetString() == "System.InvalidOperationException");
        Assert.All(output.Records, row => Assert.DoesNotContain(Marker, row.GetRawText(), StringComparison.Ordinal));
    }

    private WebApplicationFactory<Program> App(PrivateOutput output, FakeTimeProvider? clock = null,
        HealthState? health = null, QuotationNormalIamBoundary? boundary = null) =>
        fixture.App(boundary ?? new()).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(output));
            builder.ConfigureTestServices(services =>
            {
                if (clock is not null) services.AddSingleton<TimeProvider>(clock);
                services.AddControllers().AddApplicationPart(typeof(QuotationPrivateObservationTestController).Assembly);
                if (health is not null) services.AddHealthChecks().AddCheck("PrivateObservationControlled",
                    () => health.Failed ? HealthCheckResult.Unhealthy(Marker) : HealthCheckResult.Healthy());
            });
        });

    private static void SelectFormatter(WebApplicationFactory<Program> app, PrivateOutput output)
    {
        Assert.Equal(PrivateFailureConsoleFormatter.FormatterName,
            app.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        output.Formatter = Assert.IsType<PrivateFailureConsoleFormatter>(Assert.Single(
            app.Services.GetServices<ConsoleFormatter>(), formatter => formatter.Name == PrivateFailureConsoleFormatter.FormatterName));
    }

    private static Task<HttpContext> Probe(WebApplicationFactory<Program> app, string method, string remote,
        string? nonce, string? forwarded = null) => app.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = "/internal/diagnostics/observability";
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            if (nonce is not null) context.Request.Headers["X-Maliev-Diagnostic-Id"] = nonce;
            if (forwarded is not null) context.Request.Headers["X-Forwarded-For"] = forwarded;
        });

    private static JsonElement[] Observation(PrivateOutput output) => output.Records.Where(row =>
        row.GetProperty("logger").GetString()?.Contains("PrivateRequestObservation", StringComparison.Ordinal) == true
        || row.GetProperty("exceptionType").GetString() == "Maliev.Aspire.ServiceDefaults.Diagnostics.ProductionObservabilityDiagnosticException").ToArray();

    private static JsonElement[] Events(PrivateOutput output, string name) => output.Records.Where(row =>
        row.TryGetProperty("EventName", out var value) && value.GetString() == name).ToArray();

    private sealed class HealthState { public bool Failed; }

    private sealed class PrivateOutput : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public PrivateFailureConsoleFormatter Formatter { get; set; } = new();
        public ConcurrentQueue<JsonElement> Records { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }

        private sealed class Recorder(PrivateOutput owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                using var writer = new StringWriter();
                var entry = new LogEntry<TState>(level, category, eventId, state, exception, formatter);
                owner.Formatter.Write(in entry, owner.scopes, writer);
                if (writer.GetStringBuilder().Length == 0) return;
                using var document = JsonDocument.Parse(writer.ToString());
                owner.Records.Enqueue(document.RootElement.Clone());
            }
        }
    }
}

/// <summary>Only registered by the private observation fixture; never part of the production API assembly.</summary>
[ApiExplorerSettings(IgnoreApi = true)]
[ApiController]
[ApiVersionNeutral]
[Route("quotation-observability-tests")]
public sealed class QuotationPrivateObservationTestController : ControllerBase
{
    /// <summary>Returns a controlled completed status for the actual host observer.</summary>
    [HttpGet("response")]
    public IActionResult ResponseStatus([FromQuery] int status) => StatusCode(status);

    /// <summary>Throws controlled private text through the existing exception owner.</summary>
    [HttpGet("throw")]
    public IActionResult ThrowFailure() => throw new InvalidOperationException("SYNTHETIC_PRIVATE_OBSERVATION_A591");
}
