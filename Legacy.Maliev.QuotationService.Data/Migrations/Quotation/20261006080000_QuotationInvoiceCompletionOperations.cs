using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation;

[DbContext(typeof(QuotationDbContext))]
[Migration("20261006080000_QuotationInvoiceCompletionOperations")]
public sealed class QuotationInvoiceCompletionOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("QuotationInvoiceCompletionOperation", columns: table => new
        {
            OperationId = table.Column<Guid>(type: "uuid", nullable: false),
            QuotationId = table.Column<int>(type: "integer", nullable: false),
            InvoiceId = table.Column<int>(type: "integer", nullable: false),
            AuthorityJson = table.Column<string>(type: "text", nullable: false),
            OrderIdsJson = table.Column<string>(type: "text", nullable: false),
            CompletedOrderIdsJson = table.Column<string>(type: "text", nullable: false),
            State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
            DecisionOrderVersion = table.Column<string>(type: "text", nullable: false),
            ModifiedDate = table.Column<string>(type: "text", nullable: false),
            ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
            ClaimUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
        }, constraints: table => table.PrimaryKey("PK_QuotationInvoiceCompletionOperation", x => x.OperationId));
        migrationBuilder.CreateIndex("IX_QuotationInvoiceCompletionOperation_QuotationId",
            "QuotationInvoiceCompletionOperation", "QuotationId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Operation recovery evidence cannot be discarded; use a reviewed compensating migration.");
}
