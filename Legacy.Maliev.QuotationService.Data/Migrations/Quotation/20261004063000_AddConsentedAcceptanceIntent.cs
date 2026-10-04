using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Legacy.Maliev.QuotationService.Data.Migrations.Quotation;

[DbContext(typeof(QuotationDbContext))]
[Migration("20261004063000_AddConsentedAcceptanceIntent")]
public sealed class AddConsentedAcceptanceIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GoogleAnalyticsOutbox",
            columns: table => new
            {
                ID = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                QuotationID = table.Column<int>(type: "integer", nullable: false),
                EventKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                EventName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SourceRequestID = table.Column<int>(type: "integer", nullable: true),
                SourceJourneyID = table.Column<Guid>(type: "uuid", nullable: true),
                ClientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                OccurredUtc = table.Column<string>(type: "text", nullable: false),
                NextAttemptUtc = table.Column<string>(type: "text", nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                LeaseUntilUtc = table.Column<string>(type: "text", nullable: true),
                SentUtc = table.Column<string>(type: "text", nullable: true),
                FailedUtc = table.Column<string>(type: "text", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_GoogleAnalyticsOutbox", x => x.ID));
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_EventKey", "GoogleAnalyticsOutbox", "EventKey", unique: true);
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_QuotationID", "GoogleAnalyticsOutbox", "QuotationID");
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_SourceRequestID", "GoogleAnalyticsOutbox", "SourceRequestID");
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_SourceJourneyID", "GoogleAnalyticsOutbox", "SourceJourneyID");
        migrationBuilder.CreateIndex("IX_GoogleAnalyticsOutbox_DeliveryDue", "GoogleAnalyticsOutbox", new[] { "SentUtc", "FailedUtc", "NextAttemptUtc", "LeaseUntilUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Dropping consented first-acceptance payloads requires a reviewed compensating migration.");
}
