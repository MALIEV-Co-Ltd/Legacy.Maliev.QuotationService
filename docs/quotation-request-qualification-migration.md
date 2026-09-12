# Quotation-request qualification migration

Source commit `5ee4155b6af6469c985d9ba24007da753f58e6d8` is migrated into the
.NET 10 quotation-request boundary in issue #41.

The PostgreSQL-only implementation provides:

- server-owned `request-{id}` transaction identifiers, including deterministic
  backfill for existing migrated requests;
- six constrained qualification states and an optimistic integer version;
- employee-workspace update and PII-free receipt endpoints protected by live
  quotation-request update/read permissions;
- immutable audit rows containing attribution identifiers, transition data,
  actor identity, and no customer contact fields;
- request-scoped idempotency enforced by a PostgreSQL advisory transaction lock
  and a unique `(RequestID, IdempotencyKey)` index;
- database check constraints for state, duplicate count, and required reasons.

The source SQL Server deployment script is represented by an additive EF Core
PostgreSQL migration. It does not add a SQL Server runtime/provider path and is
not applied by this PR. Controller, model, migration-upgrade, replay, version,
and audit behavior are covered by the affected tests.

No deployment, database write, traffic change, or source-repository mutation is
part of this migration slice.
