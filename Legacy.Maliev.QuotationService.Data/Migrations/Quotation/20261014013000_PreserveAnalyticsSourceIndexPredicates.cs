using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation;

/// <summary>Preserves the original non-null source-key index predicates without changing stored rows.</summary>
[DbContext(typeof(QuotationDbContext))]
[Migration("20261014013000_PreserveAnalyticsSourceIndexPredicates")]
public sealed class PreserveAnalyticsSourceIndexPredicates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Build both replacement indexes before removing either existing index.
        migrationBuilder.CreateIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceRequestID_Partial",
            table: "GoogleAnalyticsOutbox", column: "SourceRequestID",
            filter: "\"SourceRequestID\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceJourneyID_Partial",
            table: "GoogleAnalyticsOutbox", column: "SourceJourneyID",
            filter: "\"SourceJourneyID\" IS NOT NULL");
        migrationBuilder.DropIndex("IX_GoogleAnalyticsOutbox_SourceRequestID", "GoogleAnalyticsOutbox");
        migrationBuilder.RenameIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceRequestID_Partial", table: "GoogleAnalyticsOutbox",
            newName: "IX_GoogleAnalyticsOutbox_SourceRequestID");
        migrationBuilder.DropIndex("IX_GoogleAnalyticsOutbox_SourceJourneyID", "GoogleAnalyticsOutbox");
        migrationBuilder.RenameIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceJourneyID_Partial", table: "GoogleAnalyticsOutbox",
            newName: "IX_GoogleAnalyticsOutbox_SourceJourneyID");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Recreate the prior definitions before removing the partial indexes; no row or recovery evidence is discarded.
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_SourceRequestID_Unfiltered", "GoogleAnalyticsOutbox", "SourceRequestID");
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_SourceJourneyID_Unfiltered", "GoogleAnalyticsOutbox", "SourceJourneyID");
        migrationBuilder.DropIndex("IX_GoogleAnalyticsOutbox_SourceRequestID", "GoogleAnalyticsOutbox");
        migrationBuilder.RenameIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceRequestID_Unfiltered", table: "GoogleAnalyticsOutbox",
            newName: "IX_GoogleAnalyticsOutbox_SourceRequestID");
        migrationBuilder.DropIndex("IX_GoogleAnalyticsOutbox_SourceJourneyID", "GoogleAnalyticsOutbox");
        migrationBuilder.RenameIndex(
            name: "IX_GoogleAnalyticsOutbox_SourceJourneyID_Unfiltered", table: "GoogleAnalyticsOutbox",
            newName: "IX_GoogleAnalyticsOutbox_SourceJourneyID");
    }
}
