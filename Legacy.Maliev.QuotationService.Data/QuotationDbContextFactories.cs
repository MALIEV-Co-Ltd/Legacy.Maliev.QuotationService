using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Legacy.Maliev.QuotationService.Data;

public sealed class QuotationDbContextFactory : IDesignTimeDbContextFactory<QuotationDbContext>
{
    public QuotationDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Require("ConnectionStrings__QuotationDbContext")).Options);
    private static string Require(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
}

public sealed class QuotationRequestDbContextFactory : IDesignTimeDbContextFactory<QuotationRequestDbContext>
{
    public QuotationRequestDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Require("ConnectionStrings__QuotationRequestDbContext")).Options);
    private static string Require(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required.");
}
