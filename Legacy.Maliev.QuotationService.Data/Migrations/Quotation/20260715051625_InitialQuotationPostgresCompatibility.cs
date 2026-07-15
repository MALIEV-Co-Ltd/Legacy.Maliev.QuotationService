using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation
{
    /// <inheritdoc />
    public partial class InitialQuotationPostgresCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Quotation",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerID = table.Column<int>(type: "integer", nullable: true),
                    EmployeeID = table.Column<int>(type: "integer", nullable: true),
                    InvoiceID = table.Column<int>(type: "integer", nullable: true),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Vat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WithholdingTax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    QuotedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true, computedColumnSql: "(\"Total\" - \"WithholdingTax\")::numeric(18,2)", stored: true),
                    CurrencyID = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    FOB = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ShippedVia = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Terms = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Accepted = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotation", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "OrderItem",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuotationID = table.Column<int>(type: "integer", nullable: false),
                    OrderID = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true, computedColumnSql: "(\"UnitPrice\" * \"Quantity\")::numeric(18,2)", stored: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItem", x => x.ID);
                    table.ForeignKey(
                        name: "FK_OrderItem_Quotation",
                        column: x => x.QuotationID,
                        principalTable: "Quotation",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "QuotationFile",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuotationID = table.Column<int>(type: "integer", nullable: false),
                    Bucket = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ObjectName = table.Column<string>(type: "text", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationFile", x => x.ID);
                    table.ForeignKey(
                        name: "FK_QuotationFile_Quotation",
                        column: x => x.QuotationID,
                        principalTable: "Quotation",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "QuotationHasOrder",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuotationID = table.Column<int>(type: "integer", nullable: false),
                    OrderID = table.Column<int>(type: "integer", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationHasOrder", x => x.ID);
                    table.ForeignKey(
                        name: "FK_QuotationHasOrder_Quotation",
                        column: x => x.QuotationID,
                        principalTable: "Quotation",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItem_QuotationID",
                table: "OrderItem",
                column: "QuotationID");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationFile_QuotationID",
                table: "QuotationFile",
                column: "QuotationID");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationHasOrder_QuotationID",
                table: "QuotationHasOrder",
                column: "QuotationID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItem");

            migrationBuilder.DropTable(
                name: "QuotationFile");

            migrationBuilder.DropTable(
                name: "QuotationHasOrder");

            migrationBuilder.DropTable(
                name: "Quotation");
        }
    }
}
