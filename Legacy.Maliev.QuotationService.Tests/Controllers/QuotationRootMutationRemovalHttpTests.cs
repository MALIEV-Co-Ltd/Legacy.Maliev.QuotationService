using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Root versioned mutation, cache invalidation and restrictive removal through the normal Production host.</summary>
public sealed class QuotationRootMutationRemovalHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Fact]
    public async Task Versioned_full_PUT_preserves_creation_updates_source_fields_invalidates_Redis_and_rejects_stale_write()
    {
        var clock = Clock();
        await using var app = App(clock);
        using var client = fixture.Client(app);
        var root = await Create(client);
        using var scope = app.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
        await cache.SetAsync(Key(root.Id), root, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(root, await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        var changed = new UpsertQuotationRequest(102, 51, 333, 45, new DateTime(2036, 1, 1),
            200.50m, 14.04m, 214.54m, 6.02m, 840, "แก้ไข", "FOB", "fixture delivery", "fixture terms", null);
        using (var updated = await Put(client, root.Id, changed, root.ModifiedDate))
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Null(await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        using var detail = await client.GetAsync($"/quotations/{root.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var current = (await detail.Content.ReadFromJsonAsync<QuotationResponse>())!;
        Assert.Equal(((int?)102, (int?)51, (int?)333, 45, 840), (current.CustomerId, current.EmployeeId, current.InvoiceId, current.Period, current.CurrencyId));
        Assert.Equal((200.50m, 14.04m, 214.54m, (decimal?)6.02m, (decimal?)208.52m),
            (current.Subtotal, current.Vat, current.Total, current.WithholdingTax, current.QuotedAmount));
        Assert.Equal((changed.Comment, changed.Fob, changed.ShippedVia, changed.Terms, changed.ExpirationDate),
            (current.Comment, current.Fob, current.ShippedVia, current.Terms, current.ExpirationDate));
        Assert.Equal(root.CreatedDate, current.CreatedDate);
        Assert.Equal(root.ModifiedDate!.Value.AddMinutes(1), current.ModifiedDate);
        Assert.Null(current.Accepted);
        await cache.SetAsync(Key(root.Id), current, TimeSpan.FromMinutes(2), CancellationToken.None);
        using (var stale = await Put(client, root.Id, changed with { Total = 999m, Comment = "stale root write" }, root.ModifiedDate))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var read = await client.GetAsync($"/quotations/{root.Id}"))
            Assert.Equal(current, await read.Content.ReadFromJsonAsync<QuotationResponse>());
        Assert.Equal(current, await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        await using var db = fixture.Context();
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        Assert.Equal(current.ModifiedDate, stored.ModifiedDate);
        Assert.Equal(214.54m, stored.Total);
        Assert.Empty(await db.AcceptedOutcomes.Where(value => value.QuotationId == root.Id).ToArrayAsync());
        Assert.Empty(await db.GoogleAnalyticsOutbox.Where(value => value.QuotationId == root.Id).ToArrayAsync());
        Assert.Contains($"/quotations/{root.Id}", fixture.LiveResources);
    }

    [Fact]
    public async Task Root_without_dependents_deletes_invalidates_real_cache_and_cannot_be_resurrected_by_stale_cache()
    {
        await using var app = App(Clock());
        using var client = fixture.Client(app);
        var root = await Create(client);
        using var scope = app.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
        await cache.SetAsync(Key(root.Id), root, TimeSpan.FromMinutes(2), CancellationToken.None);
        using (var deleted = await client.DeleteAsync($"/quotations/{root.Id}")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        await using var db = fixture.Context();
        Assert.False(await db.Quotations.AnyAsync(value => value.Id == root.Id));
        await cache.SetAsync(Key(root.Id), root, TimeSpan.FromMinutes(2), CancellationToken.None);
        using (var absent = await client.GetAsync($"/quotations/{root.Id}")) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.DeleteAsync($"/quotations/{root.Id}")) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await Put(client, root.Id, Draft(), null)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var invalid = await client.PutAsync($"/quotations/{root.Id}", new StringContent("null", System.Text.Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("link")]
    [InlineData("file")]
    public async Task Each_real_child_foreign_key_blocks_root_removal_atomically_without_cascade_or_error_detail(string child)
    {
        await using var app = App(Clock());
        using var client = fixture.Client(app);
        var root = await Create(client);
        using var created = child switch
        {
            "line" => await client.PostAsJsonAsync("/quotations/orderitems", new UpsertQuotationOrderItemRequest(root.Id, 9301, "root removal line", 2, 10.25m)),
            "link" => await client.PostAsync($"/quotations/{root.Id}/orders/9302", null),
            _ => await client.PostAsync($"/quotations/{root.Id}/files?bucket=fixture-bucket&objectName=root-removal-synthetic.stl", null)
        };
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var scope = app.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
        await cache.SetAsync(Key(root.Id), root, TimeSpan.FromMinutes(2), CancellationToken.None);
        await using var db = fixture.Context();
        var before = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        var counts = (await db.OrderItems.CountAsync(value => value.QuotationId == root.Id),
            await db.OrderLinks.CountAsync(value => value.QuotationId == root.Id), await db.Files.CountAsync(value => value.QuotationId == root.Id));
        using var blocked = await client.DeleteAsync($"/quotations/{root.Id}");
        Assert.Equal(HttpStatusCode.InternalServerError, blocked.StatusCode);
        var error = await blocked.Content.ReadAsStringAsync();
        foreach (var privateDetail in new[] { "23503", "FK_", "Npgsql", "QuotationHasOrder", "root-removal-synthetic" })
            Assert.DoesNotContain(privateDetail, error, StringComparison.OrdinalIgnoreCase);
        var after = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        Assert.Equal((before.CreatedDate, before.ModifiedDate, before.Accepted, before.Total, before.InvoiceId),
            (after.CreatedDate, after.ModifiedDate, after.Accepted, after.Total, after.InvoiceId));
        Assert.Equal(counts, (await db.OrderItems.CountAsync(value => value.QuotationId == root.Id),
            await db.OrderLinks.CountAsync(value => value.QuotationId == root.Id), await db.Files.CountAsync(value => value.QuotationId == root.Id)));
        Assert.Equal(root, await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        using var detail = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Root_update_and_delete_denials_preserve_row_and_Redis_without_bypassing_live_check(bool delete, bool authenticated)
    {
        var clock = Clock();
        await using var seedApp = App(clock);
        using var seedClient = fixture.Client(seedApp);
        var root = await Create(seedClient);
        using var scope = seedApp.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
        await cache.SetAsync(Key(root.Id), root, TimeSpan.FromMinutes(2), CancellationToken.None);
        var calls = fixture.LiveResources.Count;
        await using var app = App(clock, allowed: false);
        using var client = fixture.Client(app, authenticated);
        using var denied = delete ? await client.DeleteAsync($"/quotations/{root.Id}")
            : await Put(client, root.Id, Draft() with { Total = 999m, Comment = "denied root mutation" }, root.ModifiedDate);
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(calls + (authenticated ? 1 : 0), fixture.LiveResources.Count);
        Assert.Equal(root, await cache.GetAsync<QuotationResponse>(Key(root.Id), CancellationToken.None));
        await using var db = fixture.Context();
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        Assert.Equal((root.Total, root.Comment, root.CreatedDate, root.ModifiedDate, root.Accepted),
            (stored.Total, stored.Comment, stored.CreatedDate, stored.ModifiedDate, stored.Accepted));
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static string Key(int id) => $"quotation:{id}";
    private WebApplicationFactory<Program> App(FakeTimeProvider clock, bool allowed = true) =>
        fixture.App(allowed: allowed, resourceScoped: true).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
    private static UpsertQuotationRequest Draft() => new(101, 41, null, 30, new DateTime(2035, 1, 1),
        100.50m, 7.04m, 107.54m, 3.02m, 764, "root lifecycle", null, null, null, null);
    private static async Task<QuotationResponse> Create(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync("/quotations", Draft());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<QuotationResponse>())!;
    }
    private static async Task<HttpResponseMessage> Put(HttpClient client, int id, UpsertQuotationRequest body, DateTime? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{id}") { Content = JsonContent.Create(body) };
        if (expected is not null)
            request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
        return await client.SendAsync(request);
    }
}
