using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
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

public sealed class QuotationDraftAggregateHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    private static UpsertQuotationRequest Draft() => new(101, 41, null, 30,
        new DateTime(2035, 1, 1), 100.50m, 7.04m, 107.54m, 3.02m, 1, "น้ำ", null, null, null, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_line_link_locations_decimal_graph_and_sequential_replay(bool keyed)
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var key = Guid.NewGuid().ToString("N");
        using var root = await Send(client, "/quotations", Draft(), keyed ? key + ":root" : null);
        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var quotation = (await root.Content.ReadFromJsonAsync<QuotationResponse>())!;
        Assert.Equal(104.52m, quotation.QuotedAmount);
        Assert.Null(quotation.Accepted);
        Assert.NotNull(quotation.CreatedDate);
        Assert.NotNull(quotation.ModifiedDate);
        using var rootJson = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
        Assert.False(rootJson.RootElement.TryGetProperty("InvoiceId", out _));
        await Follow(client, root, quotation.Id);
        var itemRequest = new UpsertQuotationOrderItemRequest(quotation.Id, 901, "น้ำ", 2, 50.25m);
        using var item = await Send(client, "/quotations/orderitems", itemRequest, keyed ? key + ":line" : null);
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var line = (await item.Content.ReadFromJsonAsync<QuotationOrderItemResponse>())!;
        Assert.Equal(100.50m, line.Subtotal);
        Assert.Equal(quotation.Id, line.QuotationId);
        Assert.Equal(901, line.OrderId);
        await Follow(client, item, line.Id);
        var linkRoute = $"/quotations/{quotation.Id}/orders/901";
        using var link = await Send(client, linkRoute, null, keyed ? key + ":link" : null);
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        var order = (await link.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>())!;
        Assert.Equal(901, order.OrderId);
        Assert.Equal(quotation.Id, order.QuotationId);
        await Follow(client, link, order.Id);
        if (keyed)
        {
            foreach (var tuple in new[] { ("/quotations", (object?)Draft(), key + ":root", root),
                ("/quotations/orderitems", (object?)itemRequest, key + ":line", item), (linkRoute, (object?)null, key + ":link", link) })
            {
                using var replay = await Send(client, tuple.Item1, tuple.Item2, tuple.Item3);
                Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
                Assert.Equal(tuple.Item4.Headers.Location, replay.Headers.Location);
                Assert.Equal(await tuple.Item4.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            }
        }
        await using var db = fixture.Context();
        Assert.Equal(1, await db.Quotations.CountAsync(value => value.Id == quotation.Id));
        Assert.Equal(1, await db.OrderItems.CountAsync(value => value.QuotationId == quotation.Id));
        Assert.Equal(1, await db.OrderLinks.CountAsync(value => value.QuotationId == quotation.Id));
        Assert.Equal(100.50m, (await db.OrderItems.SingleAsync(value => value.Id == line.Id)).Subtotal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Source_zero_link_identifiers_return_400_without_any_child_write(bool zeroQuotation, bool zeroOrder)
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var root = await Send(client, "/quotations", Draft(), null);
        Assert.Equal(HttpStatusCode.Created, root.StatusCode);
        var id = (await root.Content.ReadFromJsonAsync<QuotationResponse>())!.Id;
        using var response = await Send(client, $"/quotations/{(zeroQuotation ? 0 : id)}/orders/{(zeroOrder ? 0 : 902)}", null, null);
        await using var db = fixture.Context();
        var persistedLinks = await db.OrderLinks.CountAsync(value => value.QuotationId == id);
        Assert.True(await db.Quotations.AnyAsync(value => value.Id == id));
        Assert.Equal((0, HttpStatusCode.BadRequest), (persistedLinks, response.StatusCode));
        Assert.Contains("Quotation id and order id are required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_positive_parent_link_remains_404_without_write()
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await Send(client, "/quotations/2147483647/orders/903", null, null);
        await using var db = fixture.Context();
        Assert.False(await db.OrderLinks.AnyAsync(value => value.QuotationId == int.MaxValue));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("root", false, HttpStatusCode.Unauthorized)]
    [InlineData("line", false, HttpStatusCode.Unauthorized)]
    [InlineData("link", false, HttpStatusCode.Unauthorized)]
    [InlineData("root", true, HttpStatusCode.Forbidden)]
    [InlineData("line", true, HttpStatusCode.Forbidden)]
    [InlineData("link", true, HttpStatusCode.Forbidden)]
    public async Task Authentication_and_actual_live_permission_denial_have_no_writes(string step, bool authenticated, HttpStatusCode expected)
    {
        await using var app = fixture.App(allowed: false);
        using var client = fixture.Client(app, authenticated);
        await using var before = fixture.Context();
        var roots = await before.Quotations.CountAsync();
        var lines = await before.OrderItems.CountAsync();
        var links = await before.OrderLinks.CountAsync();
        var route = step == "root" ? "/quotations" : step == "line" ? "/quotations/orderitems" : "/quotations/123/orders/904";
        object? body = step == "root" ? Draft() : step == "line" ? new UpsertQuotationOrderItemRequest(123, 904, "private", 2, 50.25m) : null;
        using var response = await Send(client, route, body, null);
        await using var after = fixture.Context();
        Assert.Equal(roots, await after.Quotations.CountAsync());
        Assert.Equal(lines, await after.OrderItems.CountAsync());
        Assert.Equal(links, await after.OrderLinks.CountAsync());
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Real_child_insert_failure_preserves_already_committed_root_without_child()
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await Send(client, "/quotations", Draft(), null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = (await response.Content.ReadFromJsonAsync<QuotationResponse>())!;
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("CREATE OR REPLACE FUNCTION reject_aggregate_child() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Description\" = 'aggregate-child-fault' THEN RAISE EXCEPTION 'synthetic child failure' USING ERRCODE='P0001'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_aggregate_child BEFORE INSERT ON \"OrderItem\" FOR EACH ROW EXECUTE FUNCTION reject_aggregate_child();");
        try
        {
            using var failed = await Send(client, "/quotations/orderitems", new UpsertQuotationOrderItemRequest(root.Id, 905, "aggregate-child-fault", 2, 50.25m), null);
            await using var fresh = fixture.Context();
            Assert.True(await fresh.Quotations.AnyAsync(value => value.Id == root.Id));
            Assert.False(await fresh.OrderItems.AnyAsync(value => value.QuotationId == root.Id));
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_aggregate_child ON \"OrderItem\"; DROP FUNCTION reject_aggregate_child();"); }
    }

    [Fact]
    public async Task Child_update_conflict_and_delete_preserve_parent_scalar_version()
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var created = await Send(client, "/quotations", Draft(), null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var root = (await created.Content.ReadFromJsonAsync<QuotationResponse>())!;
        await using var read = fixture.Context();
        var parent = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        var originalVersion = parent.ModifiedDate;
        Assert.NotNull(originalVersion);

        using var inserted = await Send(client, "/quotations/orderitems",
            new UpsertQuotationOrderItemRequest(root.Id, 907, "initial line", 2, 50.25m), null);
        Assert.Equal(HttpStatusCode.Created, inserted.StatusCode);
        var line = (await inserted.Content.ReadFromJsonAsync<QuotationOrderItemResponse>())!;
        Assert.Equal(originalVersion, (await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id)).ModifiedDate);
        using var currentResponse = await client.GetAsync($"/quotations/orderitems/{line.Id}");
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        var current = (await currentResponse.Content.ReadFromJsonAsync<QuotationOrderItemResponse>())!;
        Assert.NotNull(current.ModifiedDate);
        var expected = DateTime.SpecifyKind(current.ModifiedDate.Value, DateTimeKind.Utc)
            .ToString("O", CultureInfo.InvariantCulture);
        var updated = new UpsertQuotationOrderItemRequest(root.Id, 908, "updated line", 3, 20.25m);
        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/orderitems/{line.Id}"))
        {
            request.Headers.Add("X-Expected-Modified-Date", expected);
            request.Content = JsonContent.Create(updated);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        var persisted = await read.OrderItems.AsNoTracking().SingleAsync(value => value.Id == line.Id);
        Assert.Equal("updated line", persisted.Description);
        Assert.Equal(908, persisted.OrderId);
        Assert.Equal(3, persisted.Quantity);
        Assert.Equal(20.25m, persisted.UnitPrice);
        Assert.Equal(60.75m, persisted.Subtotal);
        Assert.NotEqual(current.ModifiedDate, persisted.ModifiedDate);
        using (var stale = new HttpRequestMessage(HttpMethod.Put, $"/quotations/orderitems/{line.Id}"))
        {
            stale.Headers.Add("X-Expected-Modified-Date", expected);
            stale.Content = JsonContent.Create(updated with { Description = "stale overwrite", Quantity = 99 });
            using var conflict = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        var unchanged = await read.OrderItems.AsNoTracking().SingleAsync(value => value.Id == line.Id);
        Assert.Equal(persisted.ModifiedDate, unchanged.ModifiedDate);
        Assert.Equal("updated line", unchanged.Description);
        Assert.Equal(60.75m, unchanged.Subtotal);
        using var deleted = await client.DeleteAsync($"/quotations/orderitems/{line.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await read.OrderItems.AnyAsync(value => value.Id == line.Id));
        using var missing = await client.GetAsync($"/quotations/orderitems/{line.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var finalParent = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        Assert.Equal(originalVersion, finalParent.ModifiedDate);
        Assert.Equal(parent.Total, finalParent.Total);
        Assert.Equal(parent.Subtotal, finalParent.Subtotal);
        Assert.Equal(parent.Accepted, finalParent.Accepted);
        Assert.Equal(parent.InvoiceId, finalParent.InvoiceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Child_update_and_delete_live_denial_preserve_existing_line(bool delete)
    {
        await using var allowed = fixture.App();
        using var creator = fixture.Client(allowed);
        using var rootResponse = await Send(creator, "/quotations", Draft(), null);
        Assert.Equal(HttpStatusCode.Created, rootResponse.StatusCode);
        var root = (await rootResponse.Content.ReadFromJsonAsync<QuotationResponse>())!;
        using var lineResponse = await Send(creator, "/quotations/orderitems",
            new UpsertQuotationOrderItemRequest(root.Id, 909, "protected line", 2, 10.25m), null);
        Assert.Equal(HttpStatusCode.Created, lineResponse.StatusCode);
        var line = (await lineResponse.Content.ReadFromJsonAsync<QuotationOrderItemResponse>())!;
        await using var read = fixture.Context();
        var before = await read.OrderItems.AsNoTracking().SingleAsync(value => value.Id == line.Id);
        await using var denied = fixture.App(allowed: false);
        using var client = fixture.Client(denied);
        using var request = new HttpRequestMessage(delete ? HttpMethod.Delete : HttpMethod.Put,
            $"/quotations/orderitems/{line.Id}");
        if (!delete) request.Content = JsonContent.Create(new UpsertQuotationOrderItemRequest(root.Id, 909, "denied change", 99, 10.25m));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await read.OrderItems.AsNoTracking().SingleAsync(value => value.Id == line.Id);
        Assert.Equal(before.ModifiedDate, after.ModifiedDate);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.Subtotal, after.Subtotal);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string route, object? body, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    private static async Task Follow(HttpClient client, HttpResponseMessage created, int id)
    {
        Assert.NotNull(created.Headers.Location);
        using var response = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, json.RootElement.GetProperty("Id").GetInt32());
    }
}

public sealed class DraftAggregateFixture : IAsyncLifetime
{
    private Infrastructure.DisposableContainerPair? containers;
    private PostgreSqlContainer postgres => (PostgreSqlContainer)containers!.First;
    private IContainer redis => containers!.Second;
    private readonly RSA key = RSA.Create(2048);
    private static readonly string[] Permissions = ["legacy.quotations.create", "legacy.quotations.read", "legacy.customer-quotations.read", "legacy.quotation-lines.write", "legacy.quotation-lines.read", "legacy.quotation-lines.delete", "legacy.quotation-orders.write", "legacy.quotation-orders.read"];
    private string Requests => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())) { Database = "aggregate_requests" }.ConnectionString;
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())).Options);
    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation100-QuotationDraftAggregateHttpTests",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint).WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build());
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE aggregate_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Context();
        await db.Database.MigrateAsync();
        await using var requests = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Requests).Options);
        await requests.Database.MigrateAsync();
    }
    public WebApplicationFactory<Program> App(bool allowed = true) => new Factory(this, allowed);
    public HttpClient Client(WebApplicationFactory<Program> app, bool authenticated = true)
    {
        var client = app.CreateClient();
        if (!authenticated) return client;
        var claims = new List<Claim> { new("sub", "employee-aggregate-fixture"), new("identity_kind", "employee"), new("role", "Employee") };
        claims.AddRange(Permissions.Select(permission => new Claim("permissions", permission)));
        var jwt = new JwtSecurityToken("https://aggregate-auth.example", "aggregate-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }
    public async Task DisposeAsync()
    {
        if (containers is not null) await containers.DisposeAsync();
        key.Dispose();
    }
    private sealed class Factory(DraftAggregateFixture fixture, bool allowed) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = Infrastructure.DisposablePostgresConnectionPolicy.Isolate(fixture.postgres.GetConnectionString()),
                ["ConnectionStrings:QuotationRequestDbContext"] = fixture.Requests,
                ["ConnectionStrings:redis"] = $"{fixture.redis.Hostname}:{fixture.redis.GetMappedPublicPort(6379)}",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.key.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://aggregate-auth.example",
                ["Jwt:Audience"] = "aggregate-services",
                ["IAM:LivePermissionChecks:Credential"] = "synthetic-aggregate-live-check",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://aggregate-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Transport(allowed));
            });
        }
    }
    private sealed class Transport(bool allowed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-aggregate-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("employee-aggregate-fixture", json.RootElement.GetProperty("principalId").GetString());
            Assert.Contains(json.RootElement.GetProperty("permissionId").GetString(), Permissions);
            Assert.Equal("global", json.RootElement.GetProperty("resourcePath").GetString());
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
}
