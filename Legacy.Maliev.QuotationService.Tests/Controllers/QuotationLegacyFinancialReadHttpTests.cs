using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Legacy raw statistics, scalar invoice lookup and UTC withholding calculation through Production HTTP.</summary>
public sealed class QuotationLegacyFinancialReadHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Fact]
    public async Task Raw_accepted_declined_open_statistics_and_scalar_invoice_lookup_preserve_legacy_wire_shape()
    {
        var clock = Clock("2030-01-01T00:00:00+00:00");
        await using var app = App(clock);
        using var client = fixture.Client(app);
        using var beforeResponse = await client.GetAsync("/quotations/stats");
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        var before = (await beforeResponse.Content.ReadFromJsonAsync<QuotationStatsResponse>())!;
        var accepted = await Seed(clock, true, 9401);
        await Seed(clock, false, 9402);
        await Seed(clock, null, 9403);
        using var stats = await client.GetAsync("/quotations/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(new QuotationStatsResponse(before.Accepted + 1, before.Declined + 1, before.Open + 1),
            await stats.Content.ReadFromJsonAsync<QuotationStatsResponse>());
        using (var json = JsonDocument.Parse(await stats.Content.ReadAsStringAsync()))
            Assert.Equal(new[] { "Accepted", "Declined", "Open" }, json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        using var invoice = await client.GetAsync("/quotations/invoices/9401");
        Assert.Equal(HttpStatusCode.OK, invoice.StatusCode);
        var found = (await invoice.Content.ReadFromJsonAsync<QuotationResponse>())!;
        Assert.Equal(accepted.Id, found.Id);
        Assert.Equal(9401, found.InvoiceId);
        Assert.True(found.Accepted);
        Assert.Equal(97.55m, found.QuotedAmount);
        using (var json = JsonDocument.Parse(await invoice.Content.ReadAsStringAsync()))
        {
            Assert.Equal(accepted.Id, json.RootElement.GetProperty("Id").GetInt32());
            Assert.Equal(9401, json.RootElement.GetProperty("InvoiceId").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("invoiceId", out _));
            Assert.False(json.RootElement.TryGetProperty("Comment", out _));
        }
        await AssertUnchanged(accepted);
    }

    [Theory]
    [InlineData("2020-03-31T23:59:59.9999999+00:00", "3.015")]
    [InlineData("2020-04-01T00:00:00+00:00", "1.5075")]
    [InlineData("2020-09-29T23:59:59.9999999+00:00", "1.5075")]
    [InlineData("2020-09-30T00:00:00+00:00", "3.015")]
    [InlineData("2030-01-01T00:00:00+00:00", "3.015")]
    public async Task Withholding_HTTP_preserves_exact_legacy_UTC_cutoffs_subtotal_basis_and_unrounded_decimal(string instant, string amount)
    {
        var clock = Clock(instant);
        var row = await Seed(clock, null, null);
        await using var app = App(clock);
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotations/{row.Id}/withholdingtax");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<decimal>();
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), result);
        Assert.NotEqual(row.WithholdingTax, result);
        await AssertUnchanged(row);
    }

    [Fact]
    public async Task Missing_invoice_reference_or_quotation_preserves_notfound_without_fabricated_financial_value()
    {
        await using var app = App(Clock("2030-01-01T00:00:00+00:00"));
        using var client = fixture.Client(app);
        using (var missing = await client.GetAsync("/quotations/invoices/2147483647")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var missing = await client.GetAsync("/quotations/2147483647/withholdingtax")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_financial_read_routes_reject_missing_JWT_or_live_IAM_denial_without_disclosure_or_mutation(bool authenticated)
    {
        var clock = Clock("2030-01-01T00:00:00+00:00");
        var row = await Seed(clock, null, 9491);
        var before = fixture.LiveResources.Count;
        await using var app = App(clock, allowed: false);
        using var client = fixture.Client(app, authenticated);
        foreach (var route in new[] { "/quotations/stats", "/quotations/invoices/9491", $"/quotations/{row.Id}/withholdingtax" })
        {
            using var denied = await client.GetAsync(route);
            Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.DoesNotContain("Subtotal", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before + (authenticated ? 3 : 0), fixture.LiveResources.Count);
        await AssertUnchanged(row);
    }

    private static FakeTimeProvider Clock(string instant) => new(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));
    private WebApplicationFactory<Program> App(FakeTimeProvider clock, bool allowed = true) =>
        fixture.App(allowed: allowed).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));

    private async Task<Quotation> Seed(FakeTimeProvider clock, bool? accepted, int? invoice)
    {
        await using var db = fixture.Context();
        var row = new Quotation
        {
            CustomerId = 101, EmployeeId = 41, InvoiceId = invoice, CurrencyId = 764, Period = 30,
            ExpirationDate = new DateTime(2035, 1, 1), Subtotal = 100.50m, Vat = 7.04m, Total = 107.54m,
            WithholdingTax = 9.99m, Accepted = accepted,
            CreatedDate = DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified),
            ModifiedDate = new DateTime(2019, 1, 1)
        };
        db.Quotations.Add(row);
        await db.SaveChangesAsync();
        await db.Entry(row).ReloadAsync();
        return row;
    }

    private async Task AssertUnchanged(Quotation row)
    {
        await using var db = fixture.Context();
        var after = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal((row.CreatedDate, row.ModifiedDate, row.Accepted, row.InvoiceId, row.Subtotal, row.WithholdingTax),
            (after.CreatedDate, after.ModifiedDate, after.Accepted, after.InvoiceId, after.Subtotal, after.WithholdingTax));
        Assert.Empty(await db.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToArrayAsync());
        Assert.Empty(await db.GoogleAnalyticsOutbox.Where(value => value.QuotationId == row.Id).ToArrayAsync());
    }
}
