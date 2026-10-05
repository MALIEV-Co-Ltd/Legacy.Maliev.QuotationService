# Order-link lifecycle acceptance

Source `5fac706a7983a6d359b39acbd670e6800afe020e` is a root commit without parents.
`Maliev.QuotationService.Api/Controllers/OrdersController.cs` has Git blob
`81c3636d33b5126b03eaebe8c0cab828bf1e537b` at both that source and final checkpoint
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f`, independently inspected from the read-only mirror.
The five original `Maliev.QuotationService.Tests/Orders/*_UnitTest.cs` files belong to this bounded source portion.

The target Production routes preserve absolute create Location, PascalCase link metadata,
collection/detail reads, the query-string `id` update route, CreatedDate preservation,
ModifiedDate advancement, null-body 400, and missing/deleted child 404 responses.
Separate creates of the same quotation/order pair remain distinct rows without an idempotency key.
Updating a child may move it to another existing quotation. Empty collections return 404.
Deletion removes only the link. Link writes do not advance either parent's ModifiedDate;
this is a source behavior characterization, not aggregate financial concurrency protection.

Four new actual-host cases use Production Program/DI, signed JWT, the real live IAM client
with controlled HTTP transport, PostgreSQL 18 and Redis. A supported FakeTimeProvider
sets exact persisted timestamps and advances a minute before updates, avoiding PostgreSQL
microsecond-rounding ambiguity while asserting exact CreatedDate and ModifiedDate behavior. Every link route is checked for
missing JWT and live IAM rejection with unchanged persisted metadata and parent version.
The existing aggregate fixture adds its missing delete permission and accepts the already
supported resource-scoped order read/write permissions in its IAM transport assertions.
No runtime, route, DTO, schema, package pin or default configuration changes are made.

Existing create/replay, zero-id and missing-parent cases remain part of the full suite.
Native OrderService transport/deadline/private diagnostics, live providers, financial aggregate
coherence, Web/Intranet/Accounting integration and complete issue/source SHA closure remain residual.
Hosted build, full tests, raw coverage, format, audit, scans and exact-main acceptance are pending.
No local SDK execution or deployment applies.
