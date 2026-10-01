using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Opaque finder text through normal authenticated HTTP and migrated disposable PostgreSQL.</summary>
public sealed class QuotationRequestFinderEnvelopeHttpTests(RequestTriageFixture fixture) : IClassFixture<RequestTriageFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinderEnvelope_GetUpdateReadback_PreservesExactContextAndServerOwnedHistory(bool refinements)
    {
        var original = FinderEnvelopeProof.Value(refinements);
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true, field: "InternalComment", value: original);
        var history = await HistoryAsync(row.Id);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var before = await ReadAsync(client, row.Id);
        AssertReadback(original, row, before);
        await using (var initialDb = fixture.Context())
            Assert.Equal(original, (await initialDb.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id)).InternalComment);

        var updated = original.Replace("ตรวจชิ้นงาน / Review part", "พร้อมตรวจสอบ / Ready for review", StringComparison.Ordinal);
        using var request = Update(row, updated, before.RootElement.GetProperty("ModifiedDate").GetDateTime());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using (var db = fixture.Context())
        {
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(updated, persisted.InternalComment);
            AssertServerOwned(row, persisted);
            Assert.NotEqual(row.ModifiedDate, persisted.ModifiedDate);
        }
        using var after = await ReadAsync(client, row.Id);
        AssertReadback(updated, row, after);
        Assert.Equal(history, await HistoryAsync(row.Id));
        AssertContextUnchanged(original, updated);
    }

    [Fact]
    public async Task FinderEnvelope_StaleAfterWinningWrite_RejectsWithoutChangingWinningGraph()
    {
        var original = FinderEnvelopeProof.Value(true);
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true, field: "InternalComment", value: original);
        var history = await HistoryAsync(row.Id);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var loaded = await ReadAsync(client, row.Id);
        var loadedVersion = loaded.RootElement.GetProperty("ModifiedDate").GetDateTime();
        var winning = original.Replace("ตรวจชิ้นงาน / Review part", "ชนะการแก้ไข / Winning review", StringComparison.Ordinal);
        using var first = Update(row, winning, loadedVersion);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);

        string winningGraph;
        DateTime? winningVersion;
        await using (var db = fixture.Context())
        {
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(winning, persisted.InternalComment);
            AssertServerOwned(row, persisted);
            winningVersion = persisted.ModifiedDate;
            Assert.NotEqual(row.ModifiedDate, winningVersion);
            winningGraph = JsonSerializer.Serialize(persisted);
        }
        var staleValue = original.Replace("ตรวจชิ้นงาน / Review part", "ล้าสมัย / Stale review", StringComparison.Ordinal);
        using var stale = Update(row, staleValue, loadedVersion);
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        await using (var db = fixture.Context())
        {
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(winningGraph, JsonSerializer.Serialize(persisted));
            Assert.Equal(winningVersion, persisted.ModifiedDate);
        }
        using var readback = await ReadAsync(client, row.Id);
        AssertReadback(winning, row, readback);
        Assert.Equal(winningVersion, readback.RootElement.GetProperty("ModifiedDate").GetDateTime());
        Assert.Equal(history, await HistoryAsync(row.Id));
    }

    private async Task<string> HistoryAsync(int id)
    {
        await using var db = fixture.Context();
        var history = await db.RequestQualificationAudit.AsNoTracking().Where(x => x.RequestId == id).OrderBy(x => x.Id).ToListAsync();
        Assert.Single(history);
        return JsonSerializer.Serialize(history);
    }

    private static HttpRequestMessage Update(QuotationRequest row, string envelope, DateTime expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotationrequests/{row.Id}")
        {
            Content = JsonContent.Create(new
            {
                row.FirstName,
                row.LastName,
                row.Email,
                row.TelephoneNumber,
                row.Country,
                row.CompanyName,
                row.TaxIdentification,
                row.Message,
                InternalComment = envelope,
                Done = true,
                JourneyId = Guid.NewGuid(),
                TransactionId = "client-forged",
                CreatedDate = DateTime.UtcNow,
                QualificationState = "not_qualified",
                QualificationVersion = 99,
            }),
        };
        request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected, DateTimeKind.Utc).ToString("O"));
        return request;
    }

    private static async Task<JsonDocument> ReadAsync(HttpClient client, int id)
    {
        using var response = await client.GetAsync($"/quotationrequests/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertReadback(string expected, QuotationRequest row, JsonDocument json)
    {
        Assert.Equal(expected, json.RootElement.GetProperty("InternalComment").GetString());
        Assert.Equal(row.JourneyId, json.RootElement.GetProperty("JourneyId").GetGuid());
        Assert.Equal(row.TransactionId, json.RootElement.GetProperty("TransactionId").GetString());
        Assert.Equal(row.CreatedDate, json.RootElement.GetProperty("CreatedDate").GetDateTime());
    }

    private static void AssertServerOwned(QuotationRequest before, QuotationRequest after)
    {
        Assert.Equal(before.JourneyId, after.JourneyId);
        Assert.Equal(before.TransactionId, after.TransactionId);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.QualificationState, after.QualificationState);
        Assert.Equal(before.QualificationVersion, after.QualificationVersion);
        Assert.Equal(before.QualificationStateChangedUtc, after.QualificationStateChangedUtc);
    }

    private static void AssertContextUnchanged(string original, string updated)
    {
        using var before = JsonDocument.Parse(original);
        using var after = JsonDocument.Parse(updated);
        foreach (var property in before.RootElement.EnumerateObject().Where(x => x.Name != "operator_comment"))
            Assert.True(JsonElement.DeepEquals(property.Value, after.RootElement.GetProperty(property.Name)));
    }
}

