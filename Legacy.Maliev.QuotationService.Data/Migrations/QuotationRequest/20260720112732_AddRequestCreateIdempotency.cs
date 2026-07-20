using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest
{
    /// <inheritdoc />
    public partial class AddRequestCreateIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestCreateIdempotency",
                columns: table => new
                {
                    KeyHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RequestID = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestCreateIdempotency", x => x.KeyHash);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestCreateIdempotency");
        }
    }
}
