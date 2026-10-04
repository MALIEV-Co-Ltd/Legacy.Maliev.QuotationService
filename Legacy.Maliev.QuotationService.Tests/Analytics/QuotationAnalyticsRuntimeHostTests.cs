using Legacy.Maliev.QuotationService.Api.Workers;
using Legacy.Maliev.QuotationService.Tests.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections;
using System.Reflection;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Collections.Concurrent;
using Legacy.Maliev.QuotationService.Api.Analytics;

namespace Legacy.Maliev.QuotationService.Tests.Analytics;

/// <summary>Actual accepted Program composition; no references to unimplemented production types.</summary>
public sealed class QuotationAnalyticsRuntimeHostTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    [Theory]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    public async Task Program_DecisionDocumentation_ExposesOptionalContextOnlyOutsideProduction(
        string environment, bool documentationAvailable)
    {
        var boundary = new QuotationNormalIamBoundary();
        using var original = fixture.App(boundary, environment: environment);
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, "true", enabled: false);
            BlockExternalHttp(builder);
        });
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/quotation/openapi/v1.json");
        Assert.Equal(documentationAvailable ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, boundary.IamCalls);
        if (!documentationAvailable) return;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("Legacy MALIEV Quotation Service API", root.GetProperty("info").GetProperty("title").GetString());
        var operation = Assert.Single(root.GetProperty("paths").EnumerateObject(),
            path => path.Name.EndsWith("/{quotationId}/decision", StringComparison.OrdinalIgnoreCase));
        Assert.True(operation.Value.TryGetProperty("put", out var put));
        Assert.Equal("Records a quotation decision with its invoice and optional first-acceptance analytics context.",
            put.GetProperty("summary").GetString());
        var pathParameter = Assert.Single(put.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "quotationId"
                && parameter.GetProperty("in").GetString() == "path");
        Assert.Equal("Quotation identifier whose decision is being recorded.", pathParameter.GetProperty("description").GetString());
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty("QuotationDecisionRequest");
        var properties = schema.GetProperty("properties");
        Assert.Equal(new[] { "Accepted", "ClientId", "Currency", "EmployeeInitiated", "InvoiceId", "SessionId", "UserId" },
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        var required = schema.TryGetProperty("required", out var requiredNode)
            ? requiredNode.EnumerateArray().Select(item => item.GetString()).ToArray() : [];
        foreach (var field in new[] { "ClientId", "SessionId", "UserId", "Currency" })
        {
            Assert.DoesNotContain(field, required);
            Assert.Contains("string", properties.GetProperty(field).GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal("Optional consent-gated client identifier, supplied together with SessionId and limited to 128 raw characters.",
            properties.GetProperty("ClientId").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void Program_EnabledAnalyticsWithoutCredentials_FailsClosedBeforeDelivery(string? standby)
    {
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, standby, enabled: true);
            BlockExternalHttp(builder);
        });
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task Program_LeaseStore_ConcurrentClaimsAndReclaimedTokenAcknowledgementsAreFenced()
    {
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, "true", enabled: false);
            BlockExternalHttp(builder);
        });
        using var bootstrap = app.CreateClient();
        // Establish real accepted Program and PostgreSQL health before checking the absent lease boundary.
        Assert.Contains(app.Services.GetServices<IHostedService>(), service => service is ExpiredQuotationWorker);
        await using (var read = fixture.Context()) await read.Quotations.CountAsync();
        var storeTypes = typeof(Program).Assembly.GetTypes().Concat(typeof(QuotationDbContext).Assembly.GetTypes())
            .Where(type => type.Name == "GoogleAnalyticsOutboxStore").ToArray();
        Assert.True(storeTypes.Length == 1, "Healthy accepted runtime has no unique durable analytics lease store.");
        using var leftScope = app.Services.CreateScope();
        using var rightScope = app.Services.CreateScope();
        var left = leftScope.ServiceProvider.GetService(storeTypes[0]);
        var right = rightScope.ServiceProvider.GetService(storeTypes[0]);
        Assert.NotNull(left);
        Assert.NotNull(right);
        var now = new DateTime(2026, 10, 3, 1, 2, 3);
        await SeedDueIntentAsync(now);
        var claims = await Task.WhenAll(ClaimAsync(left, now), ClaimAsync(right, now));
        var first = Assert.Single(claims.SelectMany(rows => rows));
        Assert.Empty(await ClaimAsync(right, now.AddSeconds(59)));
        var reclaimed = Assert.Single(await ClaimAsync(right, now.AddSeconds(60)));
        var id = Property<long>(first, "Id");
        var firstToken = Property<Guid>(first, "LeaseToken");
        var nextToken = Property<Guid>(reclaimed, "LeaseToken");
        Assert.NotEqual(firstToken, nextToken);
        Assert.Equal(2, Property<int>(reclaimed, "AttemptCount"));
        await InvokeAsync(left, "MarkSentAsync", id, firstToken, now.AddSeconds(61), CancellationToken.None);
        await InvokeAsync(left, "MarkRetryAsync", id, firstToken, now.AddSeconds(62), "HTTP 503", CancellationToken.None);
        await InvokeAsync(left, "MarkFailedAsync", id, firstToken, now.AddSeconds(62), "HTTP 400", CancellationToken.None);
        await using (var read = fixture.Context())
            Assert.Equal(1, await read.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM "GoogleAnalyticsOutbox"
                WHERE "ID" = {id} AND "LeaseToken" = {nextToken} AND "SentUtc" IS NULL AND "FailedUtc" IS NULL
                """).SingleAsync());
        await InvokeAsync(right, "MarkSentAsync", id, nextToken, now.AddSeconds(61), CancellationToken.None);
        await using (var read = fixture.Context())
            Assert.Equal(1, await read.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM "GoogleAnalyticsOutbox"
                WHERE "ID" = {id} AND "SentUtc" IS NOT NULL AND "FailedUtc" IS NULL AND "AttemptCount" = 2
                """).SingleAsync());
        Assert.Empty(await ClaimAsync(left, now.AddHours(1)));
    }

    [Fact]
    public async Task Program_DisabledProcessor_LeavesPersistedDueIntentUnclaimed()
    {
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, "true", enabled: false);
            BlockExternalHttp(builder);
        });
        using var bootstrap = app.CreateClient();
        await using (var read = fixture.Context()) await read.Quotations.CountAsync();
        var processorTypes = typeof(Program).Assembly.GetTypes()
            .Where(type => type.Name == "GoogleAnalyticsDeliveryProcessor").ToArray();
        Assert.True(processorTypes.Length == 1, "Healthy accepted runtime has no unique analytics delivery processor.");
        using var scope = app.Services.CreateScope();
        var processor = scope.ServiceProvider.GetService(processorTypes[0]);
        Assert.NotNull(processor);
        await SeedDueIntentAsync(new DateTime(2020, 1, 1));
        var operation = await InvokeAsync(processor, "DeliverBatchAsync", CancellationToken.None);
        Assert.Equal(0, (int)operation.GetType().GetProperty("Result")!.GetValue(operation)!);
        await using var verify = fixture.Context();
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value" FROM "GoogleAnalyticsOutbox"
            WHERE "QuotationID" = 7 AND "AttemptCount" = 0 AND "LeaseToken" IS NULL
                AND "LeaseUntilUtc" IS NULL AND "SentUtc" IS NULL AND "FailedUtc" IS NULL
            """).SingleAsync());
    }

    [Theory]
    [InlineData(204, 10, "sent")]
    [InlineData(400, 10, "failed")]
    [InlineData(408, 10, "retry")]
    [InlineData(429, 10, "retry")]
    [InlineData(503, 10, "retry")]
    [InlineData(503, 1, "failed")]
    [InlineData(-1, 10, "retry")]
    [InlineData(-1, 1, "failed")]
    [InlineData(-2, 10, "retry")]
    [InlineData(-2, 1, "failed")]
    public async Task Program_ProviderTransport_PreservesPayloadAndPersistsBoundedOutcome(int status, int maxAttempts, string outcome)
    {
        var transport = new ProviderTransport(status);
        var audit = new ProviderAudit();
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, "true", enabled: true);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoogleAnalyticsMeasurementProtocol:MeasurementId"] = "G-FIXTURE&1",
                ["GoogleAnalyticsMeasurementProtocol:ApiSecret"] = "synthetic/secret?1",
                ["GoogleAnalyticsMeasurementProtocol:MaxAttempts"] = maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(audit);
                logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Trace);
            });
            builder.ConfigureTestServices(services => services.PostConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = transport)));
        });
        using var bootstrap = app.CreateClient();
        var occurred = new DateTime(2026, 10, 1, 2, 3, 4).AddTicks(7);
        await SeedDueIntentAsync(occurred, attribution: true);
        using var scope = app.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>().DeliverBatchAsync(CancellationToken.None));
        Assert.Equal(1, transport.Calls);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(transport.Payload));
        Assert.Equal(new[] { "client_id", "events", "timestamp_micros", "user_id" },
            payload.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("123.456", payload.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("opaque-7", payload.RootElement.GetProperty("user_id").GetString());
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(occurred, DateTimeKind.Utc)).ToUnixTimeMilliseconds() * 1000,
            payload.RootElement.GetProperty("timestamp_micros").GetInt64());
        var eventRow = Assert.Single(payload.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("close_convert_lead", eventRow.GetProperty("name").GetString());
        var values = eventRow.GetProperty("params");
        Assert.Equal(new[] { "currency", "engagement_time_msec", "event_key", "journey_id", "session_id", "source_transaction_id", "transaction_id", "value" },
            values.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("quotation-7", values.GetProperty("transaction_id").GetString());
        Assert.Equal("request-17", values.GetProperty("source_transaction_id").GetString());
        Assert.Equal("quotation-7:close_convert_lead:v1", values.GetProperty("event_key").GetString());
        Assert.Equal("THB", values.GetProperty("currency").GetString());
        Assert.Equal("789", values.GetProperty("session_id").GetString());
        Assert.Equal(1, values.GetProperty("engagement_time_msec").GetInt32());
        Assert.Equal("11111111-2222-3333-4444-555555555555", values.GetProperty("journey_id").GetString());
        Assert.Equal(104m, values.GetProperty("value").GetDecimal());
        await using var read = fixture.Context();
        var stored = await read.GoogleAnalyticsOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.LeaseToken);
        Assert.Null(stored.LeaseUntilUtc);
        Assert.Equal(outcome == "sent", stored.SentUtc is not null);
        Assert.Equal(outcome == "failed", stored.FailedUtc is not null);
        var expectedDiagnostic = status == -1 ? nameof(HttpRequestException)
            : status == -2 ? nameof(TaskCanceledException) : $"HTTP {status}";
        Assert.Equal(outcome == "sent" ? null : expectedDiagnostic, stored.LastError);
        if (outcome == "retry") Assert.InRange(stored.NextAttemptUtc, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(15));
        Assert.Equal(outcome == "failed" ? 1 : 0, audit.Entries.Count(entry => entry.Id == 5201));
        Assert.Equal(outcome == "failed" ? 1 : 0, audit.Entries.Count(entry => entry.Level == LogLevel.Error));
        Assert.All(audit.Entries, entry =>
        {
            Assert.DoesNotContain("api_secret", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic/secret?1", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("PROVIDER_BODY_DO_NOT_LOG", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("123.456", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("opaque-7", entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Program_HostCancellation_LeavesClaimForLeaseRecoveryWithoutTerminalFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ProviderTransport(-3, cancellation);
        var audit = new ProviderAudit();
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, "true", enabled: true);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GoogleAnalyticsMeasurementProtocol:MeasurementId"] = "G-FIXTURE&1",
                    ["GoogleAnalyticsMeasurementProtocol:ApiSecret"] = "synthetic/secret?1",
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(audit));
            builder.ConfigureTestServices(services => services.PostConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = transport)));
        });
        using var bootstrap = app.CreateClient();
        var occurred = new DateTime(2026, 10, 1, 2, 3, 4).AddTicks(7);
        await SeedDueIntentAsync(occurred);
        using var scope = app.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<GoogleAnalyticsDeliveryProcessor>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.DeliverBatchAsync(cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, transport.Calls);
        await using var read = fixture.Context();
        var stored = await read.GoogleAnalyticsOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.LeaseToken);
        Assert.NotNull(stored.LeaseUntilUtc);
        Assert.Null(stored.SentUtc);
        Assert.Null(stored.FailedUtc);
        Assert.Null(stored.LastError);
        Assert.Equal(occurred, stored.NextAttemptUtc);
        Assert.DoesNotContain(audit.Entries, entry => entry.Id == 5201);
    }

    private sealed class ProviderTransport(int status, CancellationTokenSource? hostCancellation = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Payload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://www.google-analytics.com/mp/collect?measurement_id=G-FIXTURE%261&api_secret=synthetic%2Fsecret%3F1", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", request.Content?.Headers.ContentType?.CharSet);
            Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("Maliev.DependencyFailureOwnedByCaller"), out var ownsFailure) && ownsFailure);
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (status == -3)
            {
                hostCancellation!.Cancel();
                throw new TaskCanceledException("SYNTHETIC_HOST_SHUTDOWN", null, cancellationToken);
            }
            if (status == -1) throw new HttpRequestException("PROVIDER_BODY_DO_NOT_LOG");
            if (status == -2) throw new TaskCanceledException("PROVIDER_BODY_DO_NOT_LOG");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("PROVIDER_BODY_DO_NOT_LOG") };
        }
    }

    private sealed class ProviderAudit : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(int Id, LogLevel Level, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
    }

    private async Task SeedDueIntentAsync(DateTime now, bool attribution = false)
    {
        await using var write = fixture.Context();
        var mapping = Assert.Single(write.Model.GetEntityTypes(), entity => entity.ClrType.Name == "GoogleAnalyticsOutbox");
        await write.Database.ExecuteSqlRawAsync("DELETE FROM \"GoogleAnalyticsOutbox\"");
        var row = Activator.CreateInstance(mapping.ClrType)!;
        foreach (var (name, value) in new Dictionary<string, object>
        {
            ["QuotationId"] = 7,
            ["EventKey"] = "quotation-7:close_convert_lead:v1",
            ["EventName"] = "close_convert_lead",
            ["ClientId"] = "123.456",
            ["SessionId"] = "789",
            ["Currency"] = "THB",
            ["Value"] = 104m,
            ["OccurredUtc"] = now,
            ["NextAttemptUtc"] = now,
            ["AttemptCount"] = 0,
        })
            mapping.FindProperty(name)!.PropertyInfo!.SetValue(row, value);
        write.Add(row);
        if (attribution)
        {
            mapping.FindProperty("SourceRequestId")!.PropertyInfo!.SetValue(row, 17);
            mapping.FindProperty("SourceJourneyId")!.PropertyInfo!.SetValue(row, Guid.Parse("11111111-2222-3333-4444-555555555555"));
            mapping.FindProperty("UserId")!.PropertyInfo!.SetValue(row, "opaque-7");
        }
        await write.SaveChangesAsync();
    }

    private static async Task<object[]> ClaimAsync(object store, DateTime now)
    {
        var task = await InvokeAsync(store, "ClaimAsync", now, TimeSpan.FromSeconds(60), 20, CancellationToken.None);
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return ((IEnumerable)result!).Cast<object>().ToArray();
    }

    private static async Task<Task> InvokeAsync(object target, string method, params object[] arguments)
    {
        var operation = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(operation);
        var task = (Task)operation.Invoke(target, arguments)!;
        await task;
        return task;
    }

    private static T Property<T>(object row, string property) => (T)row.GetType().GetProperty(property)!.GetValue(row)!;

    private static void ConfigureAnalytics(IWebHostBuilder builder, string? standby, bool enabled)
    {
        // Registration is chosen during Program construction, before late option configuration callbacks.
        builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", standby ?? "");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MALIEV_OBSERVABILITY_STANDBY"] = standby,
                ["GoogleAnalyticsMeasurementProtocol:Enabled"] = enabled ? "true" : "false",
                ["GoogleAnalyticsMeasurementProtocol:MeasurementId"] = "",
                ["GoogleAnalyticsMeasurementProtocol:ApiSecret"] = "",
            }));
    }

    private static void BlockExternalHttp(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(
            http => http.PrimaryHandler = new NoExternalHttpTransport())));

    private sealed class NoExternalHttpTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("External HTTP is blocked in analytics host regression tests.");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("false", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("true", false)]
    public async Task Program_AnalyticsStandbyRegistration_LeavesRealExpiryActive(string? standby, bool expectedWorker)
    {
        var seeded = await fixture.SeedAsync();
        await using (var write = fixture.Context())
        {
            var quotation = await write.Quotations.FindAsync(seeded.Id);
            quotation!.ExpirationDate = new DateTime(2020, 1, 1);
            await write.SaveChangesAsync();
        }
        using var original = fixture.App(new());
        using var app = original.WithWebHostBuilder(builder =>
        {
            ConfigureAnalytics(builder, standby, enabled: false);
            BlockExternalHttp(builder);
        });
        using var bootstrap = app.CreateClient();
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, service => service is ExpiredQuotationWorker);
        // Demonstrate a healthy runtime and actual independent expiry before asserting missing worker behavior.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var read = fixture.Context();
            var quotation = await read.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
            if (quotation.Accepted == false) break;
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal(expectedWorker, hosted.Any(service => service.GetType().Name == "GoogleAnalyticsOutboxWorker"));
    }
}
