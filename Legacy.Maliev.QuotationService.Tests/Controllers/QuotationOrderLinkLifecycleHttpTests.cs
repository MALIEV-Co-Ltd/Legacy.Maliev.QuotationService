using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Source order-link lifecycle through Production routes, real JWT/IAM client and PostgreSQL.</summary>
public sealed class QuotationOrderLinkLifecycleHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Fact]
    public async Task Link_metadata_uses_absolute_location_query_id_update_and_complete_delete_lifecycle()
    {
        await using var app = fixture.App(resourceScoped: true);
        using var client = fixture.Client(app);
        var parent = await Parent(client);
        var collection = $"/quotations/{parent}/orders";
        using (var empty = await client.GetAsync(collection)) Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await client.PostAsync($"{collection}/1101", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>())!;
        var location = Assert.IsType<Uri>(created.Headers.Location);
        Assert.True(location.IsAbsoluteUri);
        Assert.Equal($"/quotations/orders/{row.Id}", location.AbsolutePath);
        using (var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
            Assert.Equal(new[] { "Id", "QuotationId", "OrderId", "CreatedDate", "ModifiedDate" },
                json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(parent, row.QuotationId);
        Assert.Equal(1101, row.OrderId);
        Assert.NotNull(row.CreatedDate);
        Assert.NotNull(row.ModifiedDate);
        using (var detail = await client.GetAsync(location))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Equal(row, await detail.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>());
        }
        using (var list = await client.GetAsync(collection))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal(row, Assert.Single((await list.Content.ReadFromJsonAsync<QuotationOrderLinkResponse[]>())!));
        }
        var updateRoute = $"/quotations/orders?id={row.Id}";
        using (var invalid = await client.PutAsync(updateRoute, new StringContent("null", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var updated = await client.PutAsJsonAsync(updateRoute, new UpsertQuotationOrderLinkRequest(parent, 1102)))
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.OrderLinks.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal(1102, persisted.OrderId);
        Assert.Equal(row.CreatedDate, persisted.CreatedDate);
        Assert.True(persisted.ModifiedDate >= row.ModifiedDate);
        using (var deleted = await client.DeleteAsync(location)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await db.OrderLinks.AnyAsync(value => value.Id == row.Id));
        Assert.True(await db.Quotations.AnyAsync(value => value.Id == parent));
        using (var absent = await client.GetAsync(location)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.DeleteAsync(location)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.PutAsJsonAsync(updateRoute, new UpsertQuotationOrderLinkRequest(parent, 1103)))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.GetAsync(collection)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Contains($"/quotations/{parent}", fixture.LiveResources);
    }

    [Fact]
    public async Task Duplicate_order_links_and_reparenting_preserve_source_child_only_versions()
    {
        await using var app = fixture.App(resourceScoped: true);
        using var client = fixture.Client(app);
        var parent = await Parent(client);
        var other = await Parent(client);
        await using var db = fixture.Context();
        var before = await db.Quotations.AsNoTracking().Where(value => value.Id == parent || value.Id == other)
            .OrderBy(value => value.Id).Select(value => value.ModifiedDate).ToArrayAsync();
        using var first = await client.PostAsync($"/quotations/{parent}/orders/1111", null);
        using var second = await client.PostAsync($"/quotations/{parent}/orders/1111", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var row = (await first.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>())!;
        var duplicate = (await second.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>())!;
        Assert.NotEqual(row.Id, duplicate.Id);
        using (var updated = await client.PutAsJsonAsync($"/quotations/orders?id={row.Id}", new UpsertQuotationOrderLinkRequest(other, 1112)))
            Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var moved = await db.OrderLinks.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal(other, moved.QuotationId);
        Assert.Equal(1112, moved.OrderId);
        Assert.Equal(row.CreatedDate, moved.CreatedDate);
        using (var original = await client.GetAsync($"/quotations/{parent}/orders"))
        {
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.Equal(duplicate.Id, Assert.Single((await original.Content.ReadFromJsonAsync<QuotationOrderLinkResponse[]>())!).Id);
        }
        using (var target = await client.GetAsync($"/quotations/{other}/orders"))
        {
            Assert.Equal(HttpStatusCode.OK, target.StatusCode);
            Assert.Equal(row.Id, Assert.Single((await target.Content.ReadFromJsonAsync<QuotationOrderLinkResponse[]>())!).Id);
        }
        var after = await db.Quotations.AsNoTracking().Where(value => value.Id == parent || value.Id == other)
            .OrderBy(value => value.Id).Select(value => value.ModifiedDate).ToArrayAsync();
        Assert.Equal(before, after);
        Assert.Contains($"/quotations/{other}", fixture.LiveResources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_order_link_route_denies_missing_JWT_or_live_IAM_rejection_without_mutation(bool authenticated)
    {
        await using var seedApp = fixture.App();
        using var seedClient = fixture.Client(seedApp);
        var parent = await Parent(seedClient);
        using var created = await seedClient.PostAsync($"/quotations/{parent}/orders/1121", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<QuotationOrderLinkResponse>())!;
        await using var db = fixture.Context();
        var version = (await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == parent)).ModifiedDate;
        await using var app = fixture.App(allowed: false, resourceScoped: true);
        using var client = fixture.Client(app, authenticated);
        var expected = authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        using (var denied = await client.PostAsync($"/quotations/{parent}/orders/1122", null)) Assert.Equal(expected, denied.StatusCode);
        using (var denied = await client.GetAsync($"/quotations/{parent}/orders")) Assert.Equal(expected, denied.StatusCode);
        using (var denied = await client.GetAsync(created.Headers.Location)) Assert.Equal(expected, denied.StatusCode);
        using (var denied = await client.PutAsJsonAsync($"/quotations/orders?id={row.Id}", new UpsertQuotationOrderLinkRequest(parent, 1123)))
            Assert.Equal(expected, denied.StatusCode);
        using (var denied = await client.DeleteAsync(created.Headers.Location)) Assert.Equal(expected, denied.StatusCode);
        var unchanged = await db.OrderLinks.AsNoTracking().SingleAsync(value => value.QuotationId == parent);
        Assert.Equal(row.Id, unchanged.Id);
        Assert.Equal(row.OrderId, unchanged.OrderId);
        Assert.Equal(row.CreatedDate, unchanged.CreatedDate);
        Assert.Equal(row.ModifiedDate, unchanged.ModifiedDate);
        Assert.Equal(version, (await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == parent)).ModifiedDate);
    }

    private static async Task<int> Parent(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync("/quotations", new UpsertQuotationRequest(101, 41, null, 30,
            new DateTime(2035, 1, 1), 100.50m, 7.04m, 107.54m, 3.02m, 1, "order-link lifecycle", null, null, null, true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<QuotationResponse>())!.Id;
    }
}
