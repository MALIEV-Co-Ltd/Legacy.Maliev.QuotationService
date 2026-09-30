using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation;

[DbContext(typeof(QuotationDbContext))]
[Migration("20260930070000_PreserveDecisionOrderVersion")]
public sealed class PreserveDecisionOrderVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<DateTime>(
        name: "DecisionOrderVersion",
        table: "Quotation",
        type: "timestamp without time zone",
        nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Dropping a bound order retry identity is unsafe; use a reviewed compensating migration.");
}
