using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationOrderItemMutationVersionHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Theory]
    [InlineData(0L)]
    [InlineData(9L)]
    [InlineData(-864000000000L)]
    public async Task Line_version_advances_at_database_precision_and_rejects_replayed_tokens(long clockOffsetTicks)
    {
        var clock = new MutationClock(new DateTimeOffset(2032, 2, 3, 4, 5, 6, TimeSpan.Zero));
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = fixture.Client(app);
        var draft = new UpsertQuotationRequest(101, 41, null, 30, new DateTime(2035, 1, 1),
            100.50m, 7.04m, 107.54m, 3.02m, 1, "synthetic version control", null, null, null, null);
        using var createdRoot = await client.PostAsJsonAsync("/quotations", draft);
        Assert.Equal(HttpStatusCode.Created, createdRoot.StatusCode);
        var root = Assert.IsType<QuotationResponse>(await createdRoot.Content.ReadFromJsonAsync<QuotationResponse>());
        var initialPayload = new UpsertQuotationOrderItemRequest(root.Id, 901, "initial", 2, 15.25m);
        using var createdLine = await client.PostAsJsonAsync("/quotations/orderitems", initialPayload);
        Assert.Equal(HttpStatusCode.Created, createdLine.StatusCode);
        var initial = Assert.IsType<QuotationOrderItemResponse>(await createdLine.Content.ReadFromJsonAsync<QuotationOrderItemResponse>());
        var originalVersion = Assert.IsType<DateTime>(initial.ModifiedDate);
        clock.Value = new DateTimeOffset(DateTime.SpecifyKind(originalVersion.AddTicks(clockOffsetTicks), DateTimeKind.Utc));

        var firstPayload = initialPayload with { Description = "first edit", Quantity = 3 };
        using var first = Update(initial.Id, firstPayload, originalVersion);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        using var firstDetail = await client.GetAsync($"/quotations/orderitems/{initial.Id}");
        Assert.Equal(HttpStatusCode.OK, firstDetail.StatusCode);
        var firstState = Assert.IsType<QuotationOrderItemResponse>(await firstDetail.Content.ReadFromJsonAsync<QuotationOrderItemResponse>());
        Assert.Equal(originalVersion.AddTicks(10), firstState.ModifiedDate);
        Assert.Equal(45.75m, firstState.Subtotal);
        using var stale = Update(initial.Id, initialPayload with { Description = "stale overwrite" }, originalVersion);
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        var finalPayload = firstPayload with { Description = "second edit", Quantity = 4 };
        using var fresh = Update(initial.Id, finalPayload, Assert.IsType<DateTime>(firstState.ModifiedDate));
        using var freshResponse = await client.SendAsync(fresh);
        Assert.Equal(HttpStatusCode.NoContent, freshResponse.StatusCode);
        using var replay = Update(initial.Id, initialPayload, Assert.IsType<DateTime>(firstState.ModifiedDate));
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        using var finalDetail = await client.GetAsync($"/quotations/orderitems/{initial.Id}");
        Assert.Equal(HttpStatusCode.OK, finalDetail.StatusCode);
        var finalState = Assert.IsType<QuotationOrderItemResponse>(await finalDetail.Content.ReadFromJsonAsync<QuotationOrderItemResponse>());
        Assert.Equal(originalVersion.AddTicks(20), finalState.ModifiedDate);
        Assert.Equal("second edit", finalState.Description);
        Assert.Equal(4, finalState.Quantity);
        Assert.Equal(61.00m, finalState.Subtotal);
        Assert.Equal(initial.CreatedDate, finalState.CreatedDate);
        Assert.Equal(initial.QuotationId, finalState.QuotationId);
        Assert.Equal(initial.OrderId, finalState.OrderId);

        await using var database = fixture.Context();
        var persisted = await database.OrderItems.AsNoTracking().SingleAsync(value => value.Id == initial.Id);
        Assert.Equal(finalState.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(finalState.Description, persisted.Description);
        Assert.Equal(finalState.Quantity, persisted.Quantity);
        Assert.Equal(finalState.Subtotal, persisted.Subtotal);
        var retainedRoot = await database.Quotations.AsNoTracking().SingleAsync(value => value.Id == root.Id);
        Assert.Equal(root.ModifiedDate, retainedRoot.ModifiedDate);
        Assert.Equal(root.CreatedDate, retainedRoot.CreatedDate);
        Assert.Null(retainedRoot.Accepted);
        Assert.Null(retainedRoot.InvoiceId);
        Assert.False(await database.AcceptedOutcomes.AnyAsync(value => value.QuotationId == root.Id));
        Assert.False(await database.GoogleAnalyticsOutbox.AnyAsync(value => value.QuotationId == root.Id));
    }

    private static HttpRequestMessage Update(int id, UpsertQuotationOrderItemRequest payload, DateTime expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/orderitems/{id}") { Content = JsonContent.Create(payload) };
        request.Headers.TryAddWithoutValidation("X-Expected-Modified-Date", DateTime.SpecifyKind(expected, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
        return request;
    }

    private sealed class MutationClock(DateTimeOffset value) : TimeProvider
    {
        internal DateTimeOffset Value { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => Value;
    }
}
