using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest
{
    /// <inheritdoc />
    public partial class AlignQualificationPrecisionWithSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestQualificationAudit_JourneyId",
                table: "RequestQualificationAudit");

            // SQL Server datetime2(7) has 100 ns precision. PostgreSQL timestamp has only
            // microseconds, so preserve the seven-digit wire value as text for future deltas.
            // Previously stored PostgreSQL timestamps cannot recover their lost seventh digit;
            // the guarded source-to-target row comparison must refresh those values.
            migrationBuilder.Sql("""
                ALTER TABLE "RequestQualificationAudit"
                    ALTER COLUMN "ChangedUtc" TYPE text
                    USING to_char("ChangedUtc", 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0';
                ALTER TABLE "Request"
                    ALTER COLUMN "QualificationStateChangedUtc" TYPE text
                    USING to_char("QualificationStateChangedUtc", 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RequestQualificationAudit_JourneyId",
                table: "RequestQualificationAudit",
                column: "JourneyId",
                filter: "\"JourneyId\" IS NOT NULL");

            migrationBuilder.Sql("""
                DROP INDEX "UX_Request_TransactionId";
                CREATE UNIQUE INDEX "UX_Request_TransactionId" ON "Request" ("TransactionId")
                    NULLS NOT DISTINCT WHERE "TransactionId" IS NOT NULL;
                """);

            // Keep the existing validation rules, but use the source-derived predicate
            // forms so the guarded delta plan can attest the actual PostgreSQL catalog.
            migrationBuilder.Sql("""
                ALTER TABLE "Request" DROP CONSTRAINT "CK_Request_QualificationState";
                ALTER TABLE "Request" ADD CONSTRAINT "CK_Request_QualificationState" CHECK
                    ("QualificationState"='incomplete' OR "QualificationState"='stale' OR
                     "QualificationState"='duplicate' OR "QualificationState"='not_qualified' OR
                     "QualificationState"='qualified' OR "QualificationState"='unreviewed');
                ALTER TABLE "RequestQualificationAudit" DROP CONSTRAINT "CK_RequestQualificationAudit_DuplicateCount";
                ALTER TABLE "RequestQualificationAudit" ADD CONSTRAINT "CK_RequestQualificationAudit_DuplicateCount"
                    CHECK ("DuplicateCount">=(0));
                ALTER TABLE "RequestQualificationAudit" DROP CONSTRAINT "CK_RequestQualificationAudit_NewState";
                ALTER TABLE "RequestQualificationAudit" ADD CONSTRAINT "CK_RequestQualificationAudit_NewState" CHECK
                    ("NewState"='incomplete' OR "NewState"='stale' OR "NewState"='duplicate' OR
                     "NewState"='not_qualified' OR "NewState"='qualified' OR "NewState"='unreviewed');
                ALTER TABLE "RequestQualificationAudit" DROP CONSTRAINT "CK_RequestQualificationAudit_Reason";
                ALTER TABLE "RequestQualificationAudit" ADD CONSTRAINT "CK_RequestQualificationAudit_Reason"
                    CHECK ("NewState"='qualified' OR nullif(ltrim(rtrim("Reason")),'') IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrading exact datetime2(7) values to PostgreSQL timestamp would lose precision.");
        }
    }
}
