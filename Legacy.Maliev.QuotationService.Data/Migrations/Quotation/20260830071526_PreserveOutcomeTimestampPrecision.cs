using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation
{
    /// <inheritdoc />
    public partial class PreserveOutcomeTimestampPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "AcceptedUtcSubMicrosecondTicks",
                table: "QuotationAcceptedOutcome",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Accepted outcome timestamp precision is immutable; use an explicitly reviewed compensating migration instead of a destructive downgrade.");
        }
    }
}
