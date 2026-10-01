using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationFinancialOutcomeReadbackHttpTests(FinancialOutcomeFixture fixture) : IClassFixture<FinancialOutcomeFixture>
{
    private static readonly Guid Journey = Guid.Parse("cb96298e-e4ab-471b-a4c2-f23627f28576");
    private static UpsertQuotationRequest Draft(decimal total = 107.54m, decimal? tax = 3.02m, int currency = 764,
        int? sourceRequest = null, Guid? journey = null) => new(101, 41, null, 30, new DateTime(2035, 1, 1),
        100.50m, 7.04m, total, tax, currency, "private น้ำ", null, null, null, true, sourceRequest, journey);

    [Fact]
    public async Task Creation_update_acceptance_replay_readback_preserves_provenance_and_literal_financial_oracle()
    {
        var day = fixture.NextDay();
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var draft = Draft(sourceRequest: 912, journey: Journey);
        var root = await Create(client, draft, day.AddHours(1));
        Assert.Null(root.Accepted);
        fixture.SetTime(day.AddHours(2));
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{root.Id}")
        {
            Content = JsonContent.Create(draft with { Accepted = null, SourceRequestId = 999, SourceJourneyId = Guid.NewGuid() })
        };
        updateRequest.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(root.ModifiedDate!.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
        using var update = await client.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        await using (var db = fixture.Context())
        {
            var persisted = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
            Assert.Equal(912, persisted.SourceRequestId);
            Assert.Equal(Journey, persisted.SourceJourneyId);
            Assert.False(await db.AcceptedOutcomes.AnyAsync(value => value.QuotationId == root.Id));
        }
        await Accept(client, root.Id, day.AddHours(3));
        long outcomeId;
        await using (var db = fixture.Context())
        {
            var persisted = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
            var outcome = await db.AcceptedOutcomes.AsNoTracking().SingleAsync(value => value.QuotationId == root.Id);
            Assert.True(persisted.Accepted);
            Assert.Equal(912, outcome.SourceRequestId);
            Assert.Equal(Journey, outcome.SourceJourneyId);
            Assert.Equal("employee", outcome.AcceptanceOrigin);
            Assert.Equal($"quotation-{root.Id}:accepted:v1", outcome.EventKey);
            Assert.Equal(DateTime.SpecifyKind(day.AddHours(3), DateTimeKind.Unspecified), outcome.AcceptedUtc);
            outcomeId = outcome.Id;
        }
        await Accept(client, root.Id, day.AddHours(4));
        await using (var db = fixture.Context())
        {
            var outcome = await db.AcceptedOutcomes.AsNoTracking().SingleAsync(value => value.QuotationId == root.Id);
            Assert.Equal(outcomeId, outcome.Id);
            Assert.Equal(DateTime.SpecifyKind(day.AddHours(3), DateTimeKind.Unspecified), outcome.AcceptedUtc);
        }
        var checks = fixture.ReadChecks;
        var readback = await Read(client, day, day.AddDays(1));
        Assert.Equal(checks + 1, fixture.ReadChecks);
        Assert.Equal("unavailable", readback.TechnicalConversionAvailability);
        Assert.Equal("unavailable", readback.QualifiedCustomerAvailability);
        Assert.Equal("unavailable", readback.RevenueAvailability);
        var result = Assert.Single(readback.Days);
        Assert.Equal(day, result.DayUtc);
        Assert.Equal((1, 1, 1, 1, 0, 0), (result.PersistedQuotationCount, result.AcceptedQuotationCount,
            result.SourceAttributedPersistedQuotationCount, result.SourceAttributedAcceptedQuotationCount,
            result.UnattributedPersistedQuotationCount, result.UnattributedAcceptedQuotationCount));
        Assert.Equal(new AcceptedQuotedAmountByCurrency(764, 104.52m, 1), Assert.Single(result.AcceptedQuotedAmountsByCurrency));
    }

    [Fact]
    public async Task Half_open_window_orders_days_and_currencies_and_counts_partial_keys_as_unattributed()
    {
        var day = fixture.NextDay();
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var before = await Create(client, Draft(10m, 0m, 392), day.AddSeconds(-1));
        await Accept(client, before.Id, day);
        var attributed = await Create(client, Draft(sourceRequest: 913, journey: Journey), day.AddHours(1));
        await Accept(client, attributed.Id, day.AddHours(2));
        var idOnly = await Create(client, Draft(50m, 0m, 840, sourceRequest: 914), day.AddDays(1).AddHours(1));
        await Accept(client, idOnly.Id, day.AddDays(1).AddHours(2));
        var journeyOnly = await Create(client, Draft(25m, 0m, 764, journey: Journey), day.AddDays(1).AddHours(3));
        await Accept(client, journeyOnly.Id, day.AddDays(1).AddHours(4));
        await Create(client, Draft(30m, 0m, 764), day.AddDays(1).AddHours(5));
        var upper = await Create(client, Draft(300m, 0m, 392), day.AddDays(2));
        await Accept(client, upper.Id, day.AddDays(2));
        var result = await Read(client, day, day.AddDays(2));
        Assert.Equal(day, result.FromUtc);
        Assert.Equal(day.AddDays(2), result.ToUtc);
        Assert.Collection(result.Days,
            first =>
            {
                Assert.Equal(day, first.DayUtc);
                Assert.Equal((1, 2, 1, 1, 0, 1), (first.PersistedQuotationCount, first.AcceptedQuotationCount,
                    first.SourceAttributedPersistedQuotationCount, first.SourceAttributedAcceptedQuotationCount,
                    first.UnattributedPersistedQuotationCount, first.UnattributedAcceptedQuotationCount));
                Assert.Equal([new AcceptedQuotedAmountByCurrency(392, 10m, 1), new(764, 104.52m, 1)], first.AcceptedQuotedAmountsByCurrency);
            },
            second =>
            {
                Assert.Equal(day.AddDays(1), second.DayUtc);
                Assert.Equal((3, 2, 0, 0, 3, 2), (second.PersistedQuotationCount, second.AcceptedQuotationCount,
                    second.SourceAttributedPersistedQuotationCount, second.SourceAttributedAcceptedQuotationCount,
                    second.UnattributedPersistedQuotationCount, second.UnattributedAcceptedQuotationCount));
                Assert.Equal([new AcceptedQuotedAmountByCurrency(764, 25m, 1), new(840, 50m, 1)], second.AcceptedQuotedAmountsByCurrency);
            });
    }

    [Fact]
    public async Task Source_current_value_join_reflects_edits_and_omits_deleted_root_without_deleting_outcome()
    {
        var day = fixture.NextDay();
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var root = await Create(client, Draft(sourceRequest: 915, journey: Journey), day.AddHours(1));
        await Accept(client, root.Id, day.AddHours(2));
        fixture.SetTime(day.AddHours(3));
        using var edit = await client.PutAsJsonAsync($"/quotations/{root.Id}", Draft(250m, 10m, 840) with { Accepted = true });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        var readback = await Read(client, day, day.AddDays(1));
        var result = Assert.Single(readback.Days);
        Assert.Equal(1, result.AcceptedQuotationCount);
        Assert.Equal(1, result.SourceAttributedAcceptedQuotationCount);
        Assert.Equal(new AcceptedQuotedAmountByCurrency(840, 240m, 1), Assert.Single(result.AcceptedQuotedAmountsByCurrency));
        fixture.SetTime(day.AddHours(4));
        using var delete = await client.DeleteAsync($"/quotations/{root.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using var db = fixture.Context();
        Assert.False(await db.Quotations.AnyAsync(value => value.Id == root.Id));
        var outcome = await db.AcceptedOutcomes.AsNoTracking().SingleAsync(value => value.QuotationId == root.Id);
        Assert.Equal(915, outcome.SourceRequestId);
        Assert.Equal(Journey, outcome.SourceJourneyId);
        Assert.Equal(DateTime.SpecifyKind(day.AddHours(2), DateTimeKind.Unspecified), outcome.AcceptedUtc);
        Assert.Empty((await Read(client, day, day.AddDays(1))).Days);
    }

    [Fact]
    public async Task Null_computed_quote_amount_counts_acceptance_without_invented_total_fallback()
    {
        var day = fixture.NextDay();
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var root = await Create(client, Draft(20m, null, 764), day.AddHours(1));
        Assert.Null(root.QuotedAmount);
        await Accept(client, root.Id, day.AddHours(2));
        var result = Assert.Single((await Read(client, day, day.AddDays(1))).Days);
        Assert.Equal(1, result.AcceptedQuotationCount);
        Assert.Empty(result.AcceptedQuotedAmountsByCurrency);
    }

    [Theory]
    [InlineData("anonymous", true, HttpStatusCode.Unauthorized)]
    [InlineData("customer", true, HttpStatusCode.Forbidden)]
    [InlineData("service", true, HttpStatusCode.Forbidden)]
    [InlineData("employee", false, HttpStatusCode.Forbidden)]
    public async Task Normal_signed_actor_and_live_permission_denials_leave_graph_unchanged(string actor, bool allowed, HttpStatusCode expected)
    {
        var day = fixture.NextDay();
        await using var app = fixture.App(allowed);
        using var client = fixture.Client(app, actor);
        await using var db = fixture.Context();
        var roots = await db.Quotations.CountAsync();
        var outcomes = await db.AcceptedOutcomes.CountAsync();
        var checks = fixture.ReadChecks;
        using var response = await client.GetAsync(Route(day, day.AddDays(1)));
        Assert.Equal(expected, response.StatusCode);
        if (actor == "employee") Assert.Equal(checks + 1, fixture.ReadChecks);
        Assert.Equal(roots, await db.Quotations.CountAsync());
        Assert.Equal(outcomes, await db.AcceptedOutcomes.CountAsync());
        Assert.DoesNotContain("private", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("equal", HttpStatusCode.BadRequest)]
    [InlineData("reverse", HttpStatusCode.BadRequest)]
    [InlineData("over31", HttpStatusCode.BadRequest)]
    [InlineData("exact31", HttpStatusCode.OK)]
    [InlineData("unspecified", HttpStatusCode.BadRequest)]
    [InlineData("malformed", HttpStatusCode.BadRequest)]
    public async Task Window_admission_matches_source_utc_and_inclusive_31_day_limit(string scenario, HttpStatusCode expected)
    {
        var day = fixture.NextDay();
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var to = scenario switch { "equal" => day, "reverse" => day.AddSeconds(-1), "over31" => day.AddDays(31).AddSeconds(1), "exact31" => day.AddDays(31), _ => day.AddDays(1) };
        var route = Route(day, to);
        if (scenario == "unspecified") route = "/quotations/outcomes/readback?fromUtc=2026-01-01T00:00:00&toUtc=2026-01-02T00:00:00";
        if (scenario == "malformed") route = "/quotations/outcomes/readback?fromUtc=not-a-date&toUtc=2026-01-02T00:00:00Z";
        using var response = await client.GetAsync(route);
        Assert.Equal(expected, response.StatusCode);
    }

    private async Task<QuotationResponse> Create(HttpClient client, UpsertQuotationRequest draft, DateTime created)
    {
        fixture.SetTime(created);
        using var response = await client.PostAsJsonAsync("/quotations", draft);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QuotationResponse>())!;
    }
    private async Task Accept(HttpClient client, int id, DateTime accepted)
    {
        fixture.SetTime(accepted);
        using var response = await client.PutAsJsonAsync($"/quotations/{id}/decision", new QuotationDecisionRequest(true, EmployeeInitiated: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<QuotationDecisionResponse>())!;
        Assert.Equal(QuotationDecisionStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalOrders);
    }
    private static async Task<QuotationOutcomeReadback> Read(HttpClient client, DateTime from, DateTime to)
    {
        using var response = await client.GetAsync(Route(from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("unavailable", json.RootElement.GetProperty("RevenueAvailability").GetString());
        Assert.False(json.RootElement.TryGetProperty("revenueAvailability", out _));
        foreach (var name in new[] { "QuotationId", "CustomerId", "EmployeeId", "InvoiceId", "OrderId", "SourceRequestId", "SourceJourneyId", "EventKey", "AcceptanceOrigin", "FirstName", "LastName", "Email", "TelephoneNumber", "TaxIdentification", "Comment", "private", "cb96298e" })
            Assert.DoesNotContain(name, body, StringComparison.OrdinalIgnoreCase);
        return (await response.Content.ReadFromJsonAsync<QuotationOutcomeReadback>())!;
    }
    private static string Route(DateTime from, DateTime to) => "/quotations/outcomes/readback?fromUtc=" + Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))
        + "&toUtc=" + Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture));
}

public sealed class FinancialOutcomeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    private readonly Clock clock = new();
    private int day;
    public int ReadChecks;
    private static readonly string[] Permissions = ["legacy.quotations.create", "legacy.quotations.read", "legacy.quotations.update", "legacy.quotations.delete", "legacy.customer-quotations.read"];
    private string Requests => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "financial_requests" }.ConnectionString;
    public DateTime NextDay() => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(40 * Interlocked.Increment(ref day));
    public void SetTime(DateTime value) => clock.Set(value);
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE financial_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Context();
        await db.Database.MigrateAsync();
        await using var requests = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Requests).Options);
        await requests.Database.MigrateAsync();
    }
    public WebApplicationFactory<Program> App(bool allowed = true) => new Factory(this, allowed);
    public HttpClient Client(WebApplicationFactory<Program> app, string actor = "employee")
    {
        var client = app.CreateClient();
        if (actor == "anonymous") return client;
        var subject = actor == "service" ? "service:financial-fixture" : actor + "-financial-fixture";
        var claims = new List<Claim> { new("sub", subject), new("identity_kind", actor), new("role", actor == "employee" ? "Employee" : actor == "customer" ? "Customer" : "Service") };
        claims.AddRange(Permissions.Select(permission => new Claim("permissions", permission)));
        var jwt = new JwtSecurityToken("https://financial-auth.example", "financial-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }
    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        key.Dispose();
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        public void Set(DateTime value) => Interlocked.Exchange(ref ticks, value.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }
    private sealed class Factory(FinancialOutcomeFixture fixture, bool allowed) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = fixture.postgres.GetConnectionString(),
                ["ConnectionStrings:QuotationRequestDbContext"] = fixture.Requests,
                ["ConnectionStrings:redis"] = $"{fixture.redis.Hostname}:{fixture.redis.GetMappedPublicPort(6379)}",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.key.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://financial-auth.example",
                ["Jwt:Audience"] = "financial-services",
                ["IAM:LivePermissionChecks:Credential"] = "synthetic-financial-live-check",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(fixture.clock);
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://financial-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Transport(fixture, allowed));
            });
        }
    }
    private sealed class Transport(FinancialOutcomeFixture fixture, bool allowed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-financial-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Contains(json.RootElement.GetProperty("principalId").GetString(), new[] { "employee-financial-fixture", "customer-financial-fixture", "service:financial-fixture" });
            var permission = json.RootElement.GetProperty("permissionId").GetString();
            Assert.Contains(permission, Permissions);
            Assert.Equal("global", json.RootElement.GetProperty("resourcePath").GetString());
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            if (permission == "legacy.quotations.read") Interlocked.Increment(ref fixture.ReadChecks);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
}
