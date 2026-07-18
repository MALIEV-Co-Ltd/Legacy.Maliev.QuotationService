using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class QuotationPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer quotationPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer requestPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => Task.WhenAll(quotationPostgres.StartAsync(), requestPostgres.StartAsync());
    public async Task DisposeAsync() { await quotationPostgres.DisposeAsync(); await requestPostgres.DisposeAsync(); }

    [Fact]
    public async Task InitialMigrations_PreserveFinancialRequestFileAndDocumentBehavior()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);
        var line = await repository.CreateOrderItemAsync(new(quotation.Id, null, "Legacy line", 2, 50m), CancellationToken.None);
        var file = await repository.CreateQuotationFileAsync(quotation.Id, "legacy-quotes", "quotes/1.pdf", CancellationToken.None);
        var order = await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        var request = await repository.CreateRequestAsync(new("Ada", "Lovelace", "ada@example.test", "02", "TH", "MALIEV", "TAX", "quote", "internal", null), CancellationToken.None);
        var requestFile = await repository.CreateRequestFileAsync(request.Id, "legacy-requests", "requests/1.stl", CancellationToken.None);
        var loaded = await repository.GetQuotationAsync(quotation.Id, CancellationToken.None);
        var snapshot = await repository.GetDocumentSnapshotAsync(quotation.Id, CancellationToken.None);

        Assert.Equal(104m, loaded?.QuotedAmount); Assert.Equal(100m, line.Subtotal); Assert.Equal("quotes/1.pdf", file?.ObjectName); Assert.Equal(77, order?.OrderId);
        Assert.Equal("Ada", request.FirstName); Assert.Equal("requests/1.stl", requestFile?.ObjectName);
        Assert.Single(snapshot!.OrderItems); Assert.Single(snapshot.Files);
        Assert.Equal(4, await quotationContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Quotation','OrderItem','QuotationFile','QuotationHasOrder')").SingleAsync());
        Assert.Equal(2, await requestContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Request','RequestFile')").SingleAsync());
    }

    [Fact]
    public async Task ExpiryStatsAndModifiedDateConcurrency_AreAtomicAndDeterministic()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync()); var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(expiration: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        var stale = quotation.ModifiedDate!.Value;
        Assert.Equal(1, await repository.DeclineExpiredAsync(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));
        quotationContext.ChangeTracker.Clear();
        Assert.Equal(UpdateResult.Conflict, await repository.UpdateQuotationAsync(quotation.Id, QuotationRequest(), new DateTimeOffset(stale), CancellationToken.None));
        var stats = await repository.GetStatsAsync(CancellationToken.None); Assert.Equal(0, stats.Accepted); Assert.Equal(1, stats.Declined); Assert.Equal(0, stats.Open);
        Assert.Equal(3m, await repository.GetWithholdingTaxAsync(quotation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CustomerQuotationDetails_EnforceOwnershipAndComposeReadOnlyMetadata()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(
            QuotationRequest(customerId: 42),
            CancellationToken.None);
        await repository.CreateOrderItemAsync(
            new(quotation.Id, 77, "Owned line", 2, 50m),
            CancellationToken.None);
        await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        await repository.CreateQuotationFileAsync(
            quotation.Id,
            "legacy-quotes",
            "quotes/owned.pdf",
            CancellationToken.None);

        Assert.Null(await repository.GetCustomerQuotationAsync(41, quotation.Id, CancellationToken.None));
        var details = await repository.GetCustomerQuotationAsync(42, quotation.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(42, details.Quotation.CustomerId);
        Assert.Equal("Owned line", Assert.Single(details.OrderItems).Description);
        Assert.Equal(77, Assert.Single(details.Orders).OrderId);
        Assert.Equal("quotes/owned.pdf", Assert.Single(details.Files).ObjectName);
    }

    private QuotationRepository Repository(QuotationDbContext quotations, QuotationRequestDbContext requests)
    {
        var cache = new Mock<IQuotationCache>();
        cache.Setup(x => x.GetAsync<QuotationResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationResponse?)null);
        cache.Setup(x => x.GetAsync<QuotationRequestResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationRequestResponse?)null);
        return new(quotations, requests, cache.Object, TimeProvider.System);
    }

    private static UpsertQuotationRequest QuotationRequest(DateTime? expiration = null, int? customerId = null) => new(customerId, null, null, 30, expiration ?? new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), 100m, 7m, 107m, 3m, 764, "legacy", "FOB", "Courier", "30 days", null);
    private QuotationDbContext QuotationContext() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(quotationPostgres.GetConnectionString()).Options);
    private QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(requestPostgres.GetConnectionString()).Options);
}
