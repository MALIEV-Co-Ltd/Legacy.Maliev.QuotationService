using Legacy.Maliev.QuotationService.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation;

[DbContext(typeof(QuotationDbContext))]
[Migration("20260927050000_PreserveHistoricalAcceptedUtc")]
public sealed class PreserveHistoricalAcceptedUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $migration$
            DECLARE
                accepted_type text;
                accepted_nullable text;
            BEGIN
                SELECT data_type, is_nullable INTO accepted_type, accepted_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Quotation' AND column_name = 'AcceptedUtc';

                IF accepted_type IS NULL OR accepted_nullable <> 'YES' THEN
                    RAISE EXCEPTION 'Quotation.AcceptedUtc historical upgrade requires an existing nullable column';
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = 'Quotation'
                      AND column_name = 'AcceptedUtcLegacyTimestamp'
                ) THEN
                    RAISE EXCEPTION 'Quotation.AcceptedUtc historical upgrade found an existing legacy timestamp column';
                END IF;

                IF accepted_type = 'text' THEN
                    RETURN;
                END IF;

                IF accepted_type <> 'timestamp without time zone' THEN
                    RAISE EXCEPTION 'Quotation.AcceptedUtc historical upgrade does not support column type %', accepted_type;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM "Quotation"
                    WHERE "AcceptedUtc" IS NOT NULL
                      AND (NOT isfinite("AcceptedUtc")
                           OR "AcceptedUtc" < TIMESTAMP '0001-01-01 00:00:00'
                           OR "AcceptedUtc" > TIMESTAMP '9999-12-31 23:59:59.999999')
                ) THEN
                    RAISE EXCEPTION 'Quotation.AcceptedUtc contains a value outside the supported date range';
                END IF;

                ALTER TABLE "Quotation" RENAME COLUMN "AcceptedUtc" TO "AcceptedUtcLegacyTimestamp";
                ALTER TABLE "Quotation" ADD COLUMN "AcceptedUtc" text NULL;
                UPDATE "Quotation"
                SET "AcceptedUtc" = to_char("AcceptedUtcLegacyTimestamp", 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0'
                WHERE "AcceptedUtcLegacyTimestamp" IS NOT NULL;
            END
            $migration$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Historical AcceptedUtc precision conversion is not reversible; use an explicitly reviewed compensating migration.");
}
