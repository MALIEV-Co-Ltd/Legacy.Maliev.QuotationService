# Root mutation and restrictive removal acceptance

The existing normal-host suites prove ordinary root PUT, acceptance-promotion rejection,
late stale cache readers and accepted-outcome retention after root deletion. This bundle
adds nine complementary actual Production Program/DI, signed-JWT/live IAM client transport,
PostgreSQL 18 and Redis cases. A supported controlled clock fixes storage precision and
advances one minute before the versioned full PUT.

The PUT checks source financial/scalar/text fields, computed QuotedAmount, preserved creation
time, an advanced ModifiedDate, actual Redis invalidation, and stale-version 409 with unchanged
persisted fields and cache. It creates no acceptance or analytics intent. InvoiceId is an external
scalar reference; this fixture does not validate a real invoice or prove Accounting behavior.
The empty draft root delete checks 204, real cache removal, persisted absence, repeated 404,
missing/null update behavior and refusal to resurrect the root from a late stale Redis value.

Each existing child foreign key is exercised separately through actual HTTP child creation:
order item, order link and file metadata. Root deletion remains restricted, returning the
current generic 500 and preserving root, child and cache atomically. The response must omit
SQL state, constraint/provider details and synthetic file metadata. This characterizes the
existing policy; it introduces neither cascade deletion nor a new conflict/error contract.
The four update/delete denial cases distinguish missing JWT from controlled live IAM rejection,
assert exact live-check entry counts and preserve persisted root metadata and Redis.

Source `5fac706a7983a6d359b39acbd670e6800afe020e` is a root with no parents. Its
`Maliev.QuotationService.Api/Controllers/QuotationsController.cs` blob
`f7de54587ff77294289ed9335b19f0a981d625d0` differs from final checkpoint
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` blob `a598e44d3d39d8c7cc882dca2552cfde97af81f9`.
The final source UpdateQuotation and DeleteQuotation tests were inspected, including
financial field updates and rejection of acceptance promotion. The final source
`Maliev.QuotationService.Data/Database/QuotationContext/QuotationContext.cs` configures
required order-item/link relationships with ClientSetNull and the same file relationship
policy. Target NoAction constraints are tested on PostgreSQL; no original SQL Server runtime
equivalence is claimed. Original-source configuration/data remains untouched.

Only tests, fixture permissions/resource assertions and this note change. Runtime, schema,
routes, DTOs, dependency pins and default feature flags remain unchanged. Cross-service invoice/
order/provider authorization, immutable financial aggregate coherence, durable accepted-fact
deletion policy and complete historical SHA/path or issue closure remain separate obligations.
The file case uses metadata only and performs no cloud upload/delete. Controlled IAM responses
are fixture boundaries, not production grants. Hosted Release/full/raw coverage/format/audit/
security/joined and protected/exact-main acceptance are pending. No local SDK/Docker or deploy.
