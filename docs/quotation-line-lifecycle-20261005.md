# Child line lifecycle acceptance boundary

This bounded issue #71 slice exercises the accepted Program's line update and deletion routes with normal signed JWT validation, the actual live IAM client with controlled external transport, and disposable PostgreSQL/Redis. It extends the existing draft aggregate fixture; no runtime, DTO, route or schema change is proposed.

The source mapping is commit `5fac706a7983a6d359b39acbd670e6800afe020e`, `Maliev.QuotationService.Api/Controllers/OrderItemsController.cs`: create, update and delete mutate only the child row. The current PostgreSQL contract keeps line subtotal computed from quantity and unit price and uses its own ModifiedDate concurrency token.

The five new cases require a successful line update with the original child version, computed decimal readback, rejection of an old-version overwrite without persisted changes, deletion and subsequent 404, and authentication/live permission denial for update/delete without child mutation.

Parent ModifiedDate remains unchanged across child writes. This explicitly characterizes a producer limitation; the parent version protects scalar edits and does not prove a coherent parent/item financial snapshot. Accounting issue #53 must retain that boundary. The separately read document snapshot, aggregate versioning, cross-attempt original-version continuity and global invoice/order saga atomicity are not established by these cases.

The actual-host JWT is fixture-signed; this is not a claim of a new Auth producer or durable session lifecycle proof. Historical joined producer pins and assertions are untouched. No whole source commit or migration ledger closure follows from this narrow proof.

Validation is pending hosted exact-head Release build, focused/full suites, generated-inclusive raw coverage per owned assembly, formatting, vulnerability/security checks and retained TRX/XML inspection. No local SDK jobs, production providers or persistent production data are used.
