using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class RequestJourneyMigrationContractTests
{
    [Fact]
    public void JourneySchema_IsOptionalUuidWithSourceFilteredIndexAndAdditiveMigration()
    {
        using var context = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only").Options);
        var entity = context.Model.FindEntityType(typeof(QuotationRequest))!;
        var property = entity.FindProperty(nameof(QuotationRequest.JourneyId))!;
        Assert.True(property.IsNullable);
        Assert.Equal("uuid", property.GetColumnType());
        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal("IX_Request_JourneyId", index.GetDatabaseName());
        Assert.Equal("\"JourneyId\" IS NOT NULL", index.GetFilter());
        var migrations = context.GetService<IMigrationsAssembly>();
        var migration = migrations.CreateMigration(migrations.Migrations["20260906120000_AddRequestJourneyId"], context.Database.ProviderName!);
        var column = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
        Assert.Equal("JourneyId", column.Name);
        Assert.True(column.IsNullable);
        Assert.Equal("uuid", column.ColumnType);
        Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.DoesNotContain(migration.UpOperations, operation => operation is DropColumnOperation or DropTableOperation or AlterColumnOperation);
        Assert.Throws<NotSupportedException>(() => migration.DownOperations);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void JourneyWire_IsOptionalPascalCaseAndPreservesNullableDone()
    {
        var journey = Guid.NewGuid();
        var value = new QuotationRequestResponse(1, null, null, null, null, null, null, null, null, null, null, null, null, journey);
        var options = new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value, options));
        Assert.Equal(journey, json.RootElement.GetProperty("JourneyId").GetGuid());
        Assert.False(json.RootElement.TryGetProperty("Done", out _));
        var omitted = System.Text.Json.JsonSerializer.Deserialize<UpsertQuotationRequestRequest>("{}")!;
        Assert.Null(omitted.JourneyId);
        Assert.Null(omitted.Done);
    }
}
