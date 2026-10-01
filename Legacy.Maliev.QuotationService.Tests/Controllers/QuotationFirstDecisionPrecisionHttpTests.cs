using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Actual first-decision HTTP and fresh PostgreSQL precision-bound retry keys.</summary>
public sealed class QuotationFirstDecisionPrecisionHttpTests(InvoiceConsumerFixture fixture)
    : IClassFixture<InvoiceConsumerFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task First_acceptance_partial_order_retry_keeps_key_and_exact_outcome_time(int discardedTicks)
    {
        var decisionTime = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(123450 + discardedTicks);
        var seeded = await fixture.SeedAsync();
        var orders = new InvoiceOrderTransport { Fail = true };
        await using var original = fixture.App(orders);
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedDecisionClock(decisionTime));
        }));
        using var client = fixture.Client(app, out _);
        using var partial = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true, InvoiceId = 0 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        Assert.NotEmpty(orders.Requests);
        var firstKey = orders.Requests.First().Key;
        long firstOutcomeId;
        await using (var observed = fixture.Context())
        {
            var row = await observed.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
            Assert.True(row.Accepted);
            Assert.Null(row.InvoiceId);
            Assert.Null(row.DecisionOrderVersion);
            var persistedTime = DateTime.SpecifyKind(decisionTime.UtcDateTime.AddTicks(-discardedTicks), DateTimeKind.Unspecified);
            Assert.Equal(persistedTime, row.ModifiedDate);
            var outcome = await observed.AcceptedOutcomes.AsNoTracking().SingleAsync(value => value.QuotationId == seeded.Id);
            firstOutcomeId = outcome.Id;
            Assert.Equal(decisionTime.UtcDateTime.Ticks, outcome.AcceptedUtc.AddTicks(outcome.AcceptedUtcSubMicrosecondTicks).Ticks);
            Assert.Equal(discardedTicks, outcome.AcceptedUtcSubMicrosecondTicks);
            Assert.Equal(seeded.SourceJourneyId, outcome.SourceJourneyId);
            Assert.Equal("employee", outcome.AcceptanceOrigin);
        }

        orders.Fail = false;
        using var replay = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true, InvoiceId = 0 });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var fresh = fixture.Context();
        var retained = Assert.Single(await fresh.AcceptedOutcomes.AsNoTracking().Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal(firstOutcomeId, retained.Id);
        Assert.Equal(decisionTime.UtcDateTime.Ticks, retained.AcceptedUtc.AddTicks(retained.AcceptedUtcSubMicrosecondTicks).Ticks);
        Assert.All(orders.Requests, request => Assert.Equal(firstKey, request.Key));
        Assert.Equal($"quotation-{seeded.Id}-accepted-{decisionTime.UtcDateTime.AddTicks(-discardedTicks).Ticks:x}-order-701", firstKey);
    }

    [Fact]
    public async Task Distinct_accept_decline_reaccept_within_one_microsecond_never_reuses_order_key()
    {
        var start = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var clock = new FixedDecisionClock(start);
        var seeded = await fixture.SeedAsync();
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = fixture.Client(app, out _);
        foreach (var (accepted, tick) in new[] { (true, 0), (false, 1), (true, 2) })
        {
            clock.Value = start.AddTicks(tick);
            using var response = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
                new { Accepted = accepted, EmployeeInitiated = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await using var fresh = fixture.Context();
        var row = await fresh.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.True(row.Accepted);
        Assert.Null(row.DecisionOrderVersion);
        Assert.Single(await fresh.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal(3, orders.Requests.Count);
        Assert.Equal(3, orders.Requests.Select(value => value.Key).Distinct().Count());
    }

    [Fact]
    public async Task Exhausted_decision_version_fails_closed_before_save_or_order_call()
    {
        var seeded = await fixture.SeedAsync();
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.ModifiedDate = DateTime.MaxValue;
            await setup.SaveChangesAsync();
        }
        var orders = new InvoiceOrderTransport();
        await using var app = fixture.App(orders);
        using var client = fixture.Client(app, out _);
        using var response = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(orders.Requests);
        await using var fresh = fixture.Context();
        var retained = await fresh.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Null(retained.Accepted);
        Assert.Equal(DateTime.MaxValue, retained.ModifiedDate);
        Assert.Empty(await fresh.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task Stalled_or_backward_clock_advances_version_once_and_preserves_raw_event_time(int offsetTicks)
    {
        var previous = new DateTime(2031, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
        var eventTime = new DateTimeOffset(DateTime.SpecifyKind(previous.AddTicks(offsetTicks), DateTimeKind.Utc));
        var seeded = await fixture.SeedAsync();
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.ModifiedDate = previous;
            await setup.SaveChangesAsync();
        }
        var orders = new InvoiceOrderTransport { Fail = true };
        await using var original = fixture.App(orders);
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedDecisionClock(eventTime));
        }));
        using var client = fixture.Client(app, out _);
        using var partial = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        orders.Fail = false;
        using var replay = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var fresh = fixture.Context();
        var rowAfter = await fresh.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(previous.AddTicks(10), rowAfter.ModifiedDate);
        var outcome = Assert.Single(await fresh.AcceptedOutcomes.AsNoTracking().Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal(eventTime.UtcDateTime.Ticks, outcome.AcceptedUtc.AddTicks(outcome.AcceptedUtcSubMicrosecondTicks).Ticks);
        Assert.All(orders.Requests, request => Assert.Equal($"quotation-{seeded.Id}-accepted-{previous.AddTicks(10).Ticks:x}-order-701", request.Key));
    }

    private sealed class FixedDecisionClock(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => Value;
    }
}
