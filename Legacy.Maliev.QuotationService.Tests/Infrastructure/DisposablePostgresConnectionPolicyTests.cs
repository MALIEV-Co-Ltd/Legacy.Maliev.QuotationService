using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

/// <summary>Real fixture consumers must not retain physical connectors across disposable lifetimes.</summary>
public sealed class DisposablePostgresConnectionPolicyTests
{
    [Fact]
    public void Policy_disables_pooling_without_changing_endpoint_or_database()
    {
        const string input = "Host=127.0.0.1;Port=5432;Database=synthetic_policy;Username=postgres";
        var actual = new NpgsqlConnectionStringBuilder(DisposablePostgresConnectionPolicy.Isolate(input));
        Assert.True(actual.Host == "127.0.0.1" && actual.Port == 5432 && actual.Database == "synthetic_policy",
            "Disposable connection endpoint/database must remain unchanged; values withheld.");
        Assert.False(actual.Pooling, "Disposable connection pooling must be explicitly disabled; values withheld.");
    }

    [Fact]
    public async Task Employee_fixture_direct_and_factory_contexts_use_isolated_connections()
    {
        var fixture = new QuotationEmployeeActorFixture();
        try
        {
            await fixture.InitializeAsync();
            using var app = fixture.App();
            using var client = app.CreateClient();
            using var scope = app.Services.CreateScope();
            await using var requests = fixture.RequestContext();
            var observed = new[]
            {
                Pooling(requests),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationDbContext>()),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationRequestDbContext>())
            };
            RequireIsolated(observed);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Draft_fixture_direct_and_factory_contexts_use_isolated_connections()
    {
        var fixture = new DraftReadFixture();
        try
        {
            await fixture.InitializeAsync();
            using var app = fixture.App(resourceScoped: false);
            using var client = app.CreateClient();
            using var scope = app.Services.CreateScope();
            await using var quotation = fixture.Context();
            var observed = new[]
            {
                Pooling(quotation),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationDbContext>()),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationRequestDbContext>())
            };
            RequireIsolated(observed);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Invoice_fixture_direct_and_factory_contexts_use_isolated_connections()
    {
        var fixture = new InvoiceConsumerFixture();
        try
        {
            await fixture.InitializeAsync();
            using var app = fixture.App();
            using var client = app.CreateClient();
            using var scope = app.Services.CreateScope();
            await using var quotation = fixture.Context();
            var observed = new[]
            {
                Pooling(quotation),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationDbContext>()),
                Pooling(scope.ServiceProvider.GetRequiredService<QuotationRequestDbContext>())
            };
            RequireIsolated(observed);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static bool Pooling(DbContext context)
    {
        var value = context.Database.GetConnectionString();
        Assert.False(string.IsNullOrEmpty(value), "Actual EF connection configuration must exist; values withheld.");
        return new NpgsqlConnectionStringBuilder(value!).Pooling;
    }

    private static void RequireIsolated(bool[] observed)
    {
        Assert.True(observed.Length == 3, "Direct and both real factory contexts must be inspected.");
        Assert.True(observed.All(pooling => !pooling),
            "Actual direct/factory disposable connections still enable pooling; values withheld.");
    }
}
