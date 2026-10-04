using System.Data;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Data;

[CollectionDefinition("Quotation design-time environment", DisableParallelization = true)]
public sealed class DesignTimeEnvironmentCollection;

/// <summary>Executes both accepted design-time factories without database connections or persisted environment changes.</summary>
[Collection("Quotation design-time environment")]
public sealed class DesignTimeExternalConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Factory_MissingExternalConfiguration_FailsWithoutSecretOrFallback(bool requestDatabase)
    {
        var name = requestDatabase ? "ConnectionStrings__QuotationRequestDbContext" : "ConnectionStrings__QuotationDbContext";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            var error = Assert.Throws<InvalidOperationException>(() => Create(requestDatabase));
            Assert.Equal($"{name} is required.", error.Message);
        }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Factory_ExternalConnection_BindsPostgreSqlWithoutOpeningDatabase(bool requestDatabase)
    {
        var name = requestDatabase ? "ConnectionStrings__QuotationRequestDbContext" : "ConnectionStrings__QuotationDbContext";
        const string connection = "Host=localhost;Port=1;Database=quotation_design_fixture;Username=fixture";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, connection);
            using var context = Create(requestDatabase);
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
            Assert.Equal(connection, context.Database.GetConnectionString());
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    private static DbContext Create(bool requestDatabase) => requestDatabase
        ? new QuotationRequestDbContextFactory().CreateDbContext([])
        : new QuotationDbContextFactory().CreateDbContext([]);
}
