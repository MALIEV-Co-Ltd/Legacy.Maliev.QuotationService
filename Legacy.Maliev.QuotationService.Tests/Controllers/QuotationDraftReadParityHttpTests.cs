using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
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

public sealed class QuotationDraftReadParityHttpTests(DraftReadFixture fixture) : IClassFixture<DraftReadFixture>
{
    [Theory]
    [InlineData("%", "ordinary")]
    [InlineData("_", "ordinary")]
    [InlineData("\\", "ordinary")]
    [InlineData("%_\\ชิ้นงาน", "prefixABชิ้นงานsuffix")]
    [InlineData("ไทย%_\\", "ไทยAB")]
    [InlineData("ท้าย\\", "ท้าย")]
    public async Task Comment_search_preserves_source_literal_contains(string literal, string unrelated)
    {
        var customer = fixture.Customer();
        var match = await fixture.SeedAsync(customer, "prefix" + literal + "suffix");
        await fixture.SeedAsync(customer, unrelated);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotations/customers/{customer}?search={Uri.EscapeDataString(literal)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([match.Id], Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
    }

    [Fact]
    public async Task Numeric_search_preserves_exclusive_source_id_or_customer_branch()
    {
        var customer = fixture.Customer();
        var match = await fixture.SeedAsync(customer, "ordinary");
        await fixture.SeedAsync(customer, "invoice " + match.Id);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotations/customers/{customer}?search={match.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([match.Id], Ids(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_customer_search_preserves_scope_and_excludes_comment_only_match(bool scoped)
    {
        var customer = fixture.Customer();
        var first = await fixture.SeedAsync(customer, "ordinary");
        var second = await fixture.SeedAsync(customer, "ordinary");
        await fixture.SeedAsync(fixture.Customer(), "reference " + customer);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        using var response = await client.GetAsync($"{route}?search={customer}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([first.Id, second.Id], Ids(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_detail_cannot_resurrect_missing_or_deleted_database_row(bool deleted)
    {
        var row = await fixture.SeedAsync(fixture.Customer(), "original");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var initial = await client.GetAsync($"/quotations/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var stale = (await initial.Content.ReadFromJsonAsync<QuotationResponse>())!;
        await using (var db = fixture.Context())
        {
            await db.Quotations.Where(value => value.Id == row.Id).ExecuteDeleteAsync();
            Assert.False(await db.Quotations.AnyAsync(value => value.Id == row.Id));
        }
        var id = deleted ? row.Id : int.MaxValue;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            var poison = stale with { Id = id };
            await cache.SetAsync($"quotation:{id}", poison, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(poison, await cache.GetAsync<QuotationResponse>($"quotation:{id}", CancellationToken.None));
        }
        using var response = await client.GetAsync($"/quotations/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Text_case_trim_customer_filter_bounds_and_pascal_null_omission_remain_compatible()
    {
        var customer = fixture.Customer();
        var marker = Guid.NewGuid().ToString("N");
        var match = await fixture.SeedAsync(customer, marker.ToUpperInvariant());
        await fixture.SeedAsync(fixture.Customer(), marker);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotations/customers/{customer}?search=%20{marker}%20&index=0&size=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([match.Id], Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
        Assert.False(json.RootElement.GetProperty("Items")[0].TryGetProperty("InvoiceId", out _));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task Missing_auth_or_permissions_never_discloses_detail(bool authenticated, HttpStatusCode expected)
    {
        var row = await fixture.SeedAsync(fixture.Customer(), "private");
        await using var app = fixture.App();
        using var client = fixture.Client(app, authenticated, permissions: false);
        using var response = await client.GetAsync($"/quotations/{row.Id}");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Customer_mismatch_and_empty_search_remain_notfound()
    {
        var customer = fixture.Customer();
        var row = await fixture.SeedAsync(customer, "private");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var detail = await client.GetAsync($"/quotations/{row.Id}?customerId={fixture.Customer()}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        using var list = await client.GetAsync($"/quotations/customers/{customer}?search={Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detail_after_live_authorized_update_rejects_late_stale_redis_writer(bool resourceScoped)
    {
        var row = await fixture.SeedAsync(fixture.Customer(), "original");
        await using var app = fixture.App(resourceScoped);
        using var client = fixture.Client(app);
        using var initial = await client.GetAsync($"/quotations/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var stale = (await initial.Content.ReadFromJsonAsync<QuotationResponse>())!;
        var before = fixture.LiveCalls;
        fixture.ExpectedLiveResource = resourceScoped ? $"/quotations/{row.Id}" : "global";
        using var update = await client.PutAsJsonAsync($"/quotations/{row.Id}", new UpsertQuotationRequest(row.CustomerId, row.EmployeeId, null,
            row.Period, row.ExpirationDate, 200m, 14m, 214m, null, row.CurrencyId, "fresh", null, null, null, null));
        Assert.Equal(fixture.ExpectedLiveResource, fixture.ReceivedLiveResource);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(before + 1, fixture.LiveCalls);
        await using (var db = fixture.Context())
        {
            var persisted = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
            Assert.Equal(214m, persisted.Total);
            Assert.Equal("fresh", persisted.Comment);
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"quotation:{row.Id}", stale, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(stale, await cache.GetAsync<QuotationResponse>($"quotation:{row.Id}", CancellationToken.None));
        }
        using var detail = await client.GetAsync($"/quotations/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var current = (await detail.Content.ReadFromJsonAsync<QuotationResponse>())!;
        Assert.Equal(214m, current.Total);
        Assert.Equal("fresh", current.Comment);
    }

    private static int[] Ids(JsonDocument json) => json.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetInt32()).ToArray();
}

public sealed class DraftReadFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    private int customer = 1000000;
    public int LiveCalls;
    public string? ExpectedLiveResource;
    public string? ReceivedLiveResource;
    public int Customer() => Interlocked.Increment(ref customer);
    private string Requests => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "draft_requests" }.ConnectionString;
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE draft_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Context();
        await db.Database.MigrateAsync();
        await using var requests = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Requests).Options);
        await requests.Database.MigrateAsync();
    }
    public async Task<Quotation> SeedAsync(int customerId, string comment)
    {
        await using var db = Context();
        var row = new Quotation
        {
            CustomerId = customerId,
            EmployeeId = 41,
            CurrencyId = 1,
            Period = 30,
            ExpirationDate = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(30), DateTimeKind.Unspecified),
            Subtotal = 100m,
            Vat = 7m,
            Total = 107m,
            Comment = comment,
            CreatedDate = new DateTime(2026, 10, 1),
            ModifiedDate = new DateTime(2026, 10, 1)
        };
        db.Quotations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
    public WebApplicationFactory<Program> App(bool resourceScoped = false) => new Factory(this, resourceScoped);
    public HttpClient Client(WebApplicationFactory<Program> app, bool authenticated = true, bool permissions = true)
    {
        var client = app.CreateClient();
        if (!authenticated) return client;
        var claims = new List<Claim> { new("sub", "employee-draft-fixture"), new("identity_kind", "employee"), new("role", "Employee") };
        if (permissions) foreach (var permission in new[] { "legacy.customer-quotations.read", "legacy.quotations.read", "legacy.quotations.update" }) claims.Add(new("permissions", permission));
        var jwt = new JwtSecurityToken("https://draft-auth.example", "draft-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }
    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        key.Dispose();
    }
    private sealed class Factory(DraftReadFixture fixture, bool resourceScoped) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            if (resourceScoped) builder.UseSetting("Features:ResourceScopedAuthEnabled", "true");
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = fixture.postgres.GetConnectionString(),
                ["ConnectionStrings:QuotationRequestDbContext"] = fixture.Requests,
                ["ConnectionStrings:redis"] = $"{fixture.redis.Hostname}:{fixture.redis.GetMappedPublicPort(6379)}",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.key.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://draft-auth.example",
                ["Jwt:Audience"] = "draft-services",
                ["IAM:LivePermissionChecks:Credential"] = "synthetic-draft-live-check",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://draft-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Transport(fixture));
            });
        }
    }
    private sealed class Transport(DraftReadFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-draft-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("employee-draft-fixture", json.RootElement.GetProperty("principalId").GetString());
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            var allowed = json.RootElement.GetProperty("permissionId").GetString() == "legacy.quotations.update";
            if (allowed)
            {
                Assert.NotNull(fixture.ExpectedLiveResource);
                fixture.ReceivedLiveResource = json.RootElement.GetProperty("resourcePath").GetString();
                Interlocked.Increment(ref fixture.LiveCalls);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
}
