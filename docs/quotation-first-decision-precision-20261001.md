# First-decision replay-version precision — child89

Protected base `cb9601af10e5576f57955159b5f0ac0e7f84dc3f`, branch `codex/quotation-first-decision-precision-20261001`. Root owns this workspace/output. Child88 is independently tested and merged; its post-main CI remains a separate acceptance gate. This document and the new fixed-clock HTTP fixture initially change no runtime, old tests, schema, authentication or CI.

## Independent genuine RED

Direct Release test-project build with warnings as errors: zero warnings/errors. Actual normal Production Program, RS256, real disposable PostgreSQL18/Redis and registered downstream Order/service-token clients are retained through the accepted InvoiceConsumerFixture. External IAM/Auth/Order HTTP transports remain controlled, not live authority/provider proof. Only TimeProvider is replaced by an exact fixed clock to make all ten precision classes deterministic.

`dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-invoice-consumer-proof-20261001/.dependencies --filter FullyQualifiedName~QuotationFirstDecisionPrecisionHttpTests --logger trx --results-directory TestResults/root-producer89-red` terminated with ten executed, nine genuine RED, one microsecond-aligned GREEN, zero skips. TRX `TestResults/root-producer89-red/natth_MALIEV-31USFIV_2026-10-01_17_53_37_net10.0.trx`.

Each first undecided employee acceptance reaches Order503, commits exactly one root/outcome, and makes four same-key Order attempts. Fresh PostgreSQL observations verify Accepted=true, InvoiceID/DecisionOrderVersion=NULL, microsecond ModifiedDate and an exact acceptance timestamp reconstructed from outcome time plus retained submicrosecond ticks. Retry reaches200 and preserves the same immutable outcome ID/time. Only the fifth Order key changes for remainders1–9; remainder0 passes. No unrelated assertion failure or test setup error is classified as product RED.

## Proposed bounded policy

ApplyDecisionAsync assigns its ModifiedDate at PostgreSQL's durable microsecond precision before projecting the first outbound replay key. Reuse the existing internal TruncateToMicroseconds helper; do not globally alter Now(), create/update/expiry timestamp policies or the Order key builder. Preserve exact AcceptedUtc capture and existing outcome residual ticks. Existing historical DecisionOrderVersion bindings are never normalized/rebound; late-attachment and ordinary-edit child88 guarantees remain intact. Decline/reaccept naturally use the next committed decision version, while optimistic-concurrency/rollback guards stay unchanged.

This aligns the first returned decision version with what the database actually commits, rather than introducing new schema/authority or trying to recover discarded digits after a retry. It cannot retroactively reconstruct a previously emitted unbound 100ns key from older writer output. No runtime has been deployed, and no live production repair/data mutation is authorized or claimed.

Require independent policy review, genuine RED→GREEN, fresh zero-warning Release build, focused tests including child88/decision/concurrency/outcome controls, full accepted503 plus ten new cases, format/static/security/coverage and protected-head/post-main CI. #68 employee-intent authority and the broader source owners remain OPEN. Source c821605b7ecde5f79d01966888b116defb10650d parent92e8ad50c5d33f7458e86bf9cd33eabbdb1eb220 and6c1a4921b3524dd575cab5b1f4aa5774ad9b8997 parent73f045c0f950953bc1ef9b8397e1a16d05dbe543 are partially owned; this child does not resolve those whole SHAs. Original held89 nondeterministic RED artifacts remain intact.

## Subsequent reviewed repair

Independent read-only reviewer traced ApplyDecisionAsync's tracked post-save response versus the fresh unchanged-decision response and QuotationDecisionWorkflow's tick-based key. The decision-local assignment is approved: historical binding is captured before the change; Kind remains Unspecified; raw `now` still feeds acceptance provenance/residual ticks. One runtime assignment now canonicalizes ModifiedDate only. The new test additionally asserts the literal canonical key format after its retained first/retry equality assertion. No historical assertion was weakened, and no existing test changed. Fresh validation is pending; this is not an acceptance claim.

### Rapid-cycle correction to the initial proposal

The initial one-line truncation passed focus28, but root added a real accept→decline→accept sequence within one microsecond. It reached three200 decisions and one retained immutable outcome, then failed three-distinct-key expectation with only two keys (`TestResults/root-producer89-cycle-red/natth_MALIEV-31USFIV_2026-10-01_18_03_36_net10.0.trx`). A separate exhausted version test also failed against the initial policy (actual200 versus desired fail-closed500; `root-producer89-overflow-red/...18_05_55...`). Neither RED is waived or counted as acceptance.

After a second independent read-only policy review, decision-local NextDecisionVersion uses floor(now) if it exceeds the previous ModifiedDate; otherwise floor(previous)+one microsecond. Overflow raises fixed OverflowException BEFORE save/Order. Early unchanged-decision replay never advances. Late historical binding is still captured before assigning the new version. Raw now/outcome residual precision remains untouched. Additional actual HTTP controls cover a stalled/backward clock, overflow/no durable mutation and distinct rapid-cycle keys.

The first monotonic focus executed32 cases:31 passed and the exhausted-version case failed500-versus400. The pinned Defaults middleware intentionally maps InvalidOperationException to400. Root therefore uses arithmetic OverflowException for server-side version exhaustion without changing shared middleware or weakening the expected500/no-save/no-Order assertions. Failed evidence remains in `TestResults/root-producer89-monotonic-focus`.

Explicit contract trade-off: this per-quotation decision concurrency/replay version may lead wall clock by microseconds or retain prior clock skew; immutable AcceptedUtc/outcome remains the event-time authority. No global monotonicity claim is made for ordinary update/expiry paths, which remain unchanged. Global normalization and key-builder/schema changes remain rejected. Latest candidate is fourteen new cases plus all accepted503; final full517/coverage/static/protected CI are pending.

## Root final local acceptance

Direct Release test-project build with warnings as errors passed0warnings/0errors. Focus32passed0failed0skipped (`TestResults/root-producer89-final-focus/natth_MALIEV-31USFIV_2026-10-01_18_16_34_net10.0.trx`). Full517passed0failed0skipped in2m47s (`TestResults/root-producer89-final-full/natth_MALIEV-31USFIV_2026-10-01_18_18_28_net10.0.trx`), preserving all503 prior tests plus14new cases. Exact final helper and OverflowException mapping independently reviewed read-only.

Unexcluded Cobertura SHA256 `CCD2890986AAD1119733055CD4A8950A6F4C3BD675D3128C8C81FDF208F20C58`: owned handwritten5201/5485=94.82%, API432/469=92.11%; generated obj/external dependency lines are explicitly reported separately, no source exclusions or threshold changes. Existing80percent checker passes. Whole-solution format verification, actionlint, allfive coverage-checker behavior tests, three scoped redacted secret scans and diffcheck pass. No old test assertion was changed. Protected-head/post-main CI, merge and issue completion remain pending; this local evidence is not deployed/Aspire or whole-source-owner acceptance.
