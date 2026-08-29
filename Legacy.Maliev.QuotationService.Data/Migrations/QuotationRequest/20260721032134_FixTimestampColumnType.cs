using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.QuotationService.Data.Migrations.QuotationRequest;

/// <inheritdoc />
public partial class FixTimestampColumnType : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: false);

    private static void ConvertUtcTimestampColumns(MigrationBuilder migrationBuilder, bool toTimestampWithoutTimeZone)
    {
        var targetType = toTimestampWithoutTimeZone
            ? "timestamp without time zone"
            : "timestamp with time zone";
        var defaultSql = toTimestampWithoutTimeZone
            ? "CURRENT_TIMESTAMP AT TIME ZONE 'UTC'"
            : "CURRENT_TIMESTAMP";

        foreach (var (table, column) in UtcTimestampColumns)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" DROP DEFAULT;
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" TYPE {targetType}
                USING "{column}" AT TIME ZONE 'UTC';
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" SET DEFAULT {defaultSql};
                """);
        }
    }

    private static readonly (string Table, string Column)[] UtcTimestampColumns =
    [
        ("RequestFile", "ModifiedDate"),
        ("RequestFile", "CreatedDate"),
        ("Request", "ModifiedDate"),
        ("Request", "CreatedDate")
    ];
}
