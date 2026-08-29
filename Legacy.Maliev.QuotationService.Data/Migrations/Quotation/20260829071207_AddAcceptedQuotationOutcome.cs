using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation
{
    /// <inheritdoc />
    public partial class AddAcceptedQuotationOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptanceOrigin",
                table: "Quotation",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedUtc",
                table: "Quotation",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceJourneyID",
                table: "Quotation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceRequestID",
                table: "Quotation",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuotationAcceptedOutcome",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    QuotationID = table.Column<int>(type: "integer", nullable: false),
                    SourceRequestID = table.Column<int>(type: "integer", nullable: true),
                    SourceJourneyID = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AcceptanceOrigin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationAcceptedOutcome", x => x.ID);
                    table.ForeignKey(
                        name: "FK_QuotationAcceptedOutcome_Quotation",
                        column: x => x.QuotationID,
                        principalTable: "Quotation",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Quotation_SourceJourneyID",
                table: "Quotation",
                column: "SourceJourneyID");

            migrationBuilder.CreateIndex(
                name: "IX_Quotation_SourceRequestID",
                table: "Quotation",
                column: "SourceRequestID");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationAcceptedOutcome_AcceptedUtc",
                table: "QuotationAcceptedOutcome",
                column: "AcceptedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationAcceptedOutcome_EventKey",
                table: "QuotationAcceptedOutcome",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotationAcceptedOutcome_QuotationID",
                table: "QuotationAcceptedOutcome",
                column: "QuotationID");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationAcceptedOutcome_SourceJourneyID",
                table: "QuotationAcceptedOutcome",
                column: "SourceJourneyID");

            migrationBuilder.CreateIndex(
                name: "IX_QuotationAcceptedOutcome_SourceRequestID",
                table: "QuotationAcceptedOutcome",
                column: "SourceRequestID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Accepted quotation provenance is immutable; use an explicitly reviewed compensating migration instead of a destructive downgrade.");
        }
    }
}
