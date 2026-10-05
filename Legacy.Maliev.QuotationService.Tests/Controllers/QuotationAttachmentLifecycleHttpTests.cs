using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Real Production routes, signed JWT, live IAM transport and disposable PostgreSQL/Redis; metadata only.</summary>
public sealed class QuotationAttachmentLifecycleHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_routes_persist_follow_absolute_locations_update_and_delete(bool requestFile)
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var parent = await Parent(client, requestFile);
        var collection = Collection(requestFile, parent);
        using (var empty = await client.GetAsync(collection)) Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await Create(client, collection, "fixture-bucket", "files/original.stl");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = Assert.IsType<Uri>(created.Headers.Location);
        Assert.True(location.IsAbsoluteUri);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("Id").GetInt32();
        Assert.Equal(parent, body.RootElement.GetProperty(requestFile ? "RequestId" : "QuotationId").GetInt32());
        Assert.Equal("fixture-bucket", body.RootElement.GetProperty("Bucket").GetString());
        Assert.Equal("files/original.stl", body.RootElement.GetProperty("ObjectName").GetString());
        Assert.Equal(new[] { "Id", requestFile ? "RequestId" : "QuotationId", "Bucket", "ObjectName", "CreatedDate", "ModifiedDate" },
            body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal($"{Detail(requestFile)}/{id}", location.AbsolutePath);
        using (var detail = await client.GetAsync(location)) Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using (var list = await client.GetAsync(collection))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(id, Assert.Single(json.RootElement.EnumerateArray()).GetProperty("Id").GetInt32());
        }
        object update = requestFile
            ? new UpsertQuotationRequestFileRequest(parent, "next-bucket", "files/updated.stl")
            : new UpsertQuotationFileRequest(parent, "next-bucket", "files/updated.stl");
        using (var updated = await client.PutAsJsonAsync(location, update)) Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        await AssertRow(requestFile, id, parent, "next-bucket", "files/updated.stl");
        using (var deleted = await client.DeleteAsync(location)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertMissing(requestFile, id);
        using (var absent = await client.GetAsync(location)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.DeleteAsync(location)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var absent = await client.GetAsync(collection)) Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        if (!requestFile) Assert.Contains($"/quotations/{parent}", fixture.LiveResources);
    }

    [Fact]
    public async Task Request_file_fingerprint_replay_conflict_and_coordinate_deduplication_use_real_stores()
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var parent = await Parent(client, true);
        var other = await Parent(client, true);
        var key = Guid.NewGuid().ToString("N");
        using var original = await Create(client, Collection(true, parent), "fixture-bucket", "files/replay.stl", key);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var row = (await original.Content.ReadFromJsonAsync<QuotationRequestFileResponse>())!;
        using (var replay = await Create(client, Collection(true, parent), "fixture-bucket", "files/replay.stl", key))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(row.Id, (await replay.Content.ReadFromJsonAsync<QuotationRequestFileResponse>())!.Id);
            Assert.Equal(original.Headers.Location, replay.Headers.Location);
        }
        foreach (var coordinates in new[]
        {
            (Parent: other, Bucket: "fixture-bucket", Object: "files/replay.stl"),
            (Parent: parent, Bucket: "different-bucket", Object: "files/replay.stl"),
            (Parent: parent, Bucket: "fixture-bucket", Object: "files/different.stl")
        })
        {
            using var conflict = await Create(client, Collection(true, coordinates.Parent), coordinates.Bucket, coordinates.Object, key);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("idempotency_key_conflict", problem.RootElement.GetProperty("code").GetString());
        }
        using var unkeyed = await Create(client, Collection(true, parent), "fixture-bucket", "files/replay.stl");
        Assert.Equal(HttpStatusCode.Created, unkeyed.StatusCode);
        Assert.Equal(row.Id, (await unkeyed.Content.ReadFromJsonAsync<QuotationRequestFileResponse>())!.Id);
        await using var db = fixture.RequestContext();
        Assert.Single(await db.Files.Where(value => value.RequestId == parent).ToListAsync());
        Assert.False(await db.Files.AnyAsync(value => value.RequestId == other));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Every_metadata_route_denies_unauthenticated_or_live_rejected_calls_without_mutation(bool requestFile, bool authenticated)
    {
        await using var allowed = fixture.App();
        using var creator = fixture.Client(allowed);
        var parent = await Parent(creator, requestFile);
        using var created = await Create(creator, Collection(requestFile, parent), "fixture-bucket", "files/protected.stl");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("Id").GetInt32();
        await using var denied = fixture.App(allowed: false);
        using var client = fixture.Client(denied, authenticated);
        foreach (var operation in new[] { "create", "detail", "list", "update", "delete" })
        {
            using var message = new HttpRequestMessage(operation switch
            {
                "create" => HttpMethod.Post, "update" => HttpMethod.Put, "delete" => HttpMethod.Delete, _ => HttpMethod.Get
            }, operation switch
            {
                "create" => Collection(requestFile, parent) + "?bucket=denied-bucket&objectName=denied.stl",
                "list" => Collection(requestFile, parent), _ => $"{Detail(requestFile)}/{id}"
            });
            if (operation == "update") message.Content = JsonContent.Create(new { Bucket = "denied-bucket", ObjectName = "denied.stl", RequestId = parent, QuotationId = parent });
            using var response = await client.SendAsync(message);
            Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
            await AssertRow(requestFile, id, parent, "fixture-bucket", "files/protected.stl");
        }
        if (requestFile)
        {
            await using var db = fixture.RequestContext();
            Assert.Single(await db.Files.Where(value => value.RequestId == parent).ToListAsync());
        }
        else
        {
            await using var db = fixture.Context();
            Assert.Single(await db.Files.Where(value => value.QuotationId == parent).ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_coordinates_and_missing_parent_preserve_bad_request_and_not_found(bool requestFile)
    {
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        var parent = await Parent(client, requestFile);
        using (var invalid = await Create(client, Collection(requestFile, parent), "", "file.stl")) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var missing = await Create(client, Collection(requestFile, int.MaxValue), "fixture-bucket", "file.stl")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var empty = await client.GetAsync(Collection(requestFile, parent))) Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
    }

    private async Task<int> Parent(HttpClient client, bool requestFile)
    {
        if (requestFile)
        {
            await using var db = fixture.RequestContext();
            var entity = new QuotationRequest { FirstName = "Synthetic", Message = Guid.NewGuid().ToString("N") };
            db.Requests.Add(entity);
            await db.SaveChangesAsync();
            return entity.Id;
        }
        using var response = await client.PostAsJsonAsync("/quotations", new UpsertQuotationRequest(101, 41, null, 30,
            new DateTime(2035, 1, 1), 100.50m, 7.04m, 107.54m, 3.02m, 1, "metadata fixture", null, null, null, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QuotationResponse>())!.Id;
    }

    private static string Collection(bool requestFile, int parent) => $"/{(requestFile ? "quotationrequests" : "quotations")}/{parent}/files";
    private static string Detail(bool requestFile) => requestFile ? "/quotationrequests/files" : "/quotations/files";
    private static async Task<HttpResponseMessage> Create(HttpClient client, string collection, string bucket, string objectName, string? key = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{collection}?bucket={Uri.EscapeDataString(bucket)}&objectName={Uri.EscapeDataString(objectName)}");
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }
    private async Task AssertRow(bool requestFile, int id, int parent, string bucket, string objectName)
    {
        if (requestFile)
        {
            await using var db = fixture.RequestContext();
            var row = await db.Files.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal(parent, row.RequestId); Assert.Equal(bucket, row.Bucket); Assert.Equal(objectName, row.ObjectName);
        }
        else
        {
            await using var db = fixture.Context();
            var row = await db.Files.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal(parent, row.QuotationId); Assert.Equal(bucket, row.Bucket); Assert.Equal(objectName, row.ObjectName);
        }
    }
    private async Task AssertMissing(bool requestFile, int id)
    {
        if (requestFile)
        {
            await using var db = fixture.RequestContext();
            Assert.False(await db.Files.AnyAsync(value => value.Id == id));
        }
        else
        {
            await using var db = fixture.Context();
            Assert.False(await db.Files.AnyAsync(value => value.Id == id));
        }
    }
}
