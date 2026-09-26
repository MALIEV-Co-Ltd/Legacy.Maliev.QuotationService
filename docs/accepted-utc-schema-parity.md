# Quotation.AcceptedUtc schema parity

The fresh QuotationService schema maps nullable `Quotation.AcceptedUtc` to PostgreSQL
`text` using `ExactDateTime2Text.NullableConverter`. The wire value is the invariant
`yyyy-MM-ddTHH:mm:ss.fffffff` form, retaining all seven SQL Server `datetime2(7)`
fractional digits. This matches the DataMigration exact-23 target manifest.

The existing `20260829074943_AddAcceptedQuotationOutcome` migration's additive
`AcceptedUtc` column definition was corrected before application to the active
targets. The reviewed production schema observation lists this column as absent.
The identity-checked persistent-local Quotation catalog already has a nullable
`text` column and no `__EFMigrationsHistory` relation. Neither target was written
to for this change; PostgreSQL 18 tests use disposable containers.

**Historical installation blocker:** A database that already applied the earlier
version of `20260829074943_AddAcceptedQuotationOutcome` has a PostgreSQL
`timestamp without time zone` column. EF records the migration ID, not a content
checksum, so this corrected migration will not convert that installation. Do not
deploy this runtime there or mark it schema-aligned. First inspect its catalog and
migration history, then design and separately review a data-preserving conversion
with a seventh-digit limitation for values already stored at microsecond precision.
This slice deliberately contains no `AlterColumn`, `DropColumn`, production DDL,
or historical timestamp upgrade path.

## Migration audit report

**Service:** Legacy.Maliev.QuotationService

**Migration:** `20260829074943_AddAcceptedQuotationOutcome.cs`

**Target branch:** `main`

- Dangerous operations: none in the changed `AcceptedUtc` operation; it adds a
  nullable `text` column. No type-changing `AlterColumn` or drop is introduced.
- Data-loss risk: none for the fresh-schema addition. Previously stored timestamp
  values are outside this slice and remain blocked as described above.
- Indexing: no new index is needed for this column; no index was removed.
- Idempotency: EF migration history prevents reapplication, but this edited
  historical migration must not be used as a conversion on an installation where
  the prior version was applied.
- `xmin`: no custom `xmin` column is created.

**Verdict:** Approved for the verified unapplied fresh-schema path only; blocked
for any already-applied timestamp installation pending a separate reviewed
conversion.
