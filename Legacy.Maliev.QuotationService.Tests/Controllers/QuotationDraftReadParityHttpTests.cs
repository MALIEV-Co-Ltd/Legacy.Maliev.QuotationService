using System.Collections.Concurrent;
using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationDraftReadParityHttpTests(DraftReadFixture fixture) : IClassFixture<DraftReadFixture>
{
    [Fact]
    public async Task Empty_database_keeps_source_notfound_for_default_filtered_and_distant_pages()
    {
        await using var existing = fixture.Context();
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetDbConnection().ConnectionString)
        {
            Database = "empty_page_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(connection).Options;
        await using var database = new QuotationDbContext(options);
        Assert.False(await database.Database.CanConnectAsync());
        Exception? primaryFailure = null;
        try
        {
            await database.Database.MigrateAsync();
            await using (var original = fixture.App())
            await using (var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<QuotationDbContext>>();
                services.AddSingleton(options);
            })))
            {
                using var client = fixture.Client(app);
                using (var response = await client.GetAsync("/quotations"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using (var response = await client.GetAsync("/quotations?search=absent"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using (var response = await client.GetAsync("/quotations/customers/41"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using (var response = await client.GetAsync("/quotations?index=2147483647&size=250"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                await database.Database.EnsureDeletedAsync();
                Assert.False(await database.Database.CanConnectAsync());
            }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                primaryFailure.Data["EmptyPageDatabaseCleanupFailure"] = cleanup;
            }
        }
    }

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
        using var response = await client.GetAsync($"/quotations/customers/{customer}?search=%20{marker}%20&index=0&size=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal([match.Id], Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
        Assert.False(json.RootElement.GetProperty("Items")[0].TryGetProperty("InvoiceId", out _));
        using var zero = await client.GetAsync($"/quotations/customers/{customer}?search=%20{marker}%20&index=0&size=0");
        Assert.Equal(HttpStatusCode.NotFound, zero.StatusCode);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 51)]
    [InlineData(true, 51)]
    [InlineData(false, 261)]
    [InlineData(true, 261)]
    public async Task Nonpositive_size_preserves_count_first_source_status_and_private_failure(bool scoped, int count)
    {
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        for (var i = 0; i < count; i++) await fixture.SeedAsync(customer, marker);
        await fixture.SeedAsync(customer, Guid.NewGuid().ToString("N"));
        await fixture.SeedAsync(fixture.Customer(), scoped ? marker : Guid.NewGuid().ToString("N"));
        await using var snapshot = fixture.Context();
        var before = JsonSerializer.Serialize(await snapshot.Quotations.AsNoTracking()
            .Where(row => row.Comment == marker).OrderBy(row => row.Id).ToArrayAsync());
        var probe = new QuotationCollectionCommandProbe(marker, "Quotation");
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<QuotationDbContext>(options => options.AddInterceptors(probe))));
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        foreach (var descending in new[] { false, true })
        {
            var sort = descending ? "QuotationId_Descending" : "QuotationId_Ascending";
            foreach (var size in new[] { int.MinValue, -1, 0 })
            {
                foreach (var index in new int?[] { null, int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                {
                    probe.Commands.Clear();
                    var query = $"{route}?search={marker}&sort={sort}&size={size}"
                        + (index is null ? "" : $"&index={index}");
                    using var response = await client.GetAsync(query);
                    Assert.Equal("count", Assert.Single(probe.Commands));
                    if (count == 0 || size == 0)
                    {
                        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                        continue;
                    }
                    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                    var body = await response.Content.ReadAsStringAsync();
                    using var json = JsonDocument.Parse(body);
                    Assert.Equal(["details", "error", "statusCode", "traceId"],
                        json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
                    Assert.Equal("An internal server error occurred", json.RootElement.GetProperty("error").GetString());
                    Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
                    Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
                    Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
                    Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
                    Assert.DoesNotContain("QuotationCollectionReadException", body, StringComparison.Ordinal);
                    Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
                }
                probe.Commands.Clear();
                using var anonymous = fixture.Client(app, authenticated: false);
                using var unauthenticated = await anonymous.GetAsync($"{route}?search={marker}&size={size}");
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
                Assert.Empty(probe.Commands);
                using var denied = fixture.Client(app, permissions: false);
                using var forbidden = await denied.GetAsync($"{route}?search={marker}&size={size}");
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.Empty(probe.Commands);
            }
        }
        var after = JsonSerializer.Serialize(await snapshot.Quotations.AsNoTracking()
            .Where(row => row.Comment == marker).OrderBy(row => row.Id).ToArrayAsync());
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(false, int.MinValue)]
    [InlineData(true, int.MinValue)]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task Nonpositive_size_preserves_count_failure_before_classification(bool scoped, int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        await fixture.SeedAsync(customer, marker);
        var probe = new QuotationCollectionCommandProbe(marker, "Quotation") { FailCount = true };
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<QuotationDbContext>(options => options.AddInterceptors(probe))));
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        using var response = await client.GetAsync($"{route}?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("count", Assert.Single(probe.Commands));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("The request cannot be processed.", json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task Nonpositive_size_preserves_caller_cancellation_at_count(bool scoped, int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        await fixture.SeedAsync(customer, marker);
        using var cancellation = new CancellationTokenSource();
        var probe = new QuotationCollectionCommandProbe(marker, "Quotation") { CancelCount = cancellation };
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<QuotationDbContext>(options => options.AddInterceptors(probe))));
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync($"{route}?search={marker}&size={size}", cancellation.Token));
        Assert.Equal("count", Assert.Single(probe.Commands));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 51)]
    [InlineData(true, 51)]
    [InlineData(false, 261)]
    [InlineData(true, 261)]
    public async Task Omitted_size_uses_filtered_count_without_default_or_cap(bool scoped, int count)
    {
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        var rows = new List<int>();
        for (var i = 0; i < count; i++) rows.Add((await fixture.SeedAsync(customer, marker)).Id);
        await fixture.SeedAsync(customer, "unrelated");
        await fixture.SeedAsync(fixture.Customer(), scoped ? marker : "unrelated");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        foreach (var descending in new[] { false, true })
        {
            var expected = descending ? rows.AsEnumerable().Reverse().ToArray() : rows.ToArray();
            var sort = descending ? "QuotationId_Descending" : "QuotationId_Ascending";
            foreach (var index in new int?[] { null, -1, 0, 1, 2, int.MaxValue })
            {
                var query = $"{route}?search={marker}&sort={sort}" + (index is null ? "" : $"&index={index}");
                using var response = await client.GetAsync(query);
                if (count == 0 || index > 1)
                {
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(expected, Ids(json));
                Assert.Equal(count, json.RootElement.GetProperty("TotalRecords").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
                Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
            }
            using var explicitSize = await client.GetAsync($"{route}?search={marker}&sort={sort}&size=50&index=1");
            Assert.Equal(count == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, explicitSize.StatusCode);
            if (count == 0) continue;
            using var explicitJson = JsonDocument.Parse(await explicitSize.Content.ReadAsStringAsync());
            Assert.Equal(expected.Take(50), Ids(explicitJson));
            Assert.Equal((int)Math.Ceiling(count / 50d), explicitJson.RootElement.GetProperty("TotalPages").GetInt32());
        }
        using var missing = await client.GetAsync($"{route}?search={marker}-missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    [InlineData(false, 101)]
    [InlineData(true, 101)]
    [InlineData(false, 250)]
    [InlineData(true, 250)]
    [InlineData(false, 251)]
    [InlineData(true, 251)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    public async Task Positive_explicit_size_is_not_capped_after_filtering(bool scoped, int size)
    {
        const int count = 261;
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        var rows = new List<int>();
        for (var i = 0; i < count; i++) rows.Add((await fixture.SeedAsync(customer, marker)).Id);
        await fixture.SeedAsync(customer, "unrelated");
        await fixture.SeedAsync(fixture.Customer(), scoped ? marker : "unrelated");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        foreach (var descending in new[] { false, true })
        {
            var expected = descending ? rows.AsEnumerable().Reverse().ToArray() : rows.ToArray();
            var sort = descending ? "QuotationId_Descending" : "QuotationId_Ascending";
            foreach (var index in new int?[] { null, -1, 0, 1, 2, 3, int.MaxValue })
            {
                var normalizedIndex = Math.Max(index ?? 1, 1);
                var offset = ((long)normalizedIndex - 1) * size;
                var query = $"{route}?search={marker}&sort={sort}&size={size}" + (index is null ? "" : $"&index={index}");
                using var response = await client.GetAsync(query);
                if (offset >= count)
                {
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(expected.Skip((int)offset).Take(size), Ids(json));
                Assert.Equal(count, json.RootElement.GetProperty("TotalRecords").GetInt32());
                Assert.Equal((int)Math.Ceiling(count / (double)size), json.RootElement.GetProperty("TotalPages").GetInt32());
                Assert.Equal(normalizedIndex, json.RootElement.GetProperty("PageIndex").GetInt32());
                Assert.Equal(normalizedIndex > 1, json.RootElement.GetProperty("HasPreviousPage").GetBoolean());
                Assert.Equal(normalizedIndex < (int)Math.Ceiling(count / (double)size), json.RootElement.GetProperty("HasNextPage").GetBoolean());
                Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
            }
        }
        using var missing = await client.GetAsync($"{route}?search={marker}-missing&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var anonymous = fixture.Client(app, authenticated: false);
        using var unauthenticated = await anonymous.GetAsync($"{route}?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var denied = fixture.Client(app, permissions: false);
        using var forbidden = await denied.GetAsync($"{route}?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData(false, int.MaxValue, 250)]
    [InlineData(true, int.MaxValue, 250)]
    [InlineData(false, 1073741825, 4)]
    [InlineData(true, 1073741825, 4)]
    [InlineData(false, int.MaxValue, 2)]
    [InlineData(true, int.MaxValue, 2)]
    public async Task Distant_page_preserves_customer_filter_and_source_notfound_without_wrapping(bool scoped, int pageIndex, int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        var customer = fixture.Customer();
        var first = await fixture.SeedAsync(customer, marker);
        var second = await fixture.SeedAsync(customer, marker);
        var outside = await fixture.SeedAsync(fixture.Customer(), marker);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var route = scoped ? $"/quotations/customers/{customer}" : "/quotations";
        using var response = await client.GetAsync($"{route}?search={marker}&index={pageIndex}&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var total = scoped ? 2 : 3;
        using var missing = await client.GetAsync($"{route}?search={marker}-missing&index={pageIndex}&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var firstPage = await client.GetAsync($"{route}?search={marker}&index=1&size=250");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using var full = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        Assert.Equal(scoped ? new[] { first.Id, second.Id } : new[] { first.Id, second.Id, outside.Id }, Ids(full));
        Assert.Equal(total, full.RootElement.GetProperty("TotalRecords").GetInt32());
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
    private Infrastructure.DisposableContainerPair? containers;
    private PostgreSqlContainer postgres => (PostgreSqlContainer)containers!.First;
    private IContainer redis => containers!.Second;
    private readonly RSA key = RSA.Create(2048);
    private int customer = 1000000;
    public int LiveCalls;
    public string? ExpectedLiveResource;
    public string? ReceivedLiveResource;
    public int Customer() => Interlocked.Increment(ref customer);
    private string Requests => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())) { Database = "draft_requests" }.ConnectionString;
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())).Options);
    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation99-draft-read",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels)
                .WithCreateParameterModifier(parameters => ConfigureLoopback(parameters, "5432/tcp")).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .WithCreateParameterModifier(parameters => ConfigureLoopback(parameters, "6379/tcp")).Build());
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
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
        try { if (containers is not null) await containers.DisposeAsync(); }
        finally { key.Dispose(); }
    }
    private static void ConfigureLoopback(Docker.DotNet.Models.CreateContainerParameters parameters, string port)
    {
        parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
        parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<Docker.DotNet.Models.PortBinding>>();
        parameters.HostConfig.PortBindings[port] = [new Docker.DotNet.Models.PortBinding { HostIP = "127.0.0.1", HostPort = "" }];
    }
    private sealed class Factory(DraftReadFixture fixture, bool resourceScoped) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            if (resourceScoped) builder.UseSetting("Features:ResourceScopedAuthEnabled", "true");
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = Infrastructure.DisposablePostgresConnectionPolicy.Isolate(fixture.postgres.GetConnectionString()),
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
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
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

internal sealed class QuotationCollectionCommandProbe(string marker, string table) : DbCommandInterceptor
{
    public ConcurrentQueue<string> Commands { get; } = new();
    public bool FailCount { get; init; }
    public CancellationTokenSource? CancelCount { get; init; }

    private void Observe(DbCommand command)
    {
        if (!command.CommandText.Contains("FROM \"" + table + "\"", StringComparison.Ordinal)
            || !command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value && value.Contains(marker, StringComparison.Ordinal)))
            return;
        var count = command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase);
        Commands.Enqueue(count ? "count" : "page");
        if (!count) return;
        if (FailCount) throw new InvalidOperationException("Synthetic collection count failure.");
        if (CancelCount is not null)
        {
            CancelCount.Cancel();
            throw new OperationCanceledException(CancelCount.Token);
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }
}
