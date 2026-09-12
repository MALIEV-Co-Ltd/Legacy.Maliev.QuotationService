using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest
{
    /// <inheritdoc />
    public partial class AddRequestQualificationContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QualificationState",
                table: "Request",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "unreviewed");

            migrationBuilder.AddColumn<DateTime>(
                name: "QualificationStateChangedUtc",
                table: "Request",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualificationVersion",
                table: "Request",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TransactionId",
                table: "Request",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Request"
                SET "TransactionId" = 'request-' || "ID"::text
                WHERE "TransactionId" IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "RequestQualificationAudit",
                columns: table => new
                {
                    ID = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestID = table.Column<int>(type: "integer", nullable: false),
                    JourneyId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PreviousState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NewState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Completeness = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    UnmatchedClassification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChangedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestQualificationAudit", x => x.ID);
                    table.CheckConstraint("CK_RequestQualificationAudit_DuplicateCount", "\"DuplicateCount\" >= 0");
                    table.CheckConstraint("CK_RequestQualificationAudit_NewState", "\"NewState\" IN ('unreviewed', 'qualified', 'not_qualified', 'duplicate', 'stale', 'incomplete')");
                    table.CheckConstraint("CK_RequestQualificationAudit_Reason", "\"NewState\" = 'qualified' OR NULLIF(BTRIM(\"Reason\"), '') IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_RequestQualificationAudit_Request",
                        column: x => x.RequestID,
                        principalTable: "Request",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Request_TransactionId",
                table: "Request",
                column: "TransactionId",
                unique: true,
                filter: "\"TransactionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Request_QualificationState",
                table: "Request",
                sql: "\"QualificationState\" IN ('unreviewed', 'qualified', 'not_qualified', 'duplicate', 'stale', 'incomplete')");

            migrationBuilder.CreateIndex(
                name: "IX_RequestQualificationAudit_JourneyId",
                table: "RequestQualificationAudit",
                column: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "UX_RequestQualificationAudit_RequestID_IdempotencyKey",
                table: "RequestQualificationAudit",
                columns: new[] { "RequestID", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestQualificationAudit");

            migrationBuilder.DropIndex(
                name: "UX_Request_TransactionId",
                table: "Request");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Request_QualificationState",
                table: "Request");

            migrationBuilder.DropColumn(
                name: "QualificationState",
                table: "Request");

            migrationBuilder.DropColumn(
                name: "QualificationStateChangedUtc",
                table: "Request");

            migrationBuilder.DropColumn(
                name: "QualificationVersion",
                table: "Request");

            migrationBuilder.DropColumn(
                name: "TransactionId",
                table: "Request");
        }
    }
}