/// <summary>Actual request ingestion and server transaction attribution without expanding fixture permissions.</summary>
public sealed class QuotationRequestFinderEnvelopeCreateHttpTests(RequestCreateRetryFixture fixture) : IClassFixture<RequestCreateRetryFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinderEnvelope_Create_PreservesExactTextAndServerTransactionId(bool refinements)
    {
        var original = FinderEnvelopeProof.Value(refinements);
        var journey = Guid.NewGuid();
        var iam = new RequestCreateIamTransport(true);
        var diagnostic = new RequestCreateStrategyDiagnostic();
        await using var app = fixture.App(iam, diagnostic);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quotationrequests")
        {
            Content = JsonContent.Create(new
            {
                FirstName = "Synthetic",
                LastName = "Fixture",
                Email = "fixture@example.invalid",
                Country = "Thailand",
                Message = "คำขอทดสอบ / Synthetic finder request",
                InternalComment = original,
                Done = false,
                JourneyId = journey,
                TransactionId = "client-forged",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, iam.Calls);
        Assert.False(diagnostic.Rejected);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(original, json.RootElement.GetProperty("InternalComment").GetString());
        Assert.Equal(journey, json.RootElement.GetProperty("JourneyId").GetGuid());
        var id = json.RootElement.GetProperty("Id").GetInt32();
        Assert.Equal($"request-{id}", json.RootElement.GetProperty("TransactionId").GetString());
        Assert.EndsWith($"/quotationrequests/{id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var db = fixture.Context();
        var persisted = Assert.Single(await db.Requests.AsNoTracking().Where(x => x.JourneyId == journey).ToListAsync());
        Assert.Equal(original, persisted.InternalComment);
        Assert.Equal(id, persisted.Id);
        Assert.Equal(journey, persisted.JourneyId);
        Assert.Equal($"request-{id}", persisted.TransactionId);
        Assert.Equal(persisted.CreatedDate, json.RootElement.GetProperty("CreatedDate").GetDateTime());
        Assert.Equal(persisted.ModifiedDate, json.RootElement.GetProperty("ModifiedDate").GetDateTime());
        Assert.False(await db.RequestQualificationAudit.AnyAsync(x => x.RequestId == id));
    }
}

internal static class FinderEnvelopeProof
{
    // Frozen source IDs; not generated by the implementation under test or an Intranet dependency.
    public static string Value(bool refinements)
    {
        var files = refinements ? "files-real-part" : "files-3d";
        var optional = refinements ? ",\"performance\":\"performance-strength\",\"environment\":\"environment-outdoor\"" : string.Empty;
        return $$$$"""
            {"source":"service_finder","version":1,"answers":{"files":"{{{{files}}}}","service":"service-3d","material":"material-plastic","quantity":"quantity-1-10","end-use":"use-prototype"{{{{optional}}}},"future_answer":{"keep":[1,true,null,"ไทย / English"]}},"recommended_service_ids":["scanning","design"],"finder_path":["scanning","design"],"operator_comment":"ตรวจชิ้นงาน / Review part","future_context":{"keep":"unchanged","nested":{"text":"<tag> & ไทย / English"}}}
            """;
    }
}
