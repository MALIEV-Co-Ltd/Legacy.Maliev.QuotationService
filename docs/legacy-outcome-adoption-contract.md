# Legacy quotation outcome adoption contract

`LegacyQuotationOutcomeAdopter` is the PostgreSQL-only consumer boundary for the
reconciled `dbo.QuotationOutcomeOutbox` inventory. It accepts the complete source
inventory plus the source's next identity value; it does not connect to SQL Server
and never derives an outcome from `Quotation.Accepted`.

The DataMigration producer remains responsible for reading the signed exact shadow,
proving that the batch is complete, and supplying these source columns without
transformation:

- `ID`
- `EventKey`
- `QuotationID`
- nullable `SourceRequestID`
- nullable `SourceJourneyID`
- `AcceptedUtc`
- `AcceptanceOrigin`

Adoption preserves `ID`, all values and nulls, and the next identity value. PostgreSQL
stores timestamps at microsecond precision, so the canonical row stores the final
SQL Server `datetime2` sub-microsecond tick in `AcceptedUtcSubMicrosecondTicks` and
reconstructs the exact source timestamp for parity checks. Replays must supply the
complete inventory; a missing, conflicting, or extra canonical row fails closed.

The operation serializes concurrent adopters with a transaction-scoped PostgreSQL
advisory lock. Inserts and sequence positioning share one transaction, so a database
failure commits neither. No Google Analytics credentials, Measurement Protocol
client, outbox sender, or analytics worker belongs to this boundary.
