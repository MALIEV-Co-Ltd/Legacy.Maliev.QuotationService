# Native order-decision consumer acceptance

This bundle exercises the existing target OrderDecisionClient and QuotationDecisionWorkflow
required by issue #74. No runtime, route, DTO, schema, package pin or default changes apply.
Production Program/DI, normal signed caller JWT, the real live IAM client and workload token
provider remain registered. Only IAM/login transports are controlled by the existing fixture.
The order transport uses actual Kestrel/Sockets HTTP on an ephemeral loopback-only endpoint,
with the production service-discovery/authentication/resilience pipeline retained. The fixture
overrides only its address and HttpClient timeout through supported typed-client configuration.

Seven cases exercise named accepted/declined POST routes, explicit operation keys, 201/409/404
mapping, native HttpClient deadline with an uncanceled caller, and caller cancellation after
the real socket request arrives. Two actual route/PostgreSQL cases persist a first acceptance,
complete one linked order, return partial 503 or dependency 409, then recover through the
same ordered per-order operation keys. They assert one accepted outcome, no consent-gated
GA intent, preserved financial/created fields and unchanged persisted decision version on retry.
Standard resilience may retry a 503 before the bounded HttpClient deadline; every captured
retry must carry the same operation key. A 503 response followed by a deadline is not claimed
as a new private diagnostic category or an exhaustion-specific result.

The loopback endpoint observes the outbound workload subject and operation key; it does not
verify JWT signatures or grant real OrderService authorization. Seeded source attribution
references are historical scalar fixture data, not persisted request qualification evidence.
This is actual target consumer behavior, distinct from a producer-only contract test, and
still excludes live Auth/IAM/Order/Accounting/Web systems and global financial saga guarantees.

Source root `5fac706a7983a6d359b39acbd670e6800afe020e` (no parents) has quotation CRUD with
the Accepted field, but no named OrderDecisionClient transport. Its controller blob
`f7de54587ff77294289ed9335b19f0a981d625d0` differs from final checkpoint
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f` blob `a598e44d3d39d8c7cc882dca2552cfde97af81f9`.
Later first-acceptance/outcome source portions have their separate accepted PR103 mappings.
No exact native transport equivalence or whole source SHA/issue closure follows from this bundle.
The shared private observer consumer remains residual; producer acceptance alone does not
prove this pinned consumer uses its newer APIs. No shared dependency upgrade is made.

Hosted Release build, focused/full tests, raw coverage, format, audit, security, joined authority
and protected/exact-main acceptance are pending. No local SDK/Docker execution, deployment,
persistent-data operation, original-source write or provider/customer effect is authorized here.
