# Request journey migration audit

Service: Legacy.Maliev.QuotationService. Target: main.
Source owner: `7ebe7e4`, inspected at `5ac7d045c51194edd9e64d8564f1b726b001be34`.
Tracking: [issue #35](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/35).

Migration: `20260906120000_AddRequestJourneyId`.

- Forward operations add nullable PostgreSQL `uuid` Request.JourneyId and the source filtered nonunique index. No existing values are rewritten, and no cross-database foreign key is introduced.
- Existing records remain null until a separately reviewed production-data migration restores attribution. Adding the column is not evidence of complete data parity.
- Destructive rollback is prohibited: Down throws instead of dropping attribution. Use a reviewed forward repair; rolling application code back does not require dropping this optional column.
- EF migration history guards normal repeated execution. Raw Up SQL is not intended for unguarded manual replay.
- No custom xmin column or concurrency-policy change is introduced.
- Index creation can briefly lock the table; production scheduling/capacity review is still required. No production or local review database is changed by this code slice.

Create captures JourneyId; update preserves the existing value, matching source. Idempotency fingerprints bind nonnull journeys without changing existing null-journey fingerprints. API null omission and nullable Done remain unchanged.

Consumer gate: Web's strict response DTO must accept optional JourneyId before service rollout. Dedicated CNC request creation remains engineering review only, not order/invoice/payment provisioning.

Verdict: additive forward migration; production application remains gated on independent data reconciliation and owner-approved deployment. Validation results belong in the linked issue/PR after execution.

## Local validation (2026-09-07)

- Release solution build with `-p:MalievWorkspaceRoot=B:/maliev-legacy -p:BuildProjectReferences=false --no-restore`: zero warnings/errors. Existing shared dependency outputs were reused to avoid concurrent-worktree collisions.
- Initial focused journey/idempotency/PostgreSQL suite: 33 passed; initial full suite: 173 passed.
- Added populated pre-Journey upgrade coverage; rebuilt with zero warnings/errors. Final focused journey/idempotency suite: 10 passed; final full suite: 174 passed, zero failed/skipped.
- The upgrade test migrates a disposable PostgreSQL 18 database to the prior migration, inserts a synthetic Thai/English request, applies the additive migration, and checks unchanged ID/text/timestamps/nullable status, null journey on the existing row, the PostgreSQL uuid column/filtered nonunique index, and a new attributed insert with advancing identity.
- `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore`: passed.
- `dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore`: no known vulnerable packages across all six projects.
- `gitleaks dir . --redact --no-banner`: no leaks found.
- No production database, local Aspire review database, cloud infrastructure, deployment, or source repository was changed. Testcontainers used only disposable synthetic databases. Production-data reconciliation is not implied by these tests.
