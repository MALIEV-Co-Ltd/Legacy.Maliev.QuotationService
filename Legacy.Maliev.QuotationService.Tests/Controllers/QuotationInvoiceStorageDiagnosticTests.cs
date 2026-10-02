using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Characterizes the actual two-database initialization with its unchanged storage cap.</summary>
public sealed class QuotationInvoiceStorageDiagnosticTests(InvoiceConsumerFixture fixture, ITestOutputHelper output)
    : IClassFixture<InvoiceConsumerFixture>
{
    [Fact]
    public async Task Fresh_bounded_storage_reaches_both_migrations_and_persisted_readback()
    {
        foreach (var metadata in fixture.StorageDiagnostics) output.WriteLine(metadata);
        var seeded = await fixture.SeedAsync();
        await using var readback = fixture.Context();
        var row = await readback.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(100m, row.Subtotal);
        Assert.Equal(107m, row.Total);
        Assert.Null(row.Accepted);
        Assert.Null(row.InvoiceId);
        Assert.Equal(701, (await readback.OrderLinks.AsNoTracking().SingleAsync(value => value.QuotationId == row.Id)).OrderId);
    }
}
