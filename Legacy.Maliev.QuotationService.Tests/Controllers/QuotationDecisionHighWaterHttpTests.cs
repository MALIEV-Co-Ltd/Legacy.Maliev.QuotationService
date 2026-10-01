using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal HTTP decision generation across intervening ordinary edits, with fresh PG state.</summary>
public sealed class QuotationDecisionHighWaterHttpTests(InvoiceConsumerFixture fixture, ITestOutputHelper output)
    : IClassFixture<InvoiceConsumerFixture>
{
    private static readonly DateTimeOffset Start = new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task Repeated_accept_decline_ordinary_edit_reaccept_never_reuses_generation_key(int editClockOffsetTicks)
    {
        var seeded = await SeedAtStart();
        var clock = new Clock(Start);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, clock);
        using var client = fixture.Client(app, out _);
        using var first = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstState = await State(seeded.Id);
        var firstOutcome = await Outcome(seeded.Id);
        Assert.Equal(Start.UtcDateTime.Ticks, firstOutcome.AcceptedUtc.AddTicks(firstOutcome.AcceptedUtcSubMicrosecondTicks).Ticks);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var decline = await Decision(client, await Read(client, seeded.Id), false);
            Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
            var declined = await State(seeded.Id);
            Assert.False(declined.Accepted);
            Assert.Null(declined.DecisionOrderVersion);
            clock.Value = Start.AddTicks(editClockOffsetTicks);
            using var edit = await Edit(client, await Read(client, seeded.Id), $"high-water edit {cycle}");
            Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
            var edited = await State(seeded.Id);
            Assert.False(edited.Accepted);
            Assert.Null(edited.DecisionOrderVersion);
            Assert.Equal($"high-water edit {cycle}", edited.Comment);
            output.WriteLine($"cycle={cycle}; declineTicks={declined.ModifiedDate!.Value.Ticks}; editTicks={edited.ModifiedDate!.Value.Ticks}");
            using var accepted = await Decision(client, await Read(client, seeded.Id), true);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var final = await State(seeded.Id);
        Assert.True(final.Accepted);
        Assert.Null(final.InvoiceId);
        Assert.Null(final.DecisionOrderVersion);
        Assert.Equal(firstState.AcceptedUtc, final.AcceptedUtc);
        Assert.Equal(firstState.AcceptanceOrigin, final.AcceptanceOrigin);
        var retained = await Outcome(seeded.Id);
        Assert.Equal(firstOutcome.Id, retained.Id);
        Assert.Equal(firstOutcome.AcceptedUtc, retained.AcceptedUtc);
        Assert.Equal(firstOutcome.AcceptedUtcSubMicrosecondTicks, retained.AcceptedUtcSubMicrosecondTicks);
        Assert.Equal(seeded.SourceRequestId, retained.SourceRequestId);
        Assert.Equal(seeded.SourceJourneyId, retained.SourceJourneyId);
        var calls = orders.Requests.ToArray();
        Assert.Equal(5, calls.Length);
        var acceptedKeys = calls.Where(call => call.Path.EndsWith("/accepted", StringComparison.Ordinal)).Select(call => call.Key).ToArray();
        Assert.Equal(3, acceptedKeys.Length);
        output.WriteLine("acceptedKeys=" + string.Join(",", acceptedKeys));
        Assert.Equal(3, acceptedKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task Unchanged_acceptance_replay_retains_one_key_version_and_outcome(int replayClockOffsetTicks)
    {
        var seeded = await SeedAtStart();
        var clock = new Clock(Start);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, clock);
        using var client = fixture.Client(app, out _);
        using var first = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstState = await State(seeded.Id);
        var outcome = await Outcome(seeded.Id);
        clock.Value = Start.AddTicks(replayClockOffsetTicks);
        using var replay = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstState.ModifiedDate, (await State(seeded.Id)).ModifiedDate);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Equal(2, orders.Requests.Count);
        Assert.Single(orders.Requests.Select(call => call.Key).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Historical_accepted_ordinary_edit_freezes_original_key_even_with_backward_clock()
    {
        var seeded = await SeedAtStart(true);
        var clock = new Clock(Start.AddTicks(-50));
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, clock);
        using var client = fixture.Client(app, out _);
        var outcome = await Outcome(seeded.Id);
        using var first = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var edit = await Edit(client, await Read(client, seeded.Id), "historical high-water control");
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        using var replay = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var row = await State(seeded.Id);
        Assert.Equal(DateTime.SpecifyKind(Start.UtcDateTime, DateTimeKind.Unspecified), row.DecisionOrderVersion);
        Assert.Equal(seeded.AcceptedUtc, row.AcceptedUtc);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Equal(2, orders.Requests.Count);
        Assert.Single(orders.Requests.Select(call => call.Key).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stale_ordinary_edit_cannot_lower_version_or_change_state_and_next_decision_is_distinct()
    {
        var seeded = await SeedAtStart();
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        var stale = await Read(client, seeded.Id);
        using var first = await Decision(client, stale, true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var decline = await Decision(client, await Read(client, seeded.Id), false);
        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        var before = await State(seeded.Id);
        using var edit = await Edit(client, stale with { Accepted = false }, "must not persist");
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        var unchanged = await State(seeded.Id);
        Assert.Equal(before.ModifiedDate, unchanged.ModifiedDate);
        Assert.Equal(before.Comment, unchanged.Comment);
        Assert.False(unchanged.Accepted);
        Assert.Null(unchanged.DecisionOrderVersion);
        Assert.Equal(2, orders.Requests.Count);
        using var accepted = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(3, orders.Requests.Select(call => call.Key).Distinct(StringComparer.Ordinal).Count());
        await Outcome(seeded.Id);
    }

    [Fact]
    public async Task Stale_changed_decision_is_conflict_without_order_call_or_outcome_change()
    {
        var seeded = await SeedAtStart();
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        var stale = await Read(client, seeded.Id);
        using var first = await Decision(client, stale, true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = await State(seeded.Id);
        var outcome = await Outcome(seeded.Id);
        using var decline = await Decision(client, stale, false);
        Assert.Equal(HttpStatusCode.Conflict, decline.StatusCode);
        var unchanged = await State(seeded.Id);
        Assert.True(unchanged.Accepted);
        Assert.Equal(before.ModifiedDate, unchanged.ModifiedDate);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Single(orders.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task Ordinary_edits_advance_persisted_microsecond_high_water_without_creating_events(int clockOffsetTicks)
    {
        var seeded = await SeedAtStart(false);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start.AddTicks(clockOffsetTicks)));
        using var client = fixture.Client(app, out _);
        for (var edit = 1; edit <= 2; edit++)
        {
            using var response = await Edit(client, await Read(client, seeded.Id), $"ordinary high-water {edit}");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var row = await State(seeded.Id);
            Assert.Equal(Start.UtcDateTime.Ticks + edit * 10, row.ModifiedDate!.Value.Ticks);
            Assert.Equal(0, row.ModifiedDate.Value.Ticks % 10);
            Assert.False(row.Accepted);
            Assert.Null(row.DecisionOrderVersion);
            Assert.Null(row.AcceptedUtc);
        }
        Assert.Empty(orders.Requests);
        await using var fresh = fixture.Context();
        Assert.Empty(await fresh.AcceptedOutcomes.Where(row => row.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Exhausted_ordinary_version_returns_opaque_500_without_saving_payload_or_binding()
    {
        var seeded = await SeedAtStart(true);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.ModifiedDate = DateTime.MaxValue;
            await setup.SaveChangesAsync();
        }
        var before = await State(seeded.Id);
        var outcome = await Outcome(seeded.Id);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        using var response = await Edit(client, await Read(client, seeded.Id), "must not save exhausted payload");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OverflowException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot advance", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must not save", body, StringComparison.Ordinal);
        var retained = await State(seeded.Id);
        Assert.Equal(before.ModifiedDate, retained.ModifiedDate);
        Assert.Equal(before.Comment, retained.Comment);
        Assert.Null(retained.DecisionOrderVersion);
        Assert.True(retained.Accepted);
        Assert.Equal(before.AcceptedUtc, retained.AcceptedUtc);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Empty(orders.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Historical_authoritative_version_survives_reset_without_generation_key_reuse(bool retainedBinding, bool ordinaryEdit)
    {
        var seeded = await SeedAtStart(true);
        var historical = DateTime.SpecifyKind(Start.UtcDateTime.AddTicks(retainedBinding ? ordinaryEdit ? 30 : 20 : ordinaryEdit ? 20 : 10), DateTimeKind.Unspecified);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.ModifiedDate = retainedBinding ? DateTime.SpecifyKind(Start.UtcDateTime, DateTimeKind.Unspecified) : null;
            row.CreatedDate = retainedBinding ? DateTime.SpecifyKind(Start.UtcDateTime.AddHours(-1), DateTimeKind.Unspecified) : historical;
            row.DecisionOrderVersion = retainedBinding ? historical : null;
            await setup.SaveChangesAsync();
        }
        var originalOutcome = await Outcome(seeded.Id);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        var current = await Read(client, seeded.Id);
        using var initial = await DecisionNullableVersion(client, current, true);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var originalKey = Assert.Single(orders.Requests).Key;
        Assert.Equal($"quotation-{seeded.Id}-accepted-{historical.Ticks:x}-order-701", originalKey);
        if (ordinaryEdit)
        {
            using var edited = await EditNullableVersion(client, await Read(client, seeded.Id), "historical future-key edit");
            Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
            Assert.Equal(historical.Ticks, (await State(seeded.Id)).DecisionOrderVersion!.Value.Ticks);
        }
        using var decline = await DecisionNullableVersion(client, await Read(client, seeded.Id), false);
        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        var declined = await State(seeded.Id);
        Assert.False(declined.Accepted);
        Assert.Null(declined.DecisionOrderVersion);
        using var accepted = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var final = await State(seeded.Id);
        Assert.True(final.Accepted);
        Assert.Null(final.DecisionOrderVersion);
        Assert.Equal(seeded.AcceptedUtc, final.AcceptedUtc);
        Assert.Equal(originalOutcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Equal(3, orders.Requests.Count);
        var acceptedKeys = orders.Requests.Where(call => call.Path.EndsWith("/accepted", StringComparison.Ordinal)).Select(call => call.Key).ToArray();
        output.WriteLine($"historicalTicks={historical.Ticks}; declineTicks={declined.ModifiedDate!.Value.Ticks}; finalTicks={final.ModifiedDate!.Value.Ticks}; acceptedKeys={string.Join(',', acceptedKeys)}");
        Assert.Equal(2, acceptedKeys.Length);
        Assert.Equal(2, acceptedKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Ordinary_false_mapping_retains_bound_high_water_for_next_acceptance()
    {
        var seeded = await SeedAtStart(true);
        var historical = DateTime.SpecifyKind(Start.UtcDateTime.AddTicks(20), DateTimeKind.Unspecified);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.DecisionOrderVersion = historical;
            await setup.SaveChangesAsync();
        }
        var outcome = await Outcome(seeded.Id);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        using var initial = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var edit = await Edit(client, (await Read(client, seeded.Id)) with { Accepted = false }, "ordinary false retains historical binding");
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        var edited = await State(seeded.Id);
        Assert.False(edited.Accepted);
        Assert.Equal(historical.Ticks, edited.DecisionOrderVersion!.Value.Ticks);
        using var accepted = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var final = await State(seeded.Id);
        Assert.True(final.Accepted);
        Assert.Null(final.DecisionOrderVersion);
        Assert.Equal(seeded.AcceptedUtc, final.AcceptedUtc);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Equal(2, orders.Requests.Count);
        output.WriteLine($"boundTicks={historical.Ticks}; editedTicks={edited.ModifiedDate!.Value.Ticks}; finalTicks={final.ModifiedDate!.Value.Ticks}");
        Assert.Equal(2, orders.Requests.Select(call => call.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Future_created_date_is_not_authoritative_when_modified_is_nonnull()
    {
        var seeded = await SeedAtStart(true);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.CreatedDate = DateTime.SpecifyKind(Start.UtcDateTime.AddYears(1), DateTimeKind.Unspecified);
            await setup.SaveChangesAsync();
        }
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        using var initial = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.Equal($"quotation-{seeded.Id}-accepted-{Start.UtcDateTime.Ticks:x}-order-701", Assert.Single(orders.Requests).Key);
        using var edit = await Edit(client, await Read(client, seeded.Id), "ignore unrelated future CreatedDate");
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        Assert.Equal(Start.UtcDateTime.Ticks + 10, (await State(seeded.Id)).ModifiedDate!.Value.Ticks);
        using var decline = await Decision(client, await Read(client, seeded.Id), false);
        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        using var accepted = await Decision(client, await Read(client, seeded.Id), true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Start.UtcDateTime.Ticks + 30, (await State(seeded.Id)).ModifiedDate!.Value.Ticks);
        Assert.Equal(3, orders.Requests.Select(call => call.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_retained_binding_fails_before_ordinary_or_decision_mutation(bool changedDecision)
    {
        var seeded = await SeedAtStart(true);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
            row.DecisionOrderVersion = DateTime.MaxValue;
            await setup.SaveChangesAsync();
        }
        var before = await State(seeded.Id);
        var outcome = await Outcome(seeded.Id);
        var orders = new InvoiceOrderTransport();
        await using var original = fixture.App(orders);
        await using var app = AtClock(original, new Clock(Start));
        using var client = fixture.Client(app, out _);
        var current = await Read(client, seeded.Id);
        using var response = changedDecision
            ? await Decision(client, current, false)
            : await Edit(client, current, "must not save exhausted binding");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("OverflowException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot advance", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must not save", body, StringComparison.Ordinal);
        var retained = await State(seeded.Id);
        Assert.Equal(before.ModifiedDate, retained.ModifiedDate);
        Assert.Equal(DateTime.MaxValue, retained.DecisionOrderVersion);
        Assert.Equal(before.Comment, retained.Comment);
        Assert.True(retained.Accepted);
        Assert.Equal(before.AcceptedUtc, retained.AcceptedUtc);
        Assert.Equal(outcome.Id, (await Outcome(seeded.Id)).Id);
        Assert.Empty(orders.Requests);
    }

    private static Task<HttpResponseMessage> DecisionNullableVersion(HttpClient client, QuotationResponse current, bool accepted) =>
        current.ModifiedDate is null
            ? client.PutAsJsonAsync($"/quotations/{current.Id}/decision", new { Accepted = accepted, EmployeeInitiated = true })
            : Decision(client, current, accepted);

    private static Task<HttpResponseMessage> EditNullableVersion(HttpClient client, QuotationResponse current, string comment) =>
        current.ModifiedDate is null
            ? client.PutAsJsonAsync($"/quotations/{current.Id}", new UpsertQuotationRequest(current.CustomerId, current.EmployeeId,
                current.InvoiceId, current.Period, current.ExpirationDate, current.Subtotal, current.Vat, current.Total,
                current.WithholdingTax, current.CurrencyId, comment, current.Fob, current.ShippedVia, current.Terms, current.Accepted))
            : Edit(client, current, comment);

    private async Task<Quotation> SeedAtStart(bool? accepted = null)
    {
        var seeded = await fixture.SeedAsync(accepted);
        await using var setup = fixture.Context();
        var row = await setup.Quotations.SingleAsync(value => value.Id == seeded.Id);
        row.ModifiedDate = DateTime.SpecifyKind(Start.UtcDateTime, DateTimeKind.Unspecified);
        await setup.SaveChangesAsync();
        return row;
    }

    private async Task<Quotation> State(int id)
    {
        await using var db = fixture.Context();
        return await db.Quotations.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private async Task<QuotationAcceptedOutcome> Outcome(int id)
    {
        await using var db = fixture.Context();
        return Assert.Single(await db.AcceptedOutcomes.AsNoTracking().Where(row => row.QuotationId == id).ToListAsync());
    }

    private static async Task<QuotationResponse> Read(HttpClient client, int id)
    {
        using var response = await client.GetAsync($"/quotations/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<QuotationResponse>(await response.Content.ReadFromJsonAsync<QuotationResponse>());
    }

    private static Task<HttpResponseMessage> Decision(HttpClient client, QuotationResponse current, bool accepted) =>
        Send(client, $"/quotations/{current.Id}/decision", new { Accepted = accepted, EmployeeInitiated = true }, current.ModifiedDate);

    private static Task<HttpResponseMessage> Edit(HttpClient client, QuotationResponse current, string comment) =>
        Send(client, $"/quotations/{current.Id}", new UpsertQuotationRequest(current.CustomerId, current.EmployeeId,
            current.InvoiceId, current.Period, current.ExpirationDate, current.Subtotal, current.Vat, current.Total,
            current.WithholdingTax, current.CurrencyId, comment, current.Fob, current.ShippedVia, current.Terms, current.Accepted), current.ModifiedDate);

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object payload, DateTime? expected)
    {
        Assert.NotNull(expected);
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-Expected-Modified-Date", new DateTimeOffset(DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc)).ToString("O"));
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> AtClock(WebApplicationFactory<Program> original, Clock clock) =>
        original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));

    private sealed class Clock(DateTimeOffset value) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = value;
        public override DateTimeOffset GetUtcNow() => Value;
    }
}
