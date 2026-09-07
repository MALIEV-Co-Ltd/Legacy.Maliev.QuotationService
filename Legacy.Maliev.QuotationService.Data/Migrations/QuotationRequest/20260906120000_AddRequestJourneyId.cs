using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest;

/// <summary>Preserves optional source quotation journey attribution on PostgreSQL.</summary>
public partial class AddRequestJourneyId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "JourneyId", table: "Request", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Request_JourneyId", table: "Request", column: "JourneyId", filter: "\"JourneyId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Journey attribution cannot be dropped safely. Use a reviewed forward repair migration.");
    }
}
