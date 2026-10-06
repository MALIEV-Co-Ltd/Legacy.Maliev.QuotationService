using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class QuotationModelCompatibilityTests
{
    [Fact]
    public void Models_PreserveTwoDatabaseOwnershipAndComputedFinancialColumns()
    {
        using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql("Host=localhost;Database=model-q").Options);
        using var request = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql("Host=localhost;Database=model-r").Options);
        var q = quotation.Model.FindEntityType(typeof(Quotation))!;
        var line = quotation.Model.FindEntityType(typeof(QuotationOrderItem))!;

        Assert.Equal("Quotation", q.GetTableName()); Assert.Contains("WithholdingTax", q.FindProperty(nameof(Quotation.QuotedAmount))!.GetComputedColumnSql());
        Assert.Contains("Quantity", line.FindProperty(nameof(QuotationOrderItem.Subtotal))!.GetComputedColumnSql());
        Assert.True(q.FindProperty(nameof(Quotation.ModifiedDate))!.IsConcurrencyToken); Assert.True(line.FindProperty(nameof(QuotationOrderItem.ModifiedDate))!.IsConcurrencyToken);
        Assert.Equal("Request", request.Model.FindEntityType(typeof(QuotationRequest))!.GetTableName());
        Assert.Equal("RequestFile", request.Model.FindEntityType(typeof(QuotationRequestFile))!.GetTableName());
        Assert.Null(request.Model.FindEntityType(typeof(Quotation)));
    }

    [Fact]
    public void InvoiceOperationModel_MatchesSnapshotAndRetainsIndependentRecoveryEvidence()
    {
        using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql("Host=localhost;Database=model-q").Options);
        using var request = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql("Host=localhost;Database=model-r").Options);
        Assert.False(quotation.Database.HasPendingModelChanges());
        Assert.False(request.Database.HasPendingModelChanges());
        var receipt = quotation.Model.FindEntityType(typeof(QuotationInvoiceCompletionOperation))!;
        Assert.Equal("QuotationInvoiceCompletionOperation", receipt.GetTableName());
        Assert.Equal(nameof(QuotationInvoiceCompletionOperation.OperationId), Assert.Single(receipt.FindPrimaryKey()!.Properties).Name);
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique && index.Properties.Single().Name == "QuotationId");
        Assert.Empty(receipt.GetForeignKeys());
        Assert.Null(request.Model.FindEntityType(typeof(QuotationInvoiceCompletionOperation)));
        Assert.Equal("text", receipt.FindProperty(nameof(QuotationInvoiceCompletionOperation.DecisionOrderVersion))!.GetColumnType());
        Assert.Equal("text", receipt.FindProperty(nameof(QuotationInvoiceCompletionOperation.ModifiedDate))!.GetColumnType());
        Assert.Contains("20261006080000_QuotationInvoiceCompletionOperations", quotation.Database.GetMigrations());
    }
}
