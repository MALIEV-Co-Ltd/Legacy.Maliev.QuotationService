using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest
{
    /// <inheritdoc />
    public partial class InitialQuotationRequestPostgresCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Request",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FirstName = table.Column<string>(type: "text", nullable: true),
                    LastName = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    TelephoneNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Country = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CompanyName = table.Column<string>(type: "text", nullable: true),
                    TaxIdentification = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Message = table.Column<string>(type: "text", nullable: true),
                    InternalComment = table.Column<string>(type: "text", nullable: true),
                    Done = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Request", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "RequestFile",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestID = table.Column<int>(type: "integer", nullable: true),
                    Bucket = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ObjectName = table.Column<string>(type: "text", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestFile", x => x.ID);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Request");

            migrationBuilder.DropTable(
                name: "RequestFile");
        }
    }
}
