using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Analytics;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Data.Migrations.Quotation;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Legacy.Maliev.QuotationService.Tests.Analytics;

/// <summary>Original source reconciliation contracts without a database connection or container fixture.</summary>
public sealed class QuotationAnalyticsSourceContractTests
{
    /// <summary>A quotation without source keys retains its own transaction ID and omits both optional reconciliation fields.</summary>
    [Fact]
    public void Payload_WithoutSource_OmitsSourceReconciliationFields()
    {
        var row = new GoogleAnalyticsOutbox
        {
            QuotationId = 43, EventKey = "quotation-43:close_convert_lead:v1", EventName = "close_convert_lead",
            ClientId = "123.456", SessionId = "789", Currency = "THB", Value = 10m,
            OccurredUtc = new DateTime(2026, 8, 12, 3, 4, 5, DateTimeKind.Utc),
        };
        using var document = JsonDocument.Parse(GoogleAnalyticsPayloadFactory.Build(row));
        var parameters = Assert.Single(document.RootElement.GetProperty("events").EnumerateArray()).GetProperty("params");
        Assert.Equal("quotation-43", parameters.GetProperty("transaction_id").GetString());
        Assert.False(parameters.TryGetProperty("source_transaction_id", out _));
        Assert.False(parameters.TryGetProperty("journey_id", out _));
    }

    /// <summary>Preserves nullable source types, physical column names and the two independent original non-null index predicates.</summary>
    [Fact]
    public void Outbox_Model_PreservesNullableSourceColumnsAndPartialIndexes()
    {
        using var context = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql().Options);
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GoogleAnalyticsOutbox));
        Assert.NotNull(entity);
        var table = StoreObjectIdentifier.Table("GoogleAnalyticsOutbox", entity.GetSchema());
        var request = entity.FindProperty(nameof(GoogleAnalyticsOutbox.SourceRequestId));
        var journey = entity.FindProperty(nameof(GoogleAnalyticsOutbox.SourceJourneyId));
        Assert.NotNull(request);
        Assert.NotNull(journey);
        Assert.Equal(typeof(int?), request.ClrType);
        Assert.Equal(typeof(Guid?), journey.ClrType);
        Assert.True(request.IsNullable);
        Assert.True(journey.IsNullable);
        Assert.Equal("SourceRequestID", request.GetColumnName(table));
        Assert.Equal("SourceJourneyID", journey.GetColumnName(table));
        var requestIndex = Assert.Single(entity.GetIndexes().Where(index => index.Properties.Count == 1 && index.Properties[0] == request));
        var journeyIndex = Assert.Single(entity.GetIndexes().Where(index => index.Properties.Count == 1 && index.Properties[0] == journey));
        Assert.False(requestIndex.IsUnique);
        Assert.False(journeyIndex.IsUnique);
        Assert.Equal("IX_GoogleAnalyticsOutbox_SourceRequestID", requestIndex.GetDatabaseName());
        Assert.Equal("IX_GoogleAnalyticsOutbox_SourceJourneyID", journeyIndex.GetDatabaseName());
        Assert.Equal("\"SourceRequestID\" IS NOT NULL", requestIndex.GetFilter());
        Assert.Equal("\"SourceJourneyID\" IS NOT NULL", journeyIndex.GetFilter());
    }

    /// <summary>The forward and rollback plans change only the two indexes and create replacements before dropping current definitions.</summary>
    [Fact]
    public void SourceIndexMigration_OnlyReplacesTheTwoIndexesAndRestoresNames()
    {
        var migration = new PreserveAnalyticsSourceIndexPredicates();
        VerifyOperations(migration.UpOperations, "Partial", filtered: true);
        VerifyOperations(migration.DownOperations, "Unfiltered", filtered: false);
    }

    private static void VerifyOperations(IReadOnlyList<MigrationOperation> operations, string suffix, bool filtered)
    {
        Assert.Equal(6, operations.Count);
        var columns = new[] { "SourceRequestID", "SourceJourneyID" };
        for (var index = 0; index < columns.Length; index++)
        {
            var column = columns[index];
            var originalName = "IX_GoogleAnalyticsOutbox_" + column;
            var temporaryName = originalName + "_" + suffix;
            var create = Assert.IsType<CreateIndexOperation>(operations[index]);
            Assert.Equal("GoogleAnalyticsOutbox", create.Table);
            Assert.Equal(temporaryName, create.Name);
            Assert.Equal(new[] { column }, create.Columns);
            Assert.False(create.IsUnique);
            Assert.Equal(filtered ? "\"" + column + "\" IS NOT NULL" : null, create.Filter);
            var drop = Assert.IsType<DropIndexOperation>(operations[2 + index * 2]);
            Assert.Equal("GoogleAnalyticsOutbox", drop.Table);
            Assert.Equal(originalName, drop.Name);
            var rename = Assert.IsType<RenameIndexOperation>(operations[3 + index * 2]);
            Assert.Equal("GoogleAnalyticsOutbox", rename.Table);
            Assert.Equal(temporaryName, rename.Name);
            Assert.Equal(originalName, rename.NewName);
        }
    }
}
